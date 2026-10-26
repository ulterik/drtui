using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Drtui;

internal static class PlanConfiguration
{
    private sealed record SettingRow(string Section, string Name, string Value, string Status,
        SetupField? Field = null, string? Detail = null, bool Blocked = false)
    {
        public bool IsUnverified => Status.Contains("unverified", StringComparison.Ordinal) || Status == "not verified";
    }
    private const int PageSize = 4;

    public static async Task OpenAsync(Session session, DrEnvironment? environment = null)
    {
        environment ??= DrEnvironment.FromValue(null);
        var checks = await RecheckAsync(session, environment);
        var selected = 0;
        while (true)
        {
            var setup = PlanSetup.From(session, checks);
            var rows = Settings(setup);
            selected = Math.Clamp(selected, 0, Math.Max(0, rows.Count - 1));
            AnsiConsole.Clear();
            var key = AnsiConsole.Live(Render(session, setup, rows, AnsiConsole.Console.Profile.Width, selected, environment))
                .AutoClear(true)
                .Start(context =>
                {
                    context.Refresh();
                    while (true)
                    {
                        var input = AnsiConsole.Console.Input;
                        var key = input.ReadKey(true);
                        if (key is null) return (ConsoleKey?)null;
                        var (next, command) = TuiNavigation.Reduce(selected, rows.Count,
                            TuiNavigation.BufferedKeys(key.Value.Key, input), Move,
                            static value => value is ConsoleKey.Enter or ConsoleKey.Escape or ConsoleKey.Q
                                or ConsoleKey.P or ConsoleKey.R or ConsoleKey.I or ConsoleKey.C);
                        if (next != selected)
                        {
                            selected = next;
                            context.UpdateTarget(Render(session, setup, rows, AnsiConsole.Console.Profile.Width, selected, environment));
                            context.Refresh();
                        }
                        if (command == ConsoleKey.Enter && (rows.Count == 0 || rows[selected].Field is null)) continue;
                        if (command is not null) return command;
                    }
                });

            if (key is null or ConsoleKey.Escape or ConsoleKey.Q) return;
            if (key == ConsoleKey.P)
            {
                ShowProviders(setup.ProviderBindings, environment);
                continue;
            }
            if (key == ConsoleKey.C)
            {
                DrCommandPrompt.Open(environment, session);
                continue;
            }
            if (key == ConsoleKey.I)
            {
                if (await CliInstaller.OpenAsync(checks)) checks = await RecheckAsync(session, environment);
                continue;
            }
            if (key == ConsoleKey.Enter)
            {
                var field = rows[selected].Field!;
                Edit(field, environment);
                checks = field.Label.EndsWith(" / AWS profile", StringComparison.Ordinal)
                    ? await RecheckAsync(session, environment) : session.CheckWithCachedProfiles(checks);
            }
            else checks = await RecheckAsync(session, environment);
        }
    }

    private static async Task<IReadOnlyList<Check>> RecheckAsync(Session session, DrEnvironment environment)
    {
        AnsiConsole.MarkupLine($"{environment.PromptMarkup} [grey]Checking local setup...[/]");
        return await session.TestAsync();
    }

    internal static int Move(int selected, int count, ConsoleKey key) => count == 0 ? 0 : key switch
    {
        ConsoleKey.DownArrow or ConsoleKey.J => Math.Min(count - 1, selected + 1),
        ConsoleKey.UpArrow or ConsoleKey.K => Math.Max(0, selected - 1),
        ConsoleKey.PageDown => Math.Min(count - 1, selected + PageSize),
        ConsoleKey.PageUp => Math.Max(0, selected - PageSize),
        ConsoleKey.Home => 0,
        ConsoleKey.End => count - 1,
        _ => selected
    };

    internal static IRenderable Render(Session session, PlanSetup setup, int width, int selected = 0, DrEnvironment? environment = null) =>
        Render(session, setup, Settings(setup), width, selected, environment ?? DrEnvironment.FromValue(null));

    private static IRenderable Render(Session session, PlanSetup setup, IReadOnlyList<SettingRow> rows, int width, int selected,
        DrEnvironment environment)
    {
        var name = (string?)session.Plan.Metadata["name"] ?? "Plan";
        var version = (string?)session.Plan.Metadata["version"] ?? "";
        selected = Math.Clamp(selected, 0, Math.Max(0, rows.Count - 1));
        var lines = new List<IRenderable>
        {
            new Markup($"[bold aqua]DRTUI[/] [grey]/[/] {environment.PromptMarkup} " +
                $"[bold]{Markup.Escape(Shorten(name, Math.Max(18, width - 40)))}[/] [grey]v{Markup.Escape(version)}[/]")
        };

        var tools = setup.LocalDependencies.Where(check => check.Label.StartsWith("CLI ", StringComparison.Ordinal)).ToArray();
        if (tools.Length == 0) lines.Add(new Markup("[grey]CLI tools: None required[/]"));
        else
        {
            var toolTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Teal)
                .AddColumn("[bold aqua]CLI tools[/]").AddColumn("[bold aqua]Value[/]").AddColumn("[bold aqua]Status[/]");
            foreach (var tool in tools)
                toolTable.AddRow(Markup.Escape(Shorten(tool.Label[4..], Math.Max(20, width / 2))), "PATH",
                    tool.Error is null ? "[green]found[/]" : "[red]missing[/]");
            lines.Add(toolTable);
        }

        if (rows.Count > 0)
        {
            var first = Math.Clamp(selected - 2, 0, Math.Max(0, rows.Count - PageSize));
            var page = rows.Select((row, index) => (Row: row, Index: index)).Skip(first).Take(PageSize).ToArray();
            lines.Add(FieldTable(page, selected, width, first + 1, rows.Count));

            var current = rows[selected];
            var valueColor = current.Blocked || current.IsUnverified
                ? "yellow" : "green";
            var selectedValue = current.Value.ReplaceLineEndings(" ").Replace('\t', ' ');
            var detail = current.Field?.Error ?? current.Detail;
            if (current.Name.Length > SettingNameWidth(width))
                detail = string.IsNullOrWhiteSpace(detail) ? current.Name : $"{detail} | {current.Name}";
            var detailWidth = width - $"Value: {selectedValue} | ".Length - 2;
            var suffix = string.IsNullOrWhiteSpace(detail) || detailWidth < 8 ? ""
                : $" [grey]| {Markup.Escape(Shorten(detail, detailWidth))}[/]";
            lines.Add(new Markup($"[bold {valueColor}]Value: {Markup.Escape(selectedValue)}[/]{suffix}"));
        }
        else lines.Add(new Markup("[grey]No plan settings[/]"));

        lines.Add(Detail($"Providers: {string.Join(", ", session.Plan.Providers.Select(item => item.Key))} (P to view)", width));
        lines.Add(new Markup("[yellow]Live recovery unavailable:[/] [grey]provider host not implemented.[/]"));
        lines.Add(new Markup("[grey]Up/Down | Enter edit | C command | I install | P providers | R check | Esc[/]"));
        return new Rows(lines);
    }

    private static IReadOnlyList<SettingRow> Settings(PlanSetup setup)
    {
        var rows = setup.Inputs.Select(field => FieldRow("Inputs", field))
            .Concat(setup.VaultSettings.Select(field => FieldRow("Keyvault settings", field))).ToList();
        foreach (var check in setup.LocalDependencies.Where(check => !check.Label.StartsWith("CLI ", StringComparison.Ordinal)))
        {
            var environment = check.Label.StartsWith("Environment ", StringComparison.Ordinal);
            var profile = check.Label.StartsWith("AWS profile ", StringComparison.Ordinal);
            var status = check.Error is not null ? environment ? "missing" : "needs attention"
                : environment ? "present (unverified)" : profile ? "listed (unverified)" : "local check passed";
            rows.Add(new SettingRow("Local setup", check.Label,
                environment ? check.Error is null ? "<set>" : "<not set>" : profile ? check.Label[12..] : "-",
                status, Detail: check.Error is null ? "Remote access not verified." : Requirement(check),
                Blocked: check.Error is not null));
        }
        foreach (var requirement in setup.Secrets)
            foreach (var secret in requirement.Names)
                rows.Add(new SettingRow("Secrets", $"{requirement.Vault} / {secret}", "<hidden>", "not verified",
                    Detail: "Secret availability and access are not checked locally."));
        return rows;
    }

    private static SettingRow FieldRow(string section, SetupField field) => new(section,
        $"{field.Label} ({field.Type}{(field.Required ? " *" : "")})", field.DisplayValue,
        field.Error is not null ? field.Error is "required" or "required input is missing" ? "required" : "invalid"
            : field.IsDefault ? "default (unverified)" : "set (unverified)",
        field, field.Description, field.Error is not null);

    private static Table FieldTable(IReadOnlyList<(SettingRow Row, int Index)> page, int selected, int width, int first, int total)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Teal)
            .AddColumn($"[bold aqua]Settings[/] [grey]({first}-{first + page.Count - 1} of {total})[/]")
            .AddColumn("[bold aqua]Status[/]");
        string? section = null;
        foreach (var (row, index) in page)
        {
            if (section != row.Section)
            {
                section = row.Section;
                table.AddRow($"[bold aqua]{Markup.Escape(section)}[/]", "");
            }
            var marker = index == selected ? "[black on aqua]>[/] " : "  ";
            var label = Markup.Escape(ShortenSetting(row.Name, SettingNameWidth(width)));
            var status = Markup.Escape(Shorten(row.Status, Math.Max(20, width / 4)));
            table.AddRow(marker + label,
                row.Blocked ? $"[red]{status}[/]" : row.IsUnverified
                    ? $"[yellow]{status}[/]" : $"[green]{status}[/]");
        }
        return table;
    }

    private static Markup Detail(string text, int width) => new(
        $"[grey]{Markup.Escape(Shorten(text.ReplaceLineEndings(" "), Math.Max(20, width - 2)))}[/]");

    private static int SettingNameWidth(int width) => Math.Max(20, width - Math.Max(20, width / 4) - 10);

    private static string ShortenSetting(string name, int length)
    {
        var separator = name.IndexOf(" / ", StringComparison.Ordinal);
        if (name.Length <= length || separator < 0) return Shorten(name, length);
        var setting = name[(separator + 3)..];
        return setting.Length > length - 6 ? Shorten(setting, length)
            : Shorten(name[..separator], length - setting.Length - 3) + " / " + setting;
    }

    private static string Requirement(Check check)
    {
        if (check.Label.StartsWith("CLI ", StringComparison.Ordinal))
            return $"Install {check.Label[4..]} and add it to PATH";
        if (check.Label.StartsWith("AWS profile ", StringComparison.Ordinal))
            return $"Verify local AWS CLI profile {check.Label[12..]} (missing or could not be listed)";
        if (check.Label.StartsWith("Environment ", StringComparison.Ordinal))
            return $"Set environment variable {check.Label[12..]} for the Cloudflare API token";
        return $"{check.Label}: {check.Error}";
    }

    private static void Edit(SetupField field, DrEnvironment environment)
    {
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(field.Label)}[/] [grey]({Markup.Escape(field.Type)})[/]");
        if (!string.IsNullOrWhiteSpace(field.Description))
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(field.Description)}[/]");

        var plainText = field.Type is "text" or "string" or "path" or "ip";
        var current = field.RawValue is null ? "" : plainText ? field.DisplayValue : field.RawValue;
        var prompt = new TextPrompt<string>($"{environment.PromptMarkup} " +
            (plainText ? "Value:" : "JSON value:")).AllowEmpty();
        if (current.Length > 0) prompt.DefaultValue(current);
        var entered = AnsiConsole.Prompt(prompt);
        field.Save(Encode(field, entered));
    }

    internal static string? Encode(SetupField field, string entered)
    {
        if (string.IsNullOrWhiteSpace(entered)) return null;
        return field.Type is "string" or "path" or "ip" ? JsonSerializer.Serialize(entered) : entered;
    }

    private static void ShowProviders(IReadOnlyList<ProviderBinding> providers, DrEnvironment environment)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"{environment.PromptMarkup} [bold]Provider settings from plan[/]");
        AnsiConsole.Write(RenderProviders(providers, AnsiConsole.Console.Profile.Width));
        AnsiConsole.MarkupLine("[grey]Credentials and secret values are not displayed.[/]");
        AnsiConsole.Prompt(new SelectionPrompt<string>().AddChoices("Back to configuration"));
    }

    internal static IRenderable RenderProviders(IReadOnlyList<ProviderBinding> providers, int width = 80)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Teal)
            .AddColumn("[bold aqua]Setting[/]").AddColumn("[bold aqua]Value[/]").AddColumn("[bold aqua]Status[/]");
        foreach (var provider in providers)
        {
            table.AddRow($"[bold aqua]{Markup.Escape(provider.Name)} ({Markup.Escape(provider.Kind)})[/]", "", "");
            foreach (var setting in provider.Settings)
                table.AddRow(Markup.Escape(Shorten(setting.Name, Math.Max(15, width / 4))),
                    Markup.Escape(Shorten(setting.Value, Math.Max(12, width / 3))),
                    "[yellow]plan value (unverified)[/]");
        }
        return table;
    }

    private static string Shorten(string value, int length)
    {
        var singleLine = value.ReplaceLineEndings(" ").Replace('\t', ' ');
        return singleLine.Length > length ? singleLine[..(length - 3)] + "..." : singleLine;
    }
}