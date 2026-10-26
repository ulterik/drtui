using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Drtui;

internal sealed record Check(string Label, string? Error = null);
internal sealed record Vault(string AuthMethod, string Profile, string Region, string AccountId, string TokenEnv);

internal sealed class Session(Plan plan)
{
    public Plan Plan { get; } = plan;
    public Dictionary<string, string> Inputs { get; } = plan.Inputs
        .Where(item => (string?)item.Value?["type"] != "secret" && item.Value?["default"] is not null)
        .ToDictionary(item => item.Key, item => item.Value!["default"]!.ToJsonString(), StringComparer.Ordinal);
    public Dictionary<string, Vault> Vaults { get; } = CreateVaults(plan);

    private static Dictionary<string, Vault> CreateVaults(Plan plan)
    {
        var vaults = new Dictionary<string, Vault>(StringComparer.Ordinal);
        foreach (var provider in plan.Providers.Where(item => (string?)item.Value?["kind"] == "keyvault"))
        {
            var auth = plan.Steps
                .FirstOrDefault(step => (string?)step?["uses"] == provider.Key + "/initialize")?
                ["with"]?["authConfig"];
            vaults[provider.Key] = new Vault(
                (string?)auth?["method"] ?? "",
                (string?)auth?["parameters"]?["profile"] ?? "",
                (string?)provider.Value?["config"]?["region"] ?? "",
                (string?)provider.Value?["config"]?["accountId"] ?? "",
                (string?)auth?["parameters"]?["tokenEnv"] ?? "");
        }
        return vaults;
    }

    public IReadOnlyList<Check> Check(Func<string, bool>? toolExists = null, Func<string, string?>? readEnvironment = null)
    {
        toolExists ??= ToolExists;
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var checks = new List<Check>();
        foreach (var input in Plan.Inputs.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var definition = input.Value!;
            Inputs.TryGetValue(input.Key, out var value);
            var issue = ValidateInput((string)definition["type"]!,
                (bool?)definition["required"] ?? false, value);
            checks.Add(new Check("input " + input.Key, issue));
        }
        foreach (var (name, vault) in Vaults.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var issue = vault.AuthMethod switch
            {
                "aws-cli-profile" when string.IsNullOrWhiteSpace(vault.Region) || string.IsNullOrWhiteSpace(vault.Profile)
                    => "configure AWS region and CLI profile",
                "cloudflare-api-token" when string.IsNullOrWhiteSpace(vault.TokenEnv)
                    => "plan must declare tokenEnv",
                "cloudflare-api-token" when ValidateCloudflareAccountId(vault.AccountId) is not null
                    => "configure a 32-character hex Cloudflare account ID",
                "aws-cli-profile" or "cloudflare-api-token" => null,
                _ => "unsupported keyvault authentication method"
            };
            if (!Plan.Steps.Any(step => (string?)step?["uses"] == name + "/initialize") ||
                !Plan.Steps.Any(step => (string?)step?["uses"] == name + "/fetchPlanSecrets"))
                issue = "plan must initialize the keyvault and declare secret fetch";
            var details = vault.AuthMethod switch
            {
                "cloudflare-api-token" => $"Cloudflare account: {vault.AccountId}, token env: {vault.TokenEnv}",
                "aws-cli-profile" => $"AWS region: {vault.Region}, profile: {vault.Profile}",
                _ => $"auth method: {vault.AuthMethod}"
            };
            checks.Add(new Check($"keyvault {name} ({details})", issue));
            if (vault.AuthMethod == "cloudflare-api-token" && !string.IsNullOrWhiteSpace(vault.TokenEnv))
                checks.Add(new Check("Environment " + vault.TokenEnv,
                    string.IsNullOrWhiteSpace(readEnvironment(vault.TokenEnv)) ? "not set or empty" : null));
        }
        var tools = new SortedSet<string>(StringComparer.Ordinal);
        if (Vaults.Values.Any(vault => vault.AuthMethod == "aws-cli-profile")) tools.Add(CliToolCatalog.Aws.Executable);
        if (Vaults.Values.Any(vault => vault.AuthMethod == "cloudflare-api-token")) tools.Add(CliToolCatalog.Cloudflare.Executable);
        foreach (var provider in Plan.Providers)
        {
            var kind = (string?)provider.Value?["kind"];
            var source = (string?)provider.Value?["source"] ?? "";
            if (kind == "infrastructure" && source.Contains("terraform", StringComparison.Ordinal))
                tools.Add(CliToolCatalog.Terraform.Executable);
            if (source.Contains("github", StringComparison.OrdinalIgnoreCase))
                tools.Add(CliToolCatalog.GitHub.Executable);
            if (kind == "backup" && source.Contains("proxmox-backup", StringComparison.Ordinal))
                tools.Add((string?)provider.Value?["config"]?["clientPath"] ?? CliToolCatalog.Proxmox.Executable);
        }
        checks.AddRange(tools.Select(tool => new Check("CLI " + tool,
            toolExists(tool) ? null : "not installed or not on PATH")));
        checks.Add(new Check("provider host", "not implemented; live plan execution is unavailable"));
        return checks;
    }

    internal IReadOnlyList<Check> CheckWithCachedProfiles(IReadOnlyList<Check> previous,
        Func<string, bool>? toolExists = null, Func<string, string?>? readEnvironment = null)
    {
        var checks = Check(toolExists, readEnvironment).ToList();
        if (checks.Any(check => check.Label == "CLI aws" && check.Error is null))
            checks.AddRange(previous.Where(check => check.Label.StartsWith("AWS profile ", StringComparison.Ordinal) &&
                Vaults.Values.Any(vault => vault.AuthMethod == "aws-cli-profile" &&
                    check.Label == "AWS profile " + vault.Profile)));
        return checks;
    }

    internal static string? ValidateCloudflareAccountId(string value) =>
        string.IsNullOrWhiteSpace(value) ? "required" :
        value.Length == 32 && value.All(Uri.IsHexDigit) ? null : "expected 32 hex characters";

    public async Task<IReadOnlyList<Check>> TestAsync()
    {
        var checks = Check().ToList();
        if (!ToolExists("aws")) return checks;
        foreach (var vault in Vaults.Values.Where(vault => vault.AuthMethod == "aws-cli-profile" && vault.Profile != ""))
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo("aws")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                process.StartInfo.ArgumentList.Add("configure");
                process.StartInfo.ArgumentList.Add("list-profiles");
                process.Start();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                string output;
                try
                {
                    output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(true);
                    throw;
                }
                var found = process.ExitCode == 0 && output.Split('\n').Any(line => line.TrimEnd('\r') == vault.Profile);
                checks.Add(new Check("AWS profile " + vault.Profile,
                    found ? null : "AWS CLI profile not found (or could not be listed)"));
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                checks.Add(new Check("AWS profile " + vault.Profile, "AWS CLI profile not found (or could not be listed)"));
            }
        }
        return checks;
    }

    internal static string? ValidateInput(string type, bool required, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "null")
            return required ? "required input is missing" : null;
        JsonElement decoded;
        try { decoded = JsonDocument.Parse(value).RootElement.Clone(); }
        catch (JsonException) { return "enter a JSON value"; }
        return type switch
        {
            "string" or "path" or "ip" when decoded.ValueKind != JsonValueKind.String || decoded.GetString() == ""
                => "expected a non-empty JSON string",
            "ip" when !IPAddress.TryParse(decoded.GetString(), out _) => "invalid IP address",
            "integer" when decoded.ValueKind != JsonValueKind.Number || !decoded.TryGetInt64(out _)
                => "expected an integer",
            "number" when decoded.ValueKind != JsonValueKind.Number => "expected a number",
            "boolean" when decoded.ValueKind is not (JsonValueKind.True or JsonValueKind.False) => "expected a boolean",
            "array" or "domain-list" when decoded.ValueKind != JsonValueKind.Array => "expected an array",
            "object" or "network-map" when decoded.ValueKind != JsonValueKind.Object => "expected an object",
            "secret" => "secrets must be sourced through a keyvault",
            _ => null
        };
    }

    internal static bool ToolExists(string tool)
    {
        if (Path.IsPathRooted(tool) || tool.Contains(Path.DirectorySeparatorChar)) return File.Exists(tool);
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : [""];
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(folder => folder != "")
            .Any(folder => extensions.Any(extension => File.Exists(Path.Combine(folder, tool + extension)) ||
                                                   File.Exists(Path.Combine(folder, tool))));
    }
}