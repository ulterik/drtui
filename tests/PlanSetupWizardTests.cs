using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.AccessControl;
using System.Security.Principal;
using Drtui;
using Xunit;

namespace Drtui.Tests;

public class PlanSetupWizardTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static Session Example() => new(PlanLoader.Load(
        Path.Combine(Root, "examples", "coolify-s3-cloudflare", "plan.drp.yaml")));

    [Fact]
    public void WizardPrioritizesMissingHostingToolsInputsAndMaskedBootstrapAuthentication()
    {
        var session = Example();
        var tasks = PlanSetupWizard.Tasks(session, session.Check(_ => false, _ => null));
        Assert.Equal("hosting", tasks[0].Kind);
        Assert.Contains(tasks, task => task.Kind == "tool" && task.Label == "CLI cf");
        Assert.Contains(tasks, task => task.Kind == "field" && task.Label == "r2_endpoint");
        Assert.Contains(tasks, task => task.Kind == "environment" && task.Label == "Environment CLOUDFLARE_API_TOKEN");
        Assert.DoesNotContain(tasks, task => task.Label.Contains("terraform") || task.Label.Contains("aws"));
        Assert.DoesNotContain(tasks, task => task.Label == "provider host");
        session.Inputs["cloudflare_account_id"] = JsonSerializer.Serialize(new string('a', 32));
        var updated = PlanSetupWizard.Tasks(session, session.Check(_ => true, _ => "hidden-bootstrap-token"));
        Assert.DoesNotContain(updated, task => task.Label == "cloudflare_account_id" || task.Kind == "environment");
        Assert.DoesNotContain("hidden-bootstrap-token", string.Join(" ", updated));
    }

    [Theory]
    [InlineData("cloudflare", "cloudflared")]
    [InlineData("custom", "terraform")]
    [InlineData("custom", "doctl;whoami")]
    [InlineData("invalid provider", "cli")]
    public void HostingRejectsInvalidOrIncompatibleSelections(string provider, string executable)
    {
        var session = Example();
        Assert.Throws<InvalidDataException>(() => HostingSetup.Configure(session.Plan.Providers["hosting"]!, provider, executable));
        Assert.Contains(session.Check(_ => true), check => check.Label == "hosting hosting" && check.Error is not null);
    }

    [Fact]
    public void HostingSelectionAddsOnlyItsNativeCliAndStillDoesNotAuthorizeRecovery()
    {
        var session = Example();
        HostingSetup.Configure(session.Plan.Providers["hosting"]!, "hetzner", "hcloud");
        var checks = session.Check(tool => tool != "hcloud");
        Assert.Contains(checks, check => check.Label == "hosting hosting" && check.Error is null);
        Assert.Contains(checks, check => check.Label == "CLI hcloud" && check.Error is not null);
        Assert.DoesNotContain(checks, check => check.Label is "CLI terraform" or "CLI aws");
        Assert.Contains(checks, check => check.Label == "provider host" && check.Error is not null);
    }

    [Fact]
    public void CloudflareIsSelectableWithCfAndPreservesAnExplicitWorkflowCompatibilityBlock()
    {
        var choice = Assert.Single(HostingSetup.Choices, choice => choice.Provider == "cloudflare");
        Assert.Equal("cf", choice.Executable);
        var session = Example();
        HostingSetup.Configure(session.Plan.Providers["hosting"]!, choice.Provider, choice.Executable);
        var checks = session.Check(_ => true);
        Assert.Equal("cloudflare-container", (string?)session.Plan.Providers["hosting"]?["config"]?["targetKind"]);
        Assert.Contains(checks, check => check.Label == "hosting hosting" && check.Error is null);
        Assert.Single(checks, check => check.Label == "CLI cf");
        Assert.Contains(checks, check => check.Label == "target compatibility hosting" && check.Error is not null);
        var tasks = PlanSetupWizard.Tasks(session, checks);
        Assert.DoesNotContain(tasks, task => task.Kind == "hosting");
        Assert.Contains(tasks, task => task.Kind == "field" && task.Label == "cloudflare_account_id");
        Assert.Contains(tasks, task => task.Kind == "field" && task.Label == "r2_endpoint");
        Assert.DoesNotContain(tasks, task => task.Label.StartsWith("target_", StringComparison.Ordinal));
        Assert.DoesNotContain(tasks, task => task.Label == "target compatibility hosting");
        Assert.Contains(tasks, task => task.Kind == "environment");
    }

    [Fact]
    public void IncompatibleHostingDefersOnlyExclusiveHostingInputsWithoutDependingOnTheirNames()
    {
        var session = Example();
        HostingSetup.Configure(session.Plan.Providers["hosting"]!, "cloudflare", "cf");
        Assert.Equal(new[] { "target_image", "target_region", "target_size" },
            HostingSetup.DeferredInputs(session.Plan).Order(StringComparer.Ordinal));
        session.Plan.Providers["s3"]!["config"]!["sharedRegion"] = "${{ inputs.target_region }}";
        session.Plan.Inputs["custom_host_option"] = JsonNode.Parse("""{"type":"string","required":true}""");
        session.Plan.Steps.First(step => (string?)step?["id"] == "provision_target")!["with"]!["custom"] =
            "${{ inputs.custom_host_option }}";
        var deferred = HostingSetup.DeferredInputs(session.Plan);
        Assert.Contains("custom_host_option", deferred);
        Assert.DoesNotContain("target_region", deferred);
        var tasks = PlanSetupWizard.Tasks(session, session.Check(_ => true));
        Assert.Contains(tasks, task => task.Label == "target_region");
        Assert.DoesNotContain(tasks, task => task.Label == "custom_host_option");
        HostingSetup.Configure(session.Plan.Providers["hosting"]!, "hetzner", "hcloud");
        Assert.Empty(HostingSetup.DeferredInputs(session.Plan));
        Assert.Contains(PlanSetupWizard.Tasks(session, session.Check(_ => true)),
            task => task.Label == "custom_host_option");
    }

    [Fact]
    public void CloudflareIsNotBlanketRejectedForAPlanWithoutTheSshProbe()
    {
        var plan = new Plan(JsonNode.Parse("""{"providers":{},"steps":[]}""")!.AsObject());
        var provider = JsonNode.Parse("""{"config":{"provisioning":"native-cli"}}""")!;
        HostingSetup.Configure(provider, "cloudflare", "cf");
        Assert.Null(HostingSetup.Validate("cloudflare", "cf"));
        Assert.Null(HostingSetup.CompatibilityIssue(plan, provider));
    }

    [Fact]
    public void WizardInstallationExcludesUnrelatedOptionalToolsButIncludesNpmPrerequisite()
    {
        var options = CliInstaller.Options([new Check("CLI cf", "missing")], true, false,
            toolExists: _ => false, requiredOnly: true);
        Assert.Equal(new[] { "node", "cf" }, options.Select(option => option.Tool));
        Assert.DoesNotContain(options, option => option.Tool is "aws" or "terraform" or "proxmox-backup-client");
    }

    [Theory]
    [InlineData("cloudflare_account_id", "not-an-id", false)]
    [InlineData("cloudflare_account_id", "0123456789abcdef0123456789abcdef", true)]
    [InlineData("coolify_state_sha256", "incorrect-hash", false)]
    [InlineData("coolify_state_sha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("r2_endpoint", "http://insecure.example", false)]
    [InlineData("r2_endpoint", "https://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com", true)]
    [InlineData("coolify_state_uri", "s3://bucket/exact/state.tar.zst", true)]
    [InlineData("coolify_state_uri", "s3://bucket", false)]
    public void WizardValidatesPlanDefinedFormats(string name, string value, bool valid)
    {
        var session = Example();
        var field = PlanSetup.From(session, session.Check(_ => true)).Inputs.Single(input => input.Label == name);
        Assert.Equal(valid, PlanSetupWizard.ValidateField(session, field, value) is null);
    }

    [Fact]
    public void DraftRoundTripPreservesPublicEditsVaultSettingsAndHostingWithoutChangingTemplate()
    {
        var session = Example();
        var original = session.Plan.Document.ToJsonString();
        session.Inputs["cloudflare_account_id"] = JsonSerializer.Serialize(new string('a', 32));
        session.Vaults["recovery_secrets"] = session.Vaults["recovery_secrets"] with { AccountId = new string('b', 32) };
        HostingSetup.Configure(session.Plan.Providers["hosting"]!, "hetzner", "hcloud");
        var root = Path.Combine(Path.GetTempPath(), "drtui-draft-" + Guid.NewGuid().ToString("N"));
        string? path = null;
        try
        {
            path = PlanStorage.SaveDraft(session, root);
            var loaded = new Session(PlanLoader.Load(path));
            Assert.Equal(session.Inputs["cloudflare_account_id"], loaded.Inputs["cloudflare_account_id"]);
            Assert.Equal(new string('b', 32), loaded.Vaults["recovery_secrets"].AccountId);
            Assert.Equal("hcloud", (string?)loaded.Plan.Providers["hosting"]?["config"]?["clientPath"]);
            Assert.Equal("${{ steps.fetch_secrets.outputs.r2_secret_access_key }}",
                (string?)loaded.Plan.Providers["s3"]?["config"]?["secretAccessKey"]);
            Assert.Null(session.Plan.Inputs["cloudflare_account_id"]?["default"]);
            Assert.DoesNotContain(new string('b', 32), original);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            else
            {
                var user = WindowsIdentity.GetCurrent().User!;
                var directorySecurity = new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl();
                Assert.True(directorySecurity.AreAccessRulesProtected);
                var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().ToArray();
                Assert.NotEmpty(rules);
                foreach (var rule in rules)
                {
                    Assert.Equal(user, rule.IdentityReference);
                    Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                }
            }
        }
        finally
        {
            if (path is not null) File.Delete(path);
            if (Directory.Exists(root)) { Directory.Delete(Path.Combine(root, "coolify-s3-cloudflare")); Directory.Delete(root); }
        }
    }

    [Theory]
    [InlineData("apiToken")]
    [InlineData("secretAccessKey")]
    [InlineData("privateKey")]
    public void ExportRejectsLiteralCredentialsBeforeAnyPublication(string key)
    {
        var session = Example();
        session.Plan.Providers["s3"]!["config"]![key] = "must-not-be-uploaded";
        Assert.Throws<InvalidDataException>(() => PlanStorage.Export(session));
    }

    [Fact]
    public void ExportRejectsSecretDefaultsAndNeverExportsEnvironmentValues()
    {
        var session = Example();
        session.Plan.Inputs["extra_secret"] = JsonNode.Parse("""{"type":"secret","default":"must-not-be-uploaded"}""");
        Assert.Throws<InvalidDataException>(() => PlanStorage.Export(session));
        session.Plan.Inputs.Remove("extra_secret");
        session.Plan.Inputs["apiToken"] = JsonNode.Parse("""{"type":"secret"}""");
        var yaml = PlanStorage.Export(session);
        Assert.Contains("tokenEnv: CLOUDFLARE_API_TOKEN", yaml);
        Assert.DoesNotContain("must-not-be-uploaded", yaml);
    }

    [Fact]
    public async Task PrivateRepositoryPublicationUsesStdinAndExistingBlobShaWithoutShellCommands()
    {
        var requests = new List<(IReadOnlyList<string> Arguments, string? Input)>();
        var storage = new GitHubPlanStorage((arguments, input, _) =>
        {
            requests.Add((arguments, input));
            return Task.FromResult(new SetupCommandResult(0, requests.Count == 1
                ? """{"data":{"repository":{"isPrivate":true,"object":{"oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}}}"""
                : """{"content":{"sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","path":"plans/coolify-s3-cloudflare/plan.drp.yaml"}}"""));
        });
        await storage.PublishAsync(Example(), "owner/private-recovery");
        Assert.Equal(2, requests.Count);
        Assert.Contains("repos/owner/private-recovery/contents/plans/coolify-s3-cloudflare/plan.drp.yaml", requests[1].Arguments);
        Assert.Equal("-", requests[1].Arguments.Last());
        var body = JsonNode.Parse(requests[1].Input!)!;
        Assert.Equal(new string('a', 40), (string?)body["sha"]);
        var plan = Encoding.UTF8.GetString(Convert.FromBase64String((string)body["content"]!));
        Assert.Contains("provisioning: native-cli", plan);
        Assert.DoesNotContain(plan, string.Join(" ", requests[1].Arguments));
    }

    [Theory]
    [InlineData("""{"data":{"repository":{"isPrivate":false}}}""")]
    [InlineData("""{"errors":[{"message":"not authorized"}]}""")]
    [InlineData("""{"data":{"repository":null}}""")]
    [InlineData("""{"data":{"repository":{"isPrivate":"true"}}}""")]
    [InlineData("[]")]
    public async Task PublicOrInaccessibleRepositoriesNeverReceiveAnUpload(string response)
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new SetupCommandResult(0, response));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.PublishAsync(Example(), "owner/recovery"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UploadFailureIsNotReportedAsSuccess()
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) => Task.FromResult(++calls == 1
            ? new SetupCommandResult(0, """{"data":{"repository":{"isPrivate":true,"object":null}}}""")
            : new SetupCommandResult(1, "not echoed")));
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.PublishAsync(Example(), "owner/recovery"));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("""{"content":{"sha":true}}""")]
    [InlineData("""{"content":{"sha":"not-a-digest"}}""")]
    [InlineData("""{"content":{"sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","path":"wrong.yaml"}}""")]
    public async Task UnexpectedUploadResponsesAreNotReportedAsSuccess(string response)
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) => Task.FromResult(new SetupCommandResult(0,
            ++calls == 1 ? """{"data":{"repository":{"isPrivate":true,"object":null}}}""" : response)));
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.PublishAsync(Example(), "owner/recovery"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task GitHubAuthenticationIsCheckedThroughGhWithoutExposingItsOutput(int exitCode, bool authenticated)
    {
        var storage = new GitHubPlanStorage((arguments, input, _) =>
        {
            Assert.Equal(new[] { "auth", "status" }, arguments);
            Assert.Null(input);
            return Task.FromResult(new SetupCommandResult(exitCode, "authentication output is not displayed"));
        });
        Assert.Equal(authenticated, await storage.IsAuthenticatedAsync());
    }

    [Fact]
    public async Task RepositoryDiscoveryUsesPaginatedGhResultsAndPrioritizesSavedThenCurrentRepositories()
    {
        var requests = new List<IReadOnlyList<string>>();
        using var cancellation = new CancellationTokenSource();
        var storage = new GitHubPlanStorage((arguments, input, token) =>
        {
            requests.Add(arguments);
            Assert.Null(input);
            Assert.Equal(cancellation.Token, token);
            return Task.FromResult(new SetupCommandResult(0, requests.Count == 1
                ? """{"nameWithOwner":"owner/current","isPrivate":true,"viewerPermission":"ADMIN"}"""
                : """
                  {"nameWithOwner":"org/saved","isPrivate":true,"canPush":true}
                  {"nameWithOwner":"owner/public","isPrivate":false,"canPush":true}
                  {"nameWithOwner":"org/readonly","isPrivate":true,"canPush":false}
                  {"nameWithOwner":"collaborator/backup","isPrivate":true,"canPush":true}
                  {"nameWithOwner":"org/SAVED","isPrivate":true,"canPush":true}
                  {"nameWithOwner":"owner/current","isPrivate":true,"canPush":true}
                  """));
        });
        var discovered = await storage.DiscoverRepositoriesAsync("ORG/saved", cancellation.Token);
        Assert.Equal(new[] { "org/saved", "owner/current", "collaborator/backup" }, discovered.Repositories);
        Assert.Equal("owner/current", discovered.CurrentRepository);
        Assert.Equal("org/saved", discovered.PreferredRepository);
        Assert.Null(discovered.Notice);
        Assert.Equal(new[] { "repo", "view", "--json", "nameWithOwner,isPrivate,viewerPermission" }, requests[0]);
        Assert.Equal(new[]
        {
            "api", "--paginate",
            "user/repos?visibility=private&affiliation=owner,collaborator,organization_member&per_page=100&sort=full_name",
            "--jq", ".[] | {nameWithOwner: .full_name, isPrivate: .private, canPush: .permissions.push} | @json"
        }, requests[1]);
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData(false, "ADMIN")]
    [InlineData(true, "READ")]
    [InlineData(true, "TRIAGE")]
    public async Task PublicOrReadOnlyCurrentRepositoriesAreNotOffered(bool isPrivate, string permission)
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) => Task.FromResult(new SetupCommandResult(0,
            ++calls == 1 ? JsonSerializer.Serialize(new { nameWithOwner = "owner/current", isPrivate, viewerPermission = permission })
                : """{"nameWithOwner":"org/backup","isPrivate":true,"canPush":true}""")));
        var discovered = await storage.DiscoverRepositoriesAsync("owner/current");
        Assert.Equal(new[] { "org/backup" }, discovered.Repositories);
        Assert.Null(discovered.PreferredRepository);
        Assert.Contains("public or not writable", discovered.Notice);
        Assert.Contains("saved plan repository is no longer", discovered.Notice);
    }

    [Theory]
    [InlineData("WRITE")]
    [InlineData("MAINTAIN")]
    [InlineData("ADMIN")]
    public async Task WritablePrivateCurrentRepositoryIsPreferredWithoutASavedSelection(string permission)
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) => Task.FromResult(new SetupCommandResult(0,
            ++calls == 1 ? JsonSerializer.Serialize(new { nameWithOwner = "owner/current", isPrivate = true, viewerPermission = permission })
                : """{"nameWithOwner":"aaa/backup","isPrivate":true,"canPush":true}""")));
        var discovered = await storage.DiscoverRepositoriesAsync();
        Assert.Equal(new[] { "owner/current", "aaa/backup" }, discovered.Repositories);
        Assert.Null(discovered.Notice);
    }

    [Theory]
    [InlineData("""{"nameWithOwner":"org/backup","isPrivate":true,"canPush":true}""", 1)]
    [InlineData("", 0)]
    public async Task RepositoryDiscoveryWorksOutsideAGitCheckoutAndPreservesEmptyResults(string list, int count)
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) => Task.FromResult(++calls == 1
            ? new SetupCommandResult(1, "private diagnostic not echoed")
            : new SetupCommandResult(0, list)));
        var discovered = await storage.DiscoverRepositoriesAsync();
        Assert.Equal(count, discovered.Repositories.Count);
        Assert.Null(discovered.CurrentRepository);
        Assert.Contains("could not detect", discovered.Notice);
        Assert.DoesNotContain("private diagnostic", discovered.Notice);
    }

    [Fact]
    public async Task RepositoryListingFailureDoesNotSilentlyFallBackToTheCurrentRepository()
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) => Task.FromResult(++calls == 1
            ? new SetupCommandResult(0, """{"nameWithOwner":"owner/current","isPrivate":true,"viewerPermission":"WRITE"}""")
            : new SetupCommandResult(1, "private diagnostic not echoed")));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => storage.DiscoverRepositoriesAsync());
        Assert.Contains("Cannot list private GitHub repositories", error.Message);
        Assert.DoesNotContain("private diagnostic", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("""{"nameWithOwner":"https://github.com/owner/repo","isPrivate":true,"canPush":true}""")]
    [InlineData("""{"nameWithOwner":"owner/repo","isPrivate":"true","canPush":true}""")]
    [InlineData("""{"nameWithOwner":"owner/repo","isPrivate":true}""")]
    public async Task MalformedDiscoveryResponsesAreNotPresentedAsEmptyRepositoryLists(string response)
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) => Task.FromResult(++calls == 1
            ? new SetupCommandResult(1, "")
            : new SetupCommandResult(0, response)));
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.DiscoverRepositoriesAsync());
    }

    [Fact]
    public async Task RepositoryOwnersComeFromTheAuthenticatedUserAndPaginatedOrganizationMemberships()
    {
        var requests = new List<IReadOnlyList<string>>();
        using var cancellation = new CancellationTokenSource();
        var storage = new GitHubPlanStorage((arguments, input, token) =>
        {
            requests.Add(arguments);
            Assert.Null(input);
            Assert.Equal(cancellation.Token, token);
            return Task.FromResult(new SetupCommandResult(0, requests.Count == 1 ? "my-user\n" :
                """
                {"login":"z-org"}
                {"login":"a-org"}
                {"login":"Z-ORG"}
                """));
        });
        var owners = await storage.DiscoverOwnersAsync(cancellation.Token);
        Assert.Equal(new[]
        {
            new GitHubRepositoryOwner("my-user", false),
            new GitHubRepositoryOwner("a-org", true),
            new GitHubRepositoryOwner("z-org", true)
        }, owners);
        Assert.Equal(new[] { "api", "user", "--jq", ".login" }, requests[0]);
        Assert.Equal(new[] { "api", "--paginate", "user/orgs?per_page=100", "--jq", ".[] | {login: .login} | @json" }, requests[1]);
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task AccountsWithNoOrganizationsCanStillCreatePersonalRepositories()
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) =>
            Task.FromResult(new SetupCommandResult(0, ++calls == 1 ? "my-user" : "")));
        Assert.Equal(new[] { new GitHubRepositoryOwner("my-user", false) }, await storage.DiscoverOwnersAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OwnerDiscoveryFailuresDoNotSilentlyChooseADifferentOwner(int failingCall)
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) => Task.FromResult(++calls == failingCall
            ? new SetupCommandResult(1, "private diagnostic not echoed")
            : new SetupCommandResult(0, "my-user")));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => storage.DiscoverOwnersAsync());
        Assert.Contains("no repository was created", error.Message);
        Assert.DoesNotContain("private diagnostic", error.Message);
        Assert.Equal(failingCall, calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"login":"org/name"}""")]
    [InlineData("""{"login":true}""")]
    public async Task InvalidOrganizationResponsesBlockCreation(string response)
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) =>
            Task.FromResult(new SetupCommandResult(0, ++calls == 1 ? "my-user" : response)));
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.DiscoverOwnersAsync());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("my-user")]
    [InlineData("my-org")]
    public async Task NewRepositoryCreationIsPrivateAndVerifiedWithoutPushingLocalFiles(string owner)
    {
        var requests = new List<IReadOnlyList<string>>();
        var storage = new GitHubPlanStorage((arguments, input, _) =>
        {
            requests.Add(arguments);
            Assert.Null(input);
            return Task.FromResult(new SetupCommandResult(0, requests.Count == 1 ? "https://github.com/" + owner + "/recovery" :
                JsonSerializer.Serialize(new { nameWithOwner = owner + "/recovery", isPrivate = true, viewerPermission = "ADMIN" })));
        });
        Assert.Equal(owner + "/recovery", await storage.CreateRepositoryAsync(owner, "recovery"));
        Assert.Equal(new[] { "repo", "create", owner + "/recovery", "--private" }, requests[0]);
        Assert.Equal(new[] { "repo", "view", owner + "/recovery", "--json", "nameWithOwner,isPrivate,viewerPermission" }, requests[1]);
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData("owner/other", "recovery")]
    [InlineData("--owner", "recovery")]
    [InlineData("owner", "owner/recovery")]
    [InlineData("owner", "name with spaces")]
    [InlineData("owner", "")]
    [InlineData("owner", ".")]
    [InlineData("owner", "..")]
    public async Task InvalidNewRepositoryArgumentsNeverReachGh(string owner, string name)
    {
        var storage = new GitHubPlanStorage((_, _, _) => throw new InvalidOperationException("gh must not run"));
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.CreateRepositoryAsync(owner, name));
    }

    [Theory]
    [InlineData("recovery-plans", true)]
    [InlineData(".github", true)]
    [InlineData("my_recovery.plans", true)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("owner/recovery", false)]
    public void NewRepositoryNamesFollowGitHubSyntax(string name, bool valid)
    {
        Assert.Equal(valid, GitHubPlanStorage.ValidNewRepositoryName(name));
        Assert.Equal(valid, GitHubPlanStorage.ValidRepository("owner/" + name));
        Assert.False(GitHubPlanStorage.ValidNewRepositoryName(new string('a', 101)));
    }

    [Fact]
    public async Task CreationFailureDoesNotReuseAnExistingRepositoryOrRetryWithAnotherOwner()
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new SetupCommandResult(1, "private diagnostic not echoed"));
        });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => storage.CreateRepositoryAsync("my-org", "recovery"));
        Assert.Contains("creation failed", error.Message);
        Assert.DoesNotContain("private diagnostic", error.Message);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "{}")]
    [InlineData(0, """{"nameWithOwner":"other/recovery","isPrivate":true,"viewerPermission":"ADMIN"}""")]
    [InlineData(0, """{"nameWithOwner":"my-org/recovery","isPrivate":false,"viewerPermission":"ADMIN"}""")]
    [InlineData(0, """{"nameWithOwner":"my-org/recovery","isPrivate":true,"viewerPermission":"READ"}""")]
    public async Task CreationMustConfirmTheRequestedWritablePrivateRepository(int verificationExit, string response)
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) => Task.FromResult(++calls == 1
            ? new SetupCommandResult(0, "")
            : new SetupCommandResult(verificationExit, response)));
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.CreateRepositoryAsync("my-org", "recovery"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void AutomatedLoginsHaveFixedArgumentsAndNeverAcceptCredentialArguments()
    {
        Assert.Equal(new[] { "auth", "login", "--web", "--git-protocol", "https" },
            PlanSetupWizard.LoginCommand("gh").ArgumentList.ToArray());
        Assert.Equal(new[] { "context", "create", "drtui-recovery" },
            PlanSetupWizard.LoginCommand("hcloud").ArgumentList.ToArray());
        Assert.Equal(new[] { "auth", "init" }, PlanSetupWizard.LoginCommand("doctl").ArgumentList.ToArray());
        Assert.False(PlanSetupWizard.LoginCommand("cf").Environment.ContainsKey("CLOUDFLARE_API_TOKEN"));
        Assert.Throws<InvalidOperationException>(() => PlanSetupWizard.LoginCommand("untrusted-command"));
    }
}
