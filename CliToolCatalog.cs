namespace Drtui;

internal sealed record ApprovedCliTool(string Name, string Executable, string? WingetPackageId = null);

internal static class CliToolCatalog
{
    public static readonly ApprovedCliTool Cloudflare = new("Cloudflare", "wrangler");
    public static readonly ApprovedCliTool Aws = new("AWS", "aws", "Amazon.AWSCLI");
    public static readonly ApprovedCliTool Terraform = new("Terraform", "terraform", "Hashicorp.Terraform");
    public static readonly ApprovedCliTool GitHub = new("GitHub", "gh", "GitHub.cli");
    public static readonly ApprovedCliTool Proxmox = new("Proxmox", "proxmox-backup-client");

    public static IReadOnlyList<ApprovedCliTool> All { get; } = Array.AsReadOnly(
        new[] { Cloudflare, Aws, Terraform, GitHub, Proxmox });

    public static ApprovedCliTool? ForExecutable(string executable) =>
        All.FirstOrDefault(tool => string.Equals(tool.Executable, executable, StringComparison.Ordinal));
}