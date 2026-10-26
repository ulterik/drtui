using System.Text.Json;
using System.Text.Json.Nodes;

namespace Drtui;

internal sealed record SetupField(
    string Label, string Type, string? RawValue, string? Description, string? Error,
    bool Required, bool IsDefault, Action<string?> Save)
{
    public string DisplayValue
    {
        get
        {
            if (string.IsNullOrWhiteSpace(RawValue)) return "<not set>";
            if (Type is "string" or "path" or "ip")
            {
                try { return JsonDocument.Parse(RawValue).RootElement.GetString() ?? "<not set>"; }
                catch (Exception error) when (error is JsonException or InvalidOperationException) { }
            }
            return RawValue;
        }
    }
}

internal sealed record SecretRequirement(string Vault, string[] Names);
internal sealed record ProviderSetting(string Name, string Value);
internal sealed record ProviderBinding(string Name, string Kind, IReadOnlyList<ProviderSetting> Settings);

internal sealed record PlanSetup(
    IReadOnlyList<SetupField> Inputs,
    IReadOnlyList<SetupField> VaultSettings,
    IReadOnlyList<ProviderBinding> ProviderBindings,
    IReadOnlyList<Check> LocalDependencies,
    IReadOnlyList<SecretRequirement> Secrets)
{
    public static PlanSetup From(Session session, IReadOnlyList<Check> checks)
    {
        var inputs = new List<SetupField>();
        var vaultSettings = new List<SetupField>();
        foreach (var (name, definition) in session.Plan.Inputs.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if ((string?)definition?["type"] == "secret") continue;
            var type = (string)definition!["type"]!;
            session.Inputs.TryGetValue(name, out var value);
            inputs.Add(new SetupField(name, type, value,
                (string?)definition["description"], checks.FirstOrDefault(check => check.Label == "input " + name)?.Error,
                (bool?)definition["required"] ?? false,
                value is not null && value == definition["default"]?.ToJsonString(),
                updated =>
                {
                    if (updated is null) session.Inputs.Remove(name);
                    else session.Inputs[name] = updated;
                }));
        }

        foreach (var (name, vault) in session.Vaults.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (vault.AuthMethod == "aws-cli-profile")
            {
                vaultSettings.Add(new SetupField(name + " / AWS profile", "text", vault.Profile,
                    "Local AWS CLI profile used for secret retrieval.",
                    string.IsNullOrWhiteSpace(vault.Profile) ? "required" : null, true, false,
                    value => session.Vaults[name] = session.Vaults[name] with { Profile = value ?? "" }));
                vaultSettings.Add(new SetupField(name + " / region", "text", vault.Region,
                    "AWS region for the keyvault provider.",
                    string.IsNullOrWhiteSpace(vault.Region) ? "required" : null, true, false,
                    value => session.Vaults[name] = session.Vaults[name] with { Region = value ?? "" }));
            }
            else if (vault.AuthMethod == "cloudflare-api-token")
            {
                vaultSettings.Add(new SetupField(name + " / Cloudflare account ID", "text", vault.AccountId,
                    "Cloudflare account containing the PBS API token in Secrets Store.",
                    Session.ValidateCloudflareAccountId(vault.AccountId), true, false,
                    value => session.Vaults[name] = session.Vaults[name] with { AccountId = value ?? "" }));
            }
        }

        var dependencies = checks.Where(check => check.Label.StartsWith("CLI ", StringComparison.Ordinal) ||
            check.Label.StartsWith("AWS profile ", StringComparison.Ordinal) ||
            check.Label.StartsWith("Environment ", StringComparison.Ordinal) ||
            check.Label.StartsWith("keyvault ", StringComparison.Ordinal) &&
            (check.Error == "plan must initialize the keyvault and declare secret fetch" ||
             check.Error == "plan must declare tokenEnv" ||
             check.Error == "unsupported keyvault authentication method") ||
            check.Label.StartsWith("input ", StringComparison.Ordinal) &&
            session.Plan.Inputs[check.Label[6..]]?["type"]?.GetValue<string>() == "secret").ToArray();

        var secrets = session.Plan.Steps
            .Where(step => ((string?)step?["uses"])?.EndsWith("/fetchPlanSecrets", StringComparison.Ordinal) == true)
            .Select(step => new SecretRequirement(((string)step!["uses"]!).Split('/')[0],
                step["outputs"]?.AsObject()
                    .Where(output => (bool?)output.Value?["sensitive"] == true &&
                        ((string?)output.Value?["from"])?.StartsWith("result.secrets.", StringComparison.Ordinal) == true)
                    .Select(output => ((string)output.Value!["from"]!)["result.secrets.".Length..])
                    .Order(StringComparer.Ordinal).ToArray() ?? []))
            .Where(requirement => requirement.Names.Length > 0).ToArray();

        var publicKeys = new[] { "server", "port", "datastore", "namespace", "region", "accountId", "workingDirectory", "checksumMode", "releaseChannel", "ttlSeconds" };
        var providers = session.Plan.Providers.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(provider =>
            {
                var config = provider.Value?["config"] as JsonObject;
                var details = publicKeys.Where(key => config?[key] is JsonValue value && !string.IsNullOrWhiteSpace(value.ToString()))
                    .Select(key => new ProviderSetting(key, config![key]!.ToString())).ToArray();
                return new ProviderBinding(provider.Key, (string?)provider.Value?["kind"] ?? "", details);
            }).ToArray();

        return new PlanSetup(inputs, vaultSettings, providers, dependencies, secrets);
    }
}