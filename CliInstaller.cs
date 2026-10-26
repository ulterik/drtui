using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;

namespace Drtui;

internal enum CliInstallMethod { Winget, Npm, Manual }

internal sealed record CliInstallOption(string Tool, CliInstallMethod Method, string? PackageId = null)
{
    private string DisplayName => CliToolCatalog.ForExecutable(Tool)?.Name ??
        (Tool == "node" ? "Node.js LTS" : Tool);

    public string Label => Method switch
    {
        CliInstallMethod.Winget => $"{DisplayName} ({Tool}) - Winget ({PackageId})",
        CliInstallMethod.Npm => $"{DisplayName} ({Tool}) - npm",
        CliInstallMethod.Manual when Tool == CliToolCatalog.Proxmox.Executable =>
            $"{DisplayName} ({Tool}) - WSL setup guidance",
        _ => $"{DisplayName} ({Tool}) - installation guidance"
    };
}

internal static class CliInstaller
{
    private sealed record Choice(string Label, CliInstallOption? Option = null);

    internal static IReadOnlyList<string> ProxmoxWslSteps { get; } =
    [
        "No verified native Windows or Winget package exists for proxmox-backup-client; WSL setup is manual.",
        "In Windows, install WSL with Debian if it is not already available: wsl --install -d Debian (may require administrator approval and a restart).",
        "Open Debian WSL and configure Proxmox's Backup Client-only APT repository and signing key for the distro's Debian release: https://pbs.proxmox.com/docs/installation.html#apt-based-proxmox-backup-client-repository",
        "In Debian WSL, run: sudo apt update && sudo apt install proxmox-backup-client",
        "Verify inside WSL: proxmox-backup-client version",
        "WSL /mnt/c access may fail with EACCES and does not provide Windows VSS snapshots. Proxmox staff describe backups of Windows files from WSL as untested.",
        "Run the .NET TUI inside the same WSL distro (with .NET 10 installed) to check its Linux PATH; the provider host is still unavailable.",
        "The Windows CLI check and PBS runner do not invoke WSL; installing there does not unblock native readiness or live recovery."
    ];

    internal static Table RenderApprovedTools(IReadOnlyList<Check> checks, Func<string, bool> toolExists)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Teal)
            .AddColumn("[bold aqua]Approved tool[/]")
            .AddColumn("[bold aqua]Command[/]")
            .AddColumn("[bold aqua]Plan[/]")
            .AddColumn("[bold aqua]Status[/]");
        foreach (var tool in CliToolCatalog.All)
        {
            var required = checks.FirstOrDefault(check => check.Label == "CLI " + tool.Executable);
            var status = required?.Error is not null ? "[red]missing[/]" :
                toolExists(tool.Executable) ? "[green]found[/]" : "[yellow]not installed[/]";
            table.AddRow(Markup.Escape(tool.Name), Markup.Escape(tool.Executable),
                required is null ? "[grey]optional[/]" : "[bold]required[/]", status);
        }
        return table;
    }

    internal static IReadOnlyList<CliInstallOption> Options(IReadOnlyList<Check> checks, bool isWindows,
        bool npmAvailable, Func<string, bool>? toolExists = null)
    {
        if (!isWindows) return [];

        toolExists ??= Session.ToolExists;
        var options = new List<CliInstallOption>();
        foreach (var approved in CliToolCatalog.All)
        {
            var tool = approved.Executable;
            if (checks.All(check => check.Label != "CLI " + tool || check.Error is null) && toolExists(tool))
                continue;
            if (approved.WingetPackageId is { } wingetId)
            {
                options.Add(new CliInstallOption(tool, CliInstallMethod.Winget, wingetId));
            }
            else if (tool == CliToolCatalog.Cloudflare.Executable)
            {
                if (!npmAvailable)
                    options.Add(new CliInstallOption("node", CliInstallMethod.Winget, "OpenJS.NodeJS.LTS"));
                options.Add(new CliInstallOption(tool, npmAvailable ? CliInstallMethod.Npm : CliInstallMethod.Manual));
            }
            else options.Add(new CliInstallOption(tool, CliInstallMethod.Manual));
        }
        return options;
    }

    internal static ProcessStartInfo CommandFor(CliInstallOption option)
    {
        var packageId = option.Tool == "node" ? "OpenJS.NodeJS.LTS" :
            CliToolCatalog.ForExecutable(option.Tool)?.WingetPackageId;
        if (option.Method == CliInstallMethod.Winget && packageId is not null && packageId == option.PackageId)
        {
            var command = new ProcessStartInfo("winget.exe") { UseShellExecute = false };
            foreach (var argument in new[] { "install", "--id", packageId, "--exact", "--source", "winget" })
                command.ArgumentList.Add(argument);
            return command;
        }
        if (option.Method == CliInstallMethod.Npm && option.Tool == "wrangler")
        {
            var command = new ProcessStartInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { UseShellExecute = false };
            foreach (var argument in new[] { "/d", "/c", "npm", "install", "--global", "wrangler" })
                command.ArgumentList.Add(argument);
            return command;
        }
        throw new InvalidOperationException("No verified installer for this tool.");
    }

    internal static void RemoveSecrets(ProcessStartInfo command, IReadOnlyList<Check> checks)
    {
        foreach (var check in checks.Where(check => check.Label.StartsWith("Environment ", StringComparison.Ordinal)))
            command.Environment.Remove(check.Label[12..]);
        foreach (var variable in new[] { "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN", "NPM_TOKEN" })
            command.Environment.Remove(variable);
    }

    internal static string MergePaths(params string?[] values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = values.Where(value => !string.IsNullOrEmpty(value))
            .SelectMany(value => value!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .Select(folder => Environment.ExpandEnvironmentVariables(folder.Trim()))
            .Where(folder => folder.Length > 0 && seen.Add(folder));
        return string.Join(Path.PathSeparator, folders);
    }

    private static void RefreshPath() => Environment.SetEnvironmentVariable("PATH", MergePaths(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)));

    public static async Task<bool> OpenAsync(IReadOnlyList<Check> checks)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(RenderApprovedTools(checks, Session.ToolExists));
        AnsiConsole.MarkupLine("[grey]Optional tools do not block this plan.[/]");
        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[yellow]Winget installation is available only on Windows.[/]");
            Back();
            return false;
        }

        var options = Options(checks, true, Session.ToolExists("npm"));
        if (options.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]All approved CLI tools are on PATH.[/]");
            Back();
            return false;
        }

        var choices = options.Select(option => new Choice(option.Label, option))
            .Prepend(new Choice("Back to settings")).ToArray();
        var choice = AnsiConsole.Prompt(new SelectionPrompt<Choice>()
            .Title("[bold aqua]Install approved CLI tools[/]")
            .PageSize(8)
            .UseConverter(item => Markup.Escape(item.Label))
            .AddChoices(choices));
        if (choice.Option is not { } selected) return false;

        if (selected.Method == CliInstallMethod.Manual)
        {
            if (selected.Tool == CliToolCatalog.Proxmox.Executable)
            {
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine("[bold aqua]Proxmox backup client in Debian WSL[/]");
                foreach (var step in ProxmoxWslSteps)
                    AnsiConsole.WriteLine(step);
                Back();
                return false;
            }
            var guidance = selected.Tool switch
            {
                "wrangler" => "Wrangler is distributed via npm, not Winget. Install Node.js LTS with Winget, open a new terminal, then install Wrangler with npm.",
                _ => "No verified Winget package for this tool. Follow its vendor's installation instructions."
            };
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(selected.Tool)}:[/] {Markup.Escape(guidance)}");
            Back();
            return false;
        }

        if (selected.Method == CliInstallMethod.Winget && !Session.ToolExists("winget"))
        {
            AnsiConsole.MarkupLine("[red]Winget is not on PATH. Install or update Windows App Installer first.[/]");
            Back();
            return false;
        }

        var displayCommand = selected.Method == CliInstallMethod.Winget
            ? $"winget install --id {selected.PackageId} --exact --source winget"
            : "npm install --global wrangler";
        AnsiConsole.MarkupLine($"[bold]Command:[/] {Markup.Escape(displayCommand)}");
        var confirmation = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Run this installer in the current terminal?")
            .AddChoices("Cancel", "Install"));
        if (confirmation != "Install") return false;

        var attempted = false;
        try
        {
            var command = CommandFor(selected);
            RemoveSecrets(command, checks);
            using var process = new Process { StartInfo = command };
            if (!process.Start()) throw new InvalidOperationException("The installer did not start.");
            attempted = true;
            await process.WaitForExitAsync();
            if (process.ExitCode == 0) RefreshPath();
            AnsiConsole.MarkupLine(process.ExitCode == 0
                ? "[green]Installer finished. Checking PATH; a new terminal may be needed for updated PATH values.[/]"
                : $"[red]Installer exited with code {process.ExitCode}.[/]");
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[red]Could not start the installer: {Markup.Escape(error.Message)}[/]");
        }
        Back();
        return attempted;
    }

    private static void Back() => AnsiConsole.Prompt(new SelectionPrompt<string>().AddChoices("Back to settings"));
}