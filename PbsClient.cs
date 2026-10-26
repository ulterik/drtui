using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Drtui;

public sealed record PbsConfig(string Server, string Datastore, string AuthId, string Fingerprint,
    string WorkDirectory, string PasswordProvider, string PasswordKey, string Namespace = "",
    int Port = 8007, string ClientPath = "proxmox-backup-client");
public sealed record PbsStatus(string ServerVersion, ulong TotalBytes, ulong UsedBytes, ulong AvailableBytes);
public sealed record PbsSnapshot(string Id, string Group, DateTimeOffset Timestamp, ulong SizeBytes, string[] Archives);
public sealed record PbsArchive(string Name, ulong SizeBytes, string CryptMode, string Fingerprint);
public sealed record PbsRestoreResult(string Snapshot, string Archive, string LocalDestination);
public sealed record PbsCommand(string Path, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, byte[] Stdin);
public sealed record PbsCommandResult(byte[] Stdout, byte[] Stderr, int ExitCode);

public sealed class PbsProviderException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public interface IPbsCommandRunner
{
    Task<PbsCommandResult> RunAsync(PbsCommand command, CancellationToken cancellationToken);
}

public sealed class PbsProcessRunner : IPbsCommandRunner
{
    public async Task<PbsCommandResult> RunAsync(PbsCommand command, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(command.Path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Cast<string>().Where(key =>
                     key.StartsWith("PBS_", StringComparison.OrdinalIgnoreCase) ||
                     key.StartsWith("PROXMOX_OUTPUT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        foreach (var (key, value) in command.Environment) start.Environment[key] = value;

        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = cancellationToken.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, cancellationToken);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, cancellationToken);
            await process.StandardInput.BaseStream.WriteAsync(command.Stdin, cancellationToken);
            process.StandardInput.Close();
            var output = await stdout;
            var errors = await stderr;
            await process.WaitForExitAsync(cancellationToken);
            return new PbsCommandResult(output, errors, process.ExitCode);
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > 4 * 1024 * 1024)
                throw new InvalidDataException("PBS command output exceeded limit");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
}

public sealed class PbsClient
{
    private static readonly Regex AuthId = Pattern(@"^[A-Za-z0-9_.-]+@[A-Za-z0-9_.-]+(?:![A-Za-z0-9_.-]+)?$");
    private static readonly Regex Datastore = Pattern(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$");
    private static readonly Regex Fingerprint = Pattern(@"^(?:[0-9a-fA-F]{2}:){31}[0-9a-fA-F]{2}$");
    private static readonly Regex Group = Pattern(@"^(?:vm|ct|host)/[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$");
    private static readonly Regex Namespace = Pattern(@"^[A-Za-z0-9][A-Za-z0-9_.-]*(?:/[A-Za-z0-9][A-Za-z0-9_.-]*)*$");
    private static readonly Regex Snapshot = Pattern(@"^(?:vm|ct|host)/[A-Za-z0-9][A-Za-z0-9_.-]{0,127}/[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$");
    private static readonly Regex Archive = Pattern(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,254}$");

    private readonly PbsConfig config;
    private readonly IPbsCommandRunner runner;
    private readonly Func<string, string, CancellationToken, Task<byte[]>> resolveSecret;

    public PbsClient(PbsConfig config, Func<string, string, CancellationToken, Task<byte[]>> resolveSecret,
        IPbsCommandRunner? runner = null)
    {
        if (string.IsNullOrWhiteSpace(config.Server) || config.Server.StartsWith('-') ||
            config.Server.IndexOfAny(['\r', '\n', '\0', ' ', '/', '\\']) >= 0 &&
            !IPAddress.TryParse(config.Server.Trim('[', ']'), out _))
            throw new ArgumentException("Valid PBS server is required");
        if (!Datastore.IsMatch(config.Datastore) || !AuthId.IsMatch(config.AuthId) ||
            !Fingerprint.IsMatch(config.Fingerprint) || !ValidNamespace(config.Namespace) ||
            string.IsNullOrWhiteSpace(config.WorkDirectory) ||
            string.IsNullOrWhiteSpace(config.PasswordProvider) || string.IsNullOrWhiteSpace(config.PasswordKey))
            throw new ArgumentException("Invalid PBS configuration");
        this.config = config with { WorkDirectory = Path.GetFullPath(config.WorkDirectory) };
        this.resolveSecret = resolveSecret ?? throw new ArgumentNullException(nameof(resolveSecret));
        this.runner = runner ?? new PbsProcessRunner();
    }

    public async Task<PbsStatus> ProbeRepositoryAsync(CancellationToken cancellationToken = default)
    {
        using var version = JsonDocument.Parse(await RunAsync(["version", "--output-format", "json"], "", cancellationToken));
        using var status = JsonDocument.Parse(await RunAsync(["status", "--output-format", "json"], "", cancellationToken));
        return new PbsStatus(Text(version.RootElement, "server-version", "server_version", "server"),
            Number(status.RootElement, "total", "total-bytes", "total_bytes"),
            Number(status.RootElement, "used", "used-bytes", "used_bytes"),
            Number(status.RootElement, "avail", "available", "available-bytes", "available_bytes"));
    }

    public async Task<IReadOnlyList<PbsSnapshot>> ListSnapshotsAsync(string group = "", string ns = "",
        CancellationToken cancellationToken = default)
    {
        if (group != "" && !Group.IsMatch(group)) throw new ArgumentException("Invalid backup group");
        if (!ValidNamespace(ns)) throw new ArgumentException("Invalid PBS namespace");
        var args = new List<string> { "snapshot", "list" };
        if (group != "") args.Add(group);
        args.AddRange(["--output-format", "json"]);
        using var output = JsonDocument.Parse(await RunAsync(args, ns == "" ? config.Namespace : ns, cancellationToken));
        return output.RootElement.EnumerateArray().Select(row =>
        {
            var id = Text(row, "snapshot");
            if (id == "" && Number(row, "backup-time", "backup_time") is var time && time > 0)
                id = $"{Text(row, "backup-type", "backup_type")}/{Text(row, "backup-id", "backup_id")}/{DateTimeOffset.FromUnixTimeSeconds((long)time):yyyy-MM-ddTHH:mm:ssZ}";
            if (!Snapshot.IsMatch(id) || !DateTimeOffset.TryParse(id[(id.LastIndexOf('/') + 1)..], out var timestamp))
                throw new InvalidDataException("PBS returned an invalid snapshot ID");
            var files = row.TryGetProperty("files", out var field)
                ? field.ValueKind switch
                {
                    JsonValueKind.Array => field.EnumerateArray().Select(item => item.GetString() ?? "").ToArray(),
                    JsonValueKind.String => (field.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    JsonValueKind.Null => [],
                    _ => throw new InvalidDataException("PBS returned invalid archive names")
                } : [];
            return new PbsSnapshot(id, id[..id.LastIndexOf('/')], timestamp, Number(row, "size"), files);
        }).ToArray();
    }

    public async Task<IReadOnlyList<PbsArchive>> ListSnapshotFilesAsync(string snapshot, string ns = "",
        CancellationToken cancellationToken = default)
    {
        ValidateSnapshot(snapshot, ns);
        using var output = JsonDocument.Parse(await RunAsync(
            ["snapshot", "files", snapshot, "--output-format", "json"],
            ns == "" ? config.Namespace : ns, cancellationToken));
        return output.RootElement.EnumerateArray().Select(row =>
        {
            var name = Text(row, "filename", "name");
            if (!Archive.IsMatch(name)) throw new InvalidDataException("PBS returned an invalid archive name");
            return new PbsArchive(name, Number(row, "size"), Text(row, "crypt-mode", "crypt_mode"), Text(row, "fingerprint"));
        }).ToArray();
    }

    public async Task<PbsRestoreResult> RestoreArchiveAsync(string snapshot, string archive, string destination,
        string ns = "", bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ValidateSnapshot(snapshot, ns);
        if (!Archive.IsMatch(archive)) throw new ArgumentException("Invalid archive name");
        if (destination is "" or "-") throw new ArgumentException("Restore destination must be a filesystem path");
        var target = Path.GetFullPath(destination, config.WorkDirectory);
        var relative = Path.GetRelativePath(config.WorkDirectory, target);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative)) throw new ArgumentException("Restore destination escapes the provider work directory");
        var args = new List<string> { "restore", snapshot, archive, target };
        if (overwrite) args.AddRange(["--overwrite", "true"]);
        await RunAsync(args, ns == "" ? config.Namespace : ns, cancellationToken);
        return new PbsRestoreResult(snapshot, archive, target);
    }

    private async Task<byte[]> RunAsync(IReadOnlyList<string> args, string ns, CancellationToken cancellationToken)
    {
        byte[] password;
        try { password = await resolveSecret(config.PasswordProvider, config.PasswordKey, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new PbsProviderException("AUTHENTICATION_FAILED", "Resolve PBS credential failed");
        }
        try
        {
            if (password.Length == 0 || password.Contains((byte)'\r') || password.Contains((byte)'\n'))
                throw new PbsProviderException("AUTHENTICATION_FAILED", "PBS credential is empty or contains a newline");
            var stdin = new byte[password.Length + 1];
            password.CopyTo(stdin, 0);
            stdin[^1] = (byte)'\n';
            try
            {
                var commandArgs = args.Concat(["--server", config.Server, "--port", config.Port.ToString(),
                    "--datastore", config.Datastore, "--auth-id", config.AuthId]).ToList();
                if (ns != "") commandArgs.AddRange(["--ns", ns]);
                PbsCommandResult result;
                try
                {
                    result = await runner.RunAsync(new PbsCommand(config.ClientPath, commandArgs,
                        new Dictionary<string, string>
                        {
                            ["PBS_FINGERPRINT"] = config.Fingerprint, ["PBS_LOG"] = "error", ["PBS_PASSWORD_FD"] = "0",
                            ["PROXMOX_OUTPUT_FORMAT"] = "json", ["PROXMOX_OUTPUT_NO_BORDER"] = "1",
                            ["PROXMOX_OUTPUT_NO_HEADER"] = "1"
                        }, stdin), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new PbsProviderException("TIMEOUT", "PBS client deadline exceeded", true);
                }
                catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
                {
                    throw new PbsProviderException("INTERNAL", "PBS client command failed");
                }
                if (result.ExitCode != 0) throw ClassifyFailure(result.Stderr);
                return result.Stdout;
            }
            finally { Array.Clear(stdin); }
        }
        finally { Array.Clear(password); }
    }

    private static PbsProviderException ClassifyFailure(byte[] stderr)
    {
        var message = System.Text.Encoding.UTF8.GetString(stderr).ToLowerInvariant();
        if (message.Contains("authentication") || message.Contains("permission denied") || message.Contains("unauthorized"))
            return new("AUTHENTICATION_FAILED", "PBS authentication failed");
        if (message.Contains("not found") || message.Contains("does not exist"))
            return new("NOT_FOUND", "PBS resource not found");
        if (message.Contains("connection") || message.Contains("timed out") || message.Contains("temporarily unavailable"))
            return new("TRANSIENT", "PBS repository is unavailable", true);
        return new("INTERNAL", "PBS client command failed");
    }

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static bool ValidNamespace(string ns) => ns == "" || Namespace.IsMatch(ns);
    private static void ValidateSnapshot(string snapshot, string ns)
    {
        if (!Snapshot.IsMatch(snapshot) || !ValidNamespace(ns)) throw new ArgumentException("Invalid PBS snapshot reference");
    }
    private static string Text(JsonElement row, params string[] keys)
    {
        foreach (var key in keys)
            if (row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        return "";
    }
    private static ulong Number(JsonElement row, params string[] keys)
    {
        foreach (var key in keys)
            if (row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetUInt64(out var number)) return number;
        return 0;
    }
}