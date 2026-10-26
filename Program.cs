using Drtui;
using Spectre.Console;

try
{
    var argsList = Environment.GetCommandLineArgs().Skip(1).ToArray();
    var environment = DrEnvironment.FromValue(Environment.GetEnvironmentVariable("DR_ENV"));
    if (argsList.FirstOrDefault() == "command")
    {
        var result = DrCommandEngine.Execute(environment, string.Join(' ', argsList.Skip(1)));
        Console.WriteLine(result.Output);
        return result.ExitCode;
    }
    if (argsList.FirstOrDefault() == "shell")
    {
        DrCommandPrompt.Open(environment);
        return 0;
    }
    var checkOnly = argsList.FirstOrDefault() == "check";
    var root = (checkOnly ? argsList.Skip(1) : argsList).FirstOrDefault() ?? "examples";
    if (checkOnly)
    {
        var paths = PlanLoader.Discover(root);
        if (paths.Length == 0) throw new InvalidDataException($"No plan.drp.yaml files found in {root}");
        var blocked = false;
        foreach (var path in paths)
        {
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(path)}[/]");
            var checks = await new Session(PlanLoader.Load(path)).TestAsync();
            ShowChecks(checks);
            blocked |= checks.Any(check => check.Error is not null);
        }
        return blocked ? 1 : 0;
    }

    while (true)
    {
        var paths = PlanLoader.Discover(root);
        if (paths.Length == 0) throw new InvalidDataException($"No plan.drp.yaml files found in {root}");
        var selection = PlanPicker.Select(paths.Select(PlanPreview.Load).ToArray(), environment);
        if (selection is null) break;
        try { await PlanConfiguration.OpenAsync(new Session(selection.Plan!), environment); }
        catch (Exception error) when (error is InvalidDataException or IOException or YamlDotNet.Core.YamlException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error.Message)}[/]");
        }
    }
    return 0;
}
catch (Exception error) when (error is InvalidDataException or IOException or YamlDotNet.Core.YamlException)
{
    if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() == "command")
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { error = error.Message }));
    else AnsiConsole.MarkupLine($"[red]{Markup.Escape(error.Message)}[/]");
    return 1;
}

static void ShowChecks(IReadOnlyList<Check> checks)
{
    var table = new Table().AddColumn("Readiness").AddColumn("Status");
    foreach (var check in checks)
        table.AddRow(Markup.Escape(check.Label), check.Error is null
            ? "[green]OK[/]" : $"[red]BLOCKED: {Markup.Escape(check.Error)}[/]");
    AnsiConsole.Write(table);
}