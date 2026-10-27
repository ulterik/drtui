using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Drtui;

internal sealed record HostingChoice(string Name, string Provider, string Executable, string Documentation,
    string TargetKind = "linux-vm");

internal static class HostingSetup
{
    internal static IReadOnlyList<HostingChoice> Choices { get; } =
    [
        new("Cloudflare (Containers / Sandbox)", "cloudflare", "cf", "https://developers.cloudflare.com/containers/",
            "cloudflare-container"),
        new("Hetzner Cloud", "hetzner", "hcloud", "https://github.com/hetznercloud/cli"),
        new("DigitalOcean", "digitalocean", "doctl", "https://docs.digitalocean.com/reference/doctl/")
    ];

    internal static IEnumerable<KeyValuePair<string, JsonNode?>> Providers(Plan plan) =>
        plan.Providers.Where(provider => (string?)provider.Value?["kind"] == "infrastructure" &&
            (string?)provider.Value?["config"]?["provisioning"] == "native-cli");

    internal static string? Validate(string? provider, string? executable)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(executable))
            return "select a recovery hosting provider during guided setup";
        if (!Regex.IsMatch(provider, "^[a-z][a-z0-9_-]{0,62}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(executable, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant))
            return "use a provider identifier and a single CLI executable name";
        if ((provider == "cloudflare" && executable != "cf") ||
            (provider != "cloudflare" && executable is "cf" or "cloudflare"))
            return "select Cloudflare with its cf CLI";
        if (executable is "cloudflared" or "wrangler")
            return "select a compute-management CLI; use cf for Cloudflare";
        if (executable == "terraform")
            return "select the hosting provider's own CLI, not Terraform";
        return null;
    }

    internal static void Configure(JsonNode provider, string name, string executable)
    {
        if (Validate(name, executable) is { } error) throw new InvalidDataException(error);
        provider["config"]!["provider"] = name;
        provider["config"]!["clientPath"] = executable;
        provider["config"]!["targetKind"] = Choices.FirstOrDefault(choice => choice.Provider == name)?.TargetKind ?? "linux-vm";
        provider["requiredClis"] = new JsonArray(executable);
    }

    internal static string? CompatibilityIssue(Plan plan, JsonNode provider)
    {
        if ((string?)provider["config"]?["provider"] == "cloudflare" &&
            plan.Steps.Any(step => (string?)step?["uses"] == "engine/probeSSH"))
            return "Cloudflare Containers/Sandbox is selected, but this plan requires SSH host recovery. A Cloudflare-compatible recovery workflow and adapter are not implemented.";
        return null;
    }

    internal static IReadOnlySet<string> DeferredInputs(Plan plan)
    {
        var incompatible = Providers(plan).Where(provider => CompatibilityIssue(plan, provider.Value!) is not null)
            .Select(provider => provider.Key).ToHashSet(StringComparer.Ordinal);
        var deferred = new HashSet<string>(StringComparer.Ordinal);
        if (incompatible.Count == 0) return deferred;
        var shared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in plan.Providers)
            CollectInputs(provider.Value?["config"], incompatible.Contains(provider.Key) ? deferred : shared);
        foreach (var step in plan.Steps)
        {
            var provider = ((string?)step?["uses"] ?? "").Split('/')[0];
            CollectInputs(step, incompatible.Contains(provider) ? deferred : shared);
        }
        CollectInputs(plan.Document["policy"], shared);
        CollectInputs(plan.Document["transactions"], shared);
        deferred.ExceptWith(shared);
        return deferred;
    }

    private static void CollectInputs(JsonNode? node, ISet<string> inputs)
    {
        if (node is JsonObject properties)
            foreach (var property in properties) CollectInputs(property.Value, inputs);
        else if (node is JsonArray items)
            foreach (var item in items) CollectInputs(item, inputs);
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var reference = Regex.Match(text,
                @"^\$\{\{\s*inputs\.([a-z][a-z0-9_-]*)(?:\.[A-Za-z_][A-Za-z0-9_-]*)*\s*\}\}$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (reference.Success) inputs.Add(reference.Groups[1].Value);
        }
    }
}
