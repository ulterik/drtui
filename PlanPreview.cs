using System.Text.Json;
using YamlDotNet.Core;

namespace Drtui;

internal sealed record PlanPreview(string Path, Plan? Plan, string? Error)
{
    public string Name => (string?)Plan?.Metadata["name"]
        ?? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) ?? Path;
    public string Version => (string?)Plan?.Metadata["version"] ?? "";

    public static PlanPreview Load(string path)
    {
        try { return new PlanPreview(path, PlanLoader.Load(path), null); }
        catch (Exception error) when (error is InvalidDataException or IOException or YamlException or JsonException)
        {
            return new PlanPreview(path, null, error.Message);
        }
    }
}