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
}
