using Drtui;
using Spectre.Console;
using Xunit;

namespace Drtui.Tests;

public class CliInstallerTests
{
    [Fact]
    public void CatalogContainsApprovedToolsIncludingTheSeparateTunnelConnector()
    {
        Assert.Equal(new[] { "Cloudflare", "AWS", "Terraform", "GitHub", "Proxmox", "Coolify", "Cloudflare Tunnel" },
            CliToolCatalog.All.Select(tool => tool.Name));
        Assert.Equal(new[] { "cf", "aws", "terraform", "gh", "proxmox-backup-client", "coolify", "cloudflared" },
            CliToolCatalog.All.Select(tool => tool.Executable));
        Assert.Null(CliToolCatalog.ForExecutable("user-defined-tool"));
    }

    [Fact]
    public void ApprovedToolsViewDistinguishesRequiredAndOptionalAtEightyColumns()
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;
        console.Write(CliInstaller.RenderApprovedTools(
            [new Check("CLI cf", "missing"), new Check("CLI aws"), new Check("CLI custom-host", "missing")],
            executable => executable is "aws" or "gh"));

        var lines = writer.ToString().Split('\n');
        Assert.Contains(lines, line => line.Contains("Cloudflare") && line.Contains("cf") &&
            line.Contains("required") && line.Contains("missing"));
        Assert.Contains(lines, line => line.Contains("AWS") && line.Contains("aws") &&
            line.Contains("required") && line.Contains("found"));
        Assert.Contains(lines, line => line.Contains("Terraform") && line.Contains("terraform") &&
            line.Contains("optional") && line.Contains("not installed"));
        Assert.Contains(lines, line => line.Contains("GitHub") && line.Contains("gh") &&
            line.Contains("optional") && line.Contains("found"));
        Assert.Contains(lines, line => line.Contains("Proxmox") && line.Contains("proxmox-backup-client") &&
            line.Contains("optional") && line.Contains("not installed"));
        Assert.Contains(lines, line => line.Contains("custom-host") && line.Contains("required") && line.Contains("missing"));
        Assert.True(lines.Length <= 24, $"Approved tools view exceeds 24 rows:\n{writer}");
        Assert.All(lines, line => Assert.True(line.TrimEnd('\r').Length <= 80, $"Line exceeds 80 columns: {line}"));
    }

    [Fact]
    public void OptionalApprovedToolsAreInstallableButDoNotBecomePlanRequirements()
    {
        var checks = new[] { new Check("CLI aws") };
        var options = CliInstaller.Options(checks, isWindows: true, npmAvailable: true,
            toolExists: executable => executable == "aws");

        Assert.DoesNotContain(options, option => option.Tool == "aws");
        Assert.Contains(options, option => option.Tool == "gh" && option.PackageId == "GitHub.cli");
        Assert.Contains(options, option => option.Tool == "cf" && option.Method == CliInstallMethod.Npm);
        Assert.Contains(options, option => option.Tool == "proxmox-backup-client" && option.Method == CliInstallMethod.Manual);
        Assert.Single(checks);
    }

    [Fact]
    public void MissingToolsOfferOnlyVerifiedWingetPackages()
    {
        var checks = new[]
        {
            new Check("CLI aws", "missing"),
            new Check("CLI terraform", "missing"),
            new Check("CLI gh", "missing"),
            new Check("CLI cf", "missing"),
            new Check("CLI proxmox-backup-client", "missing"),
            new Check("CLI user-defined-tool", "missing"),
            new Check("CLI installed-tool")
        };
        var options = CliInstaller.Options(checks, isWindows: true, npmAvailable: true);

        Assert.Contains(options, option => option is { Tool: "aws", Method: CliInstallMethod.Winget, PackageId: "Amazon.AWSCLI" });
        Assert.Contains(options, option => option is { Tool: "terraform", Method: CliInstallMethod.Winget, PackageId: "Hashicorp.Terraform" });
        Assert.Contains(options, option => option is { Tool: "gh", Method: CliInstallMethod.Winget, PackageId: "GitHub.cli" });
        Assert.Contains(options, option => option is { Tool: "cf", Method: CliInstallMethod.Npm });
        Assert.Contains(options, option => option is { Tool: "proxmox-backup-client", Method: CliInstallMethod.Manual });
        var custom = Assert.Single(options, option => option.Tool == "user-defined-tool");
        Assert.Equal(CliInstallMethod.Manual, custom.Method);
        Assert.Throws<InvalidOperationException>(() => CliInstaller.CommandFor(custom));
        Assert.DoesNotContain(options, option => option.Tool == "installed-tool");
        Assert.Empty(CliInstaller.Options(checks, isWindows: false, npmAvailable: true));
    }

    [Fact]
    public void MissingNpmOffersVerifiedNodePrerequisiteWithoutMislabelingCf()
    {
        var options = CliInstaller.Options([new Check("CLI cf", "missing")], isWindows: true, npmAvailable: false);
        Assert.Contains(options, option => option is { Tool: "node", Method: CliInstallMethod.Winget, PackageId: "OpenJS.NodeJS.LTS" });
        Assert.Contains(options, option => option is { Tool: "cf", Method: CliInstallMethod.Manual });
    }

    [Fact]
    public void ProxmoxOnWindowsProvidesWslStepsWithoutTreatingItAsANativeInstall()
    {
        var option = Assert.Single(CliInstaller.Options([new Check("CLI proxmox-backup-client", "missing")],
            isWindows: true, npmAvailable: true, toolExists: tool => tool != "proxmox-backup-client"),
            option => option.Tool == "proxmox-backup-client");

        Assert.Equal(CliInstallMethod.Manual, option.Method);
        Assert.Contains("WSL setup guidance", option.Label);
        Assert.Contains(CliInstaller.ProxmoxWslSteps, step => step.Contains("wsl --install -d Debian"));
        Assert.Contains(CliInstaller.ProxmoxWslSteps, step => step.Contains("sudo apt install proxmox-backup-client"));
        Assert.Contains(CliInstaller.ProxmoxWslSteps, step => step.Contains("proxmox-backup-client version"));
        Assert.Contains(CliInstaller.ProxmoxWslSteps, step => step.Contains("EACCES") && step.Contains("VSS"));
        Assert.Contains(CliInstaller.ProxmoxWslSteps, step => step.Contains("same WSL distro") && step.Contains(".NET 10"));
        Assert.Contains(CliInstaller.ProxmoxWslSteps, step => step.Contains("does not unblock native readiness"));
        Assert.Throws<InvalidOperationException>(() => CliInstaller.CommandFor(option));
    }

    [Fact]
    public void InstallerCommandsUseApprovedIdsAndFixedArguments()
    {
        var winget = CliInstaller.CommandFor(new CliInstallOption("aws", CliInstallMethod.Winget, "Amazon.AWSCLI"));
        Assert.Equal("winget.exe", winget.FileName);
        Assert.False(winget.UseShellExecute);
        Assert.Equal(new[] { "install", "--id", "Amazon.AWSCLI", "--exact", "--source", "winget" }, winget.ArgumentList.ToArray());

        var github = CliInstaller.CommandFor(new CliInstallOption("gh", CliInstallMethod.Winget, "GitHub.cli"));
        Assert.Equal(new[] { "install", "--id", "GitHub.cli", "--exact", "--source", "winget" }, github.ArgumentList.ToArray());

        var npm = CliInstaller.CommandFor(new CliInstallOption("cf", CliInstallMethod.Npm));
        Assert.EndsWith("cmd.exe", npm.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.False(npm.UseShellExecute);
        Assert.Equal(new[] { "/d", "/c", "npm", "install", "--global", "cf" }, npm.ArgumentList.ToArray());
        Assert.Throws<InvalidOperationException>(() => CliInstaller.CommandFor(
            new CliInstallOption("wrangler", CliInstallMethod.Npm)));
        Assert.Throws<InvalidOperationException>(() => CliInstaller.CommandFor(
            new CliInstallOption("cloudflared", CliInstallMethod.Npm)));

        Assert.Throws<InvalidOperationException>(() => CliInstaller.CommandFor(
            new CliInstallOption("user-defined-tool", CliInstallMethod.Winget, "Untrusted.Package")));
        Assert.Throws<InvalidOperationException>(() => CliInstaller.CommandFor(
            new CliInstallOption("aws", CliInstallMethod.Winget, "Untrusted.Package")));
        Assert.Throws<InvalidOperationException>(() => CliInstaller.CommandFor(
            new CliInstallOption("proxmox-backup-client", CliInstallMethod.Manual)));
    }

    [Fact]
    public void InstallerDoesNotPassPlanTokensToPackageManagers()
    {
        var command = CliInstaller.CommandFor(new CliInstallOption("cf", CliInstallMethod.Npm));
        command.Environment["CLOUDFLARE_API_TOKEN"] = "do-not-share";
        command.Environment["AWS_SECRET_ACCESS_KEY"] = "do-not-share";
        command.Environment["NPM_TOKEN"] = "do-not-share";

        CliInstaller.RemoveSecrets(command, [new Check("Environment CLOUDFLARE_API_TOKEN")]);

        Assert.False(command.Environment.ContainsKey("CLOUDFLARE_API_TOKEN"));
        Assert.False(command.Environment.ContainsKey("AWS_SECRET_ACCESS_KEY"));
        Assert.False(command.Environment.ContainsKey("NPM_TOKEN"));
    }

    [Fact]
    public void InstallerRemovesProviderCredentialsWithoutAKeyvaultEnvironmentCheck()
    {
        var command = CliInstaller.CommandFor(new CliInstallOption("cf", CliInstallMethod.Npm));
        var variables = new[] { "CLOUDFLARE_API_TOKEN", "CLOUDFLARE_API_KEY", "TUNNEL_TOKEN",
            "COOLIFY_API_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" };
        foreach (var variable in variables) command.Environment[variable] = "not-for-the-installer";
        CliInstaller.RemoveSecrets(command, []);
        Assert.All(variables, variable => Assert.False(command.Environment.ContainsKey(variable)));
    }

    [Fact]
    public void PathRefreshPreservesExistingFoldersWithoutDuplicates()
    {
        var separator = Path.PathSeparator;
        Assert.Equal($"old{separator}new", CliInstaller.MergePaths($"old{separator}new", "NEW", "old"));
    }
}