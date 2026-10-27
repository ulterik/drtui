using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace Drtui;

internal static class PlanStorage
{
    internal const string RepositoryLabel = "drtui.plan-repository";
    private static readonly HashSet<string> CredentialKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apiToken", "token", "password", "privateKey", "secretAccessKey", "accessKeyId", "clientSecret", "credential"
    };

    internal static string DraftPath(Session session, string? root = null)
    {
        var id = (string?)session.Plan.Metadata["id"] ?? "";
        if (!Regex.IsMatch(id, "^[a-z][a-z0-9_-]{0,62}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("A valid plan ID is required for draft storage.");
        return Path.Combine(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "drtui", "plans"),
            id, "plan.drp.yaml");
    }

    internal static string Export(Session session)
    {
        var document = session.Plan.Document.DeepClone().AsObject();
        foreach (var input in document["inputs"]?.AsObject() ?? new JsonObject())
        {
            if ((string?)input.Value?["type"] == "secret")
            {
                if (input.Value?["default"] is not null)
                    throw new InvalidDataException("Remove literal secret input defaults before saving a plan.");
                continue;
            }
            if (session.Inputs.TryGetValue(input.Key, out var value))
            {
                if (CredentialKeys.Contains(input.Key))
                    throw new InvalidDataException($"Credential input '{input.Key}' must be declared secret and obtained through the keyvault.");
                var definition = input.Value!;
                var error = Session.ValidateInput((string)definition["type"]!,
                    false, value, (string?)definition["pattern"]);
                if (error is not null)
                    throw new InvalidDataException($"Cannot save input {input.Key}: {error}");
                definition["default"] = JsonNode.Parse(value);
            }
            else input.Value!.AsObject().Remove("default");
        }
        foreach (var (name, vault) in session.Vaults)
        {
            var config = document["providers"]![name]!["config"] as JsonObject;
            if (config is null)
            {
                config = new JsonObject();
                document["providers"]![name]!["config"] = config;
            }
            if (vault.AuthMethod == "cloudflare-api-token") config["accountId"] = vault.AccountId;
            else if (vault.AuthMethod == "aws-cli-profile")
            {
                config["region"] = vault.Region;
                var initialize = document["steps"]!.AsArray()
                    .First(step => (string?)step?["uses"] == name + "/initialize");
                initialize!["with"]!["authConfig"]!["parameters"]!["profile"] = vault.Profile;
            }
        }
        RejectCredentials(document, isDocument: true);
        var valueTree = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization()
            .Build().Deserialize<object>(document.ToJsonString());
        var yaml = new SerializerBuilder().WithQuotingNecessaryStrings().Build().Serialize(valueTree);
        if (Encoding.UTF8.GetByteCount(yaml) > 4 * 1024 * 1024)
            throw new InvalidDataException("Plan exceeds 4 MiB limit.");
        return yaml;
    }

    private static void RejectCredentials(JsonNode? node, bool isDocument = false)
    {
        if (node is JsonObject obj)
        {
            if ((bool?)obj["sensitive"] == true && obj["default"] is not null)
                throw new InvalidDataException("Sensitive defaults cannot be stored in a plan.");
            foreach (var (key, value) in obj)
            {
                if (isDocument && key == "inputs") continue;
                if (CredentialKeys.Contains(key) && value is not null)
                {
                    var reference = value is JsonValue scalar && scalar.TryGetValue<string>(out var text) &&
                        text.StartsWith("${{ ", StringComparison.Ordinal) && text.EndsWith(" }}", StringComparison.Ordinal);
                    var secretReference = value is JsonObject secret && secret["provider"] is JsonValue &&
                        secret["key"] is JsonValue && secret.Count == 2;
                    if (!reference && !secretReference)
                        throw new InvalidDataException($"Literal credential field '{key}' cannot be saved. Use a secret reference.");
                }
                RejectCredentials(value);
            }
        }
        else if (node is JsonArray array)
            foreach (var value in array) RejectCredentials(value);
    }

    internal static string SaveDraft(Session session, string? root = null)
    {
        var yaml = Export(session);
        var path = DraftPath(session, root);
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to save a draft through a linked directory.");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User
                ?? throw new IOException("Cannot determine the current Windows user for draft permissions.");
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to overwrite a linked draft.");
        var temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(yaml);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
}

internal sealed record SetupCommandResult(int ExitCode, string Output);

internal sealed record GitHubRepositoryDiscovery(
    IReadOnlyList<string> Repositories, string? CurrentRepository, string? PreferredRepository, string? Notice);

internal sealed record GitHubRepositoryOwner(string Login, bool IsOrganization);

internal sealed class GitHubPlanStorage(
    Func<IReadOnlyList<string>, string?, CancellationToken, Task<SetupCommandResult>>? run = null)
{
    private static bool ValidOwner(string owner) => Regex.IsMatch(owner,
        "^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant);

    internal static bool ValidNewRepositoryName(string name) => name is not "." and not ".." &&
        Regex.IsMatch(name, "^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);

    internal static bool ValidRepository(string repository)
    {
        var parts = repository.Split('/');
        return parts.Length == 2 && ValidOwner(parts[0]) && ValidNewRepositoryName(parts[1]);
    }

    internal async Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken = default) =>
        (await (run ?? RunAsync)(["auth", "status"], null, cancellationToken)).ExitCode == 0;

    internal async Task<GitHubRepositoryDiscovery> DiscoverRepositoriesAsync(
        string? preferredRepository = null, CancellationToken cancellationToken = default)
    {
        var execute = run ?? RunAsync;
        var current = await execute(["repo", "view", "--json", "nameWithOwner,isPrivate,viewerPermission"],
            null, cancellationToken);
        string? currentRepository = null;
        string? notice = null;
        var repositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (current.ExitCode == 0)
        {
            var repo = JsonNode.Parse(current.Output) as JsonObject
                ?? throw new InvalidDataException("GitHub returned an invalid current repository response.");
            currentRepository = RepositoryName(repo);
            if (WritablePrivateRepository(repo))
                repositories.Add(currentRepository);
            else notice = "The current repository is public or not writable; it will not be offered for plan storage.";
        }
        else notice = "gh could not detect the current repository. Choose from your accessible private repositories instead.";

        var listed = await execute(["api", "--paginate",
            "user/repos?visibility=private&affiliation=owner,collaborator,organization_member&per_page=100&sort=full_name",
            "--jq", ".[] | {nameWithOwner: .full_name, isPrivate: .private, canPush: .permissions.push} | @json"],
            null, cancellationToken);
        if (listed.ExitCode != 0)
            throw new InvalidDataException("Cannot list private GitHub repositories. Check gh authentication, network access, and organization authorization. The local draft remains saved.");
        foreach (var line in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var repo = JsonNode.Parse(line) as JsonObject
                ?? throw new InvalidDataException("GitHub returned an invalid repository entry.");
            var name = RepositoryName(repo);
            if (repo["isPrivate"] is not JsonValue privacy || !privacy.TryGetValue<bool>(out var isPrivate) ||
                repo["canPush"] is not JsonValue permission || !permission.TryGetValue<bool>(out var canPush))
                throw new InvalidDataException("GitHub did not return repository privacy and write permissions.");
            if (isPrivate && canPush) repositories.Add(name);
        }
        var preferred = repositories.FirstOrDefault(name =>
            string.Equals(name, preferredRepository, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(preferredRepository) && preferred is null)
            notice = (notice is null ? "" : notice + " ") +
                "The saved plan repository is no longer accessible, private, or writable; choose another repository.";
        var ordered = repositories.OrderBy(name => name != preferred)
            .ThenBy(name => !string.Equals(name, currentRepository, StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new GitHubRepositoryDiscovery(ordered, currentRepository, preferred, notice);
    }

    internal async Task<IReadOnlyList<GitHubRepositoryOwner>> DiscoverOwnersAsync(CancellationToken cancellationToken = default)
    {
        var execute = run ?? RunAsync;
        var user = await execute(["api", "user", "--jq", ".login"], null, cancellationToken);
        if (user.ExitCode != 0 || !ValidOwner(user.Output.Trim()))
            throw new InvalidDataException("Cannot discover your GitHub account. Check gh authentication; no repository was created.");
        var owners = new List<GitHubRepositoryOwner> { new(user.Output.Trim(), false) };
        var organizations = await execute(["api", "--paginate", "user/orgs?per_page=100",
            "--jq", ".[] | {login: .login} | @json"], null, cancellationToken);
        if (organizations.ExitCode != 0)
            throw new InvalidDataException("Cannot list your GitHub organizations. Check gh read:org access and organization SSO authorization; no repository was created.");
        foreach (var line in organizations.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (JsonNode.Parse(line) is not JsonObject org || org["login"] is not JsonValue value ||
                !value.TryGetValue<string>(out var login) || !ValidOwner(login))
                throw new InvalidDataException("GitHub returned an invalid organization owner; no repository was created.");
            if (!owners.Any(owner => string.Equals(owner.Login, login, StringComparison.OrdinalIgnoreCase)))
                owners.Add(new GitHubRepositoryOwner(login, true));
        }
        return owners.OrderBy(owner => owner.IsOrganization)
            .ThenBy(owner => owner.Login, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal async Task<string> CreateRepositoryAsync(string owner, string name, CancellationToken cancellationToken = default)
    {
        if (!ValidOwner(owner) || !ValidNewRepositoryName(name))
            throw new InvalidDataException("Use a discovered GitHub owner and a repository name of 1-100 letters, digits, dots, hyphens, or underscores (not . or ..).");
        var repository = owner + "/" + name;
        var execute = run ?? RunAsync;
        var created = await execute(["repo", "create", repository, "--private"], null, cancellationToken);
        if (created.ExitCode != 0)
            throw new InvalidDataException("GitHub repository creation failed. The name may already exist, or your permissions or organization policy may block creation. No plan was uploaded; the local draft remains saved.");
        var inspected = await execute(["repo", "view", repository, "--json", "nameWithOwner,isPrivate,viewerPermission"],
            null, cancellationToken);
        if (inspected.ExitCode != 0)
            throw new InvalidDataException($"GitHub may have created {repository}, but verification failed. Check the repository before retrying; no plan was uploaded.");
        var repo = JsonNode.Parse(inspected.Output) as JsonObject
            ?? throw new InvalidDataException("GitHub returned an invalid created repository response. Verify the repository before retrying; no plan was uploaded.");
        if (!string.Equals(RepositoryName(repo), repository, StringComparison.OrdinalIgnoreCase) || !WritablePrivateRepository(repo))
            throw new InvalidDataException("GitHub did not confirm the requested writable private repository. Verify the repository before retrying; no plan was uploaded.");
        return repository;
    }

    private static bool WritablePrivateRepository(JsonObject repo)
    {
        if (repo["isPrivate"] is not JsonValue privacy || !privacy.TryGetValue<bool>(out var isPrivate) ||
            repo["viewerPermission"] is not JsonValue permission || !permission.TryGetValue<string>(out var access))
            throw new InvalidDataException("GitHub did not return repository privacy and permissions.");
        return isPrivate && access is "ADMIN" or "MAINTAIN" or "WRITE";
    }

    private static string RepositoryName(JsonObject repo)
    {
        if (repo["nameWithOwner"] is not JsonValue value || !value.TryGetValue<string>(out var name) ||
            !ValidRepository(name))
            throw new InvalidDataException("GitHub returned an invalid owner/repository name.");
        return name;
    }

    internal async Task PublishAsync(Session session, string repository, CancellationToken cancellationToken = default)
    {
        if (!ValidRepository(repository)) throw new InvalidDataException("Use an owner/repository name, not a URL.");
        var yaml = PlanStorage.Export(session);
        var id = (string)session.Plan.Metadata["id"]!;
        _ = PlanStorage.DraftPath(session);
        var path = $"plans/{id}/plan.drp.yaml";
        var parts = repository.Split('/');
        var execute = run ?? RunAsync;
        const string query = "query($owner:String!,$name:String!,$expression:String!){repository(owner:$owner,name:$name){isPrivate object(expression:$expression){... on Blob{oid}}}}";
        var inspect = await execute(["api", "graphql", "-f", "query=" + query,
            "-f", "owner=" + parts[0], "-f", "name=" + parts[1], "-f", "expression=HEAD:" + path], null, cancellationToken);
        if (inspect.ExitCode != 0) throw new InvalidDataException("Cannot inspect the GitHub repository. Check gh authentication and repository access.");
        var response = JsonNode.Parse(inspect.Output) as JsonObject
            ?? throw new InvalidDataException("GitHub returned an invalid repository response.");
        if (response["errors"] is JsonArray errors && errors.Count > 0)
            throw new InvalidDataException("GitHub returned repository errors; nothing was uploaded.");
        var repo = (response["data"] as JsonObject)?["repository"] as JsonObject;
        if (repo?["isPrivate"] is not JsonValue privacy || !privacy.TryGetValue<bool>(out var isPrivate) || !isPrivate)
            throw new InvalidDataException("Plan publication requires an existing private GitHub repository. Nothing was uploaded.");
        var body = new JsonObject
        {
            ["message"] = "Update recovery plan " + id,
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml))
        };
        if (repo["object"] is JsonObject existing)
        {
            if (existing["oid"] is not JsonValue oid || !oid.TryGetValue<string>(out var sha) ||
                !Regex.IsMatch(sha, "^(?:[a-f0-9]{40}|[a-f0-9]{64})$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("The existing GitHub path is not a valid plan file.");
            body["sha"] = sha;
        }
        var upload = await execute(["api", "--method", "PUT", $"repos/{repository}/contents/{path}", "--input", "-"],
            body.ToJsonString(), cancellationToken);
        if (upload.ExitCode != 0)
            throw new InvalidDataException("GitHub plan upload failed. Check write permission or concurrent changes; your local draft remains saved.");
        var uploaded = (JsonNode.Parse(upload.Output) as JsonObject)?["content"] as JsonObject;
        if (uploaded?["sha"] is not JsonValue stored || !stored.TryGetValue<string>(out var storedSha) ||
            !Regex.IsMatch(storedSha, "^(?:[a-f0-9]{40}|[a-f0-9]{64})$", RegexOptions.CultureInvariant) ||
            uploaded["path"] is not JsonValue storedPath || !storedPath.TryGetValue<string>(out var confirmedPath) ||
            confirmedPath != path)
            throw new InvalidDataException("GitHub did not confirm the stored plan. Verify the repository before relying on it.");
    }

    private static async Task<SetupCommandResult> RunAsync(IReadOnlyList<string> arguments, string? input,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo("gh")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
        });
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, timeout);
            var stderr = ReadBoundedAsync(process.StandardError, timeout);
            await process.StandardInput.WriteAsync((input ?? "").AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            return new SetupCommandResult(process.ExitCode, await stdout);
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationTokenSource cancellation)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation.Token)) > 0)
        {
            if (result.Length + count > 1024 * 1024)
            {
                cancellation.Cancel();
                throw new InvalidDataException("GitHub response exceeded the setup output limit.");
            }
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
