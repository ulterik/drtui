using System.Text.Json.Nodes;
using Drtui;
using Json.Schema;
using Xunit;

namespace Drtui.Tests;

public class RecoveryPlanTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static Plan Example => PlanLoader.Load(Path.Combine(Root, "examples", "coolify-s3-cloudflare", "plan.drp.yaml"));

    [Fact]
    public void ExplicitCliRequirementsAreGenericDeduplicatedAndReplaceSourceInference()
    {
        var plan = new Plan(JsonNode.Parse("""
            {"providers":{
              "host":{"kind":"infrastructure","source":"file://terraform-github-provider","requiredClis":["hcloud","cf"]},
              "storage":{"kind":"storage","source":"file://other","requiredClis":["cf","aws"]},
              "remote":{"kind":"application","source":"file://github-compose","requiredClis":[]}
            },"steps":[]}
            """)!.AsObject());
        var checkedTools = new List<string>();
        var checks = new Session(plan).Check(tool => { checkedTools.Add(tool); return tool == "cf"; });
        Assert.Equal(new[] { "aws", "cf", "hcloud" }, checkedTools);
        Assert.Contains(checks, check => check.Label == "CLI hcloud" && check.Error is not null);
        Assert.Contains(checks, check => check.Label == "CLI cf" && check.Error is null);
        Assert.DoesNotContain(checks, check => check.Label is "CLI terraform" or "CLI gh" or "CLI docker");
    }

    [Theory]
    [InlineData("[\"cf\",\"cf\"]")]
    [InlineData("[\"npm install cf\"]")]
    [InlineData("[\"../cf\"]")]
    [InlineData("[\"cf;whoami\"]")]
    [InlineData("[\"\"]")]
    [InlineData("\"cf\"")]
    public void SchemaRejectsInvalidCliDeclarations(string requirements)
    {
        var document = Example.Document.DeepClone().AsObject();
        document["providers"]!["cloudflare"]!["requiredClis"] = JsonNode.Parse(requirements);
        var path = Path.Combine(Path.GetTempPath(), $"drtui-cli-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, document.ToJsonString());
            Assert.Throws<InvalidDataException>(() => PlanLoader.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RecoveryRemainsBlockedUntilRealBackupInventoryIsConfigured()
    {
        var checks = new Session(Example).Check(_ => true);
        foreach (var name in new[] { "r2_endpoint", "coolify_state_uri", "coolify_state_sha256",
            "recovery_manifest_uri", "recovery_manifest_sha256", "cloudflare_account_id", "cloudflare_zone_id" })
            Assert.Contains(checks, check => check.Label == "input " + name && check.Error == "required input is missing");
        Assert.Contains(checks, check => check.Label == "provider host" && check.Error is not null);
    }

    [Fact]
    public void PrimaryRecoveryRequiresNoAwsAccountCliOrProfile()
    {
        var session = new Session(Example);
        var checks = session.Check(tool => tool != "aws", _ => "present");
        Assert.Equal(new[] { "cf", "coolify", "gh" },
            checks.Where(check => check.Label.StartsWith("CLI ", StringComparison.Ordinal))
                .Select(check => check.Label[4..]));
        Assert.All(checks.Where(check => check.Label.StartsWith("CLI ", StringComparison.Ordinal)),
            check => Assert.Null(check.Error));
        Assert.All(session.Vaults.Values, vault => Assert.Equal("cloudflare-api-token", vault.AuthMethod));
        Assert.Contains(checks, check => check.Label == "hosting hosting" && check.Error is not null);
        Assert.DoesNotContain("terraform", session.Plan.Document.ToJsonString());
        Assert.DoesNotContain(session.Plan.Providers, provider => provider.Value?["config"]?["profile"] is not null);
        Assert.DoesNotContain("aws", string.Join(" ", session.Plan.Providers.Select(provider => (string?)provider.Value?["source"])));
        Assert.Equal("${{ steps.fetch_secrets.outputs.r2_access_key_id }}",
            (string?)session.Plan.Providers["s3"]?["config"]?["accessKeyId"]);
        Assert.Equal("${{ steps.fetch_secrets.outputs.r2_secret_access_key }}",
            (string?)session.Plan.Providers["s3"]?["config"]?["secretAccessKey"]);
    }

    [Fact]
    public void ExplicitEmptyRequirementsCannotBypassKeyvaultCliPrerequisites()
    {
        var plan = Example;
        plan.Providers["recovery_secrets"]!["requiredClis"] = new JsonArray();
        plan.Providers["s3"]!["requiredClis"] = new JsonArray();
        plan.Providers["cloudflare"]!["requiredClis"] = new JsonArray();
        Assert.Contains(new Session(plan).Check(_ => false),
            check => check.Label == "CLI cf" && check.Error is not null);
    }

    [Fact]
    public void RecoveryOrdersDatabaseAndTunnelGatesAndRestoresExactPriorRoutes()
    {
        var plan = Example;
        var steps = plan.Steps.ToDictionary(step => (string)step!["id"]!, step => step!);
        var order = PlanLoader.ValidateDependencies(plan.Steps);
        foreach (var (before, after) in new[]
        {
            ("verify_target", "bootstrap_runtime"), ("fetch_compose", "restore_databases"),
            ("verify_runtime", "restore_databases"), ("verify_databases", "deploy_services"),
            ("verify_applications", "create_tunnel"), ("configure_tunnel", "start_connector"),
            ("verify_tunnel", "snapshot_routes"), ("snapshot_routes", "sync_dns"), ("sync_dns", "verify_dns")
        })
        {
            Assert.True(Array.IndexOf(order, before) < Array.IndexOf(order, after));
            var pending = new Stack<string>();
            var ancestors = new HashSet<string>(StringComparer.Ordinal);
            pending.Push(after);
            while (pending.TryPop(out var id))
                foreach (var dependency in steps[id]["needs"]?.AsArray() ?? new JsonArray())
                {
                    var needed = dependency!.GetValue<string>();
                    if (ancestors.Add(needed)) pending.Push(needed);
                }
            Assert.Contains(before, ancestors);
        }
        Assert.All(order.Skip(Array.IndexOf(order, "provision_target")),
            id => Assert.Equal("recovery", (string?)steps[id]["transaction"]));

        Assert.Equal("cloudflare/syncTunnelRoutes", (string?)steps["sync_dns"]["uses"]);
        Assert.Equal("cloudflare/restoreRoutes", (string?)steps["sync_dns"]["rollback"]?["uses"]);
        Assert.Equal("${{ steps.snapshot_routes.outputs.previous_records }}",
            (string?)steps["sync_dns"]["rollback"]?["with"]?["records"]);
        Assert.False((bool)steps["bootstrap_runtime"]["with"]!["startTunnel"]!);
        Assert.False((bool)steps["restore_databases"]["with"]!["overwriteExistingVolumes"]!);
        Assert.True((bool)steps["restore_databases"]["with"]!["requireStoppedArchiveVolumes"]!);
        Assert.Equal("secret", (string?)steps["create_tunnel"]["outputs"]?["connector_token"]?["type"]);
        Assert.True((bool)steps["create_tunnel"]["outputs"]!["connector_token"]!["sensitive"]!);
        Assert.DoesNotContain("targetIp", plan.Document.ToJsonString());
        Assert.DoesNotContain("previous_primary_ip", plan.Inputs.Select(input => input.Key));
    }

    [Fact]
    public void RecoveryManifestAcceptsVolumeAndNativeBackupsWithoutChoosingAnEngine()
    {
        var inventory = Inventory();
        Assert.True(ManifestIsValid(inventory));
        var backup = inventory["workloads"]![0]!["database"]!["backups"]![0]!;
        backup["format"] = "database-native";
        backup.AsObject().Remove("volume");
        backup.AsObject().Remove("consistency");
        backup["restoreAdapter"] = "custom-database/restore";
        Assert.True(ManifestIsValid(inventory));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("consistency")]
    [InlineData("volume")]
    public void VolumeBackupRequiresExplicitFormatConsistencyAndMapping(string missing)
    {
        var inventory = Inventory();
        inventory["workloads"]![0]!["database"]!["backups"]![0]!.AsObject().Remove(missing);
        Assert.False(ManifestIsValid(inventory));
    }

    [Theory]
    [InlineData("../compose.yaml")]
    [InlineData("deploy/../../compose.yaml")]
    [InlineData("/compose.yaml")]
    [InlineData("C:\\compose.yaml")]
    public void ManifestRejectsEscapingComposePaths(string path)
    {
        var inventory = Inventory();
        inventory["workloads"]![0]!["source"]!["composeFiles"]![0] = path;
        Assert.False(ManifestIsValid(inventory));
    }

    [Fact]
    public void ManifestRejectsMutableCommitsMissingChecksumsAndUnspecifiedNativeAdapters()
    {
        var inventory = Inventory();
        inventory["workloads"]![0]!["source"]!["commit"] = "main";
        Assert.False(ManifestIsValid(inventory));
        inventory = Inventory();
        var backup = inventory["workloads"]![0]!["database"]!["backups"]![0]!;
        backup["artifact"]!.AsObject().Remove("sha256");
        Assert.False(ManifestIsValid(inventory));
        inventory = Inventory();
        inventory["workloads"]![0]!["database"]!["backups"]![0]!["format"] = "database-native";
        Assert.False(ManifestIsValid(inventory));
    }

    private static bool ManifestIsValid(JsonObject inventory)
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(Root, "schemas", "recovery-manifest.schema.json")));
        return schema.Evaluate(inventory).IsValid;
    }

    private static JsonObject Inventory() => JsonNode.Parse($$$"""
        {
          "apiVersion":"drtui.io/recovery-inventory/v1alpha1",
          "workloads":[{
            "id":"database",
            "kind":"database",
            "source":{"repository":"example/recovery","commit":"{{{new string('a', 40)}}}","composeFiles":["deploy/compose.yaml"],"service":"db"},
            "database":{
              "engine":"custom-database",
              "image":"example/database@sha256:{{{new string('b', 64)}}}",
              "backups":[{"format":"volume-archive","volume":"db_data","consistency":"stopped",
                "artifact":{"uri":"s3://recovery/immutable/db.tar.zst","sha256":"{{{new string('c', 64)}}}"}}],
              "verification":{"adapter":"custom-database/readOnly","check":"schema-version"}
            }
          }],
          "routes":[{"hostname":"app.example.com","service":"http://127.0.0.1:8080","healthPath":"/health","expectedStatus":200}]
        }
        """)!.AsObject();
}
