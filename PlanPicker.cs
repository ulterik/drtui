using Spectre.Console;
using Spectre.Console.Rendering;

namespace Drtui;

internal static class PlanPicker
{
    public static PlanPreview? Select(IReadOnlyList<PlanPreview> previews, DrEnvironment? environment = null, bool startWithSetup = false)
    {
        environment ??= DrEnvironment.FromValue(null);
        var selected = 0;
        while (true)
        {
            AnsiConsole.Clear();
            var command = AnsiConsole.Live(Render(previews, selected, AnsiConsole.Console.Profile.Width, environment, startWithSetup))
                .AutoClear(true)
                .Start(context =>
                {
                    context.Refresh();
                    while (true)
                    {
                        var input = AnsiConsole.Console.Input;
                        var key = input.ReadKey(true);
                        if (key is null) return ConsoleKey.Escape;
                        var (next, action) = TuiNavigation.Reduce(selected, previews.Count + 1,
                            TuiNavigation.BufferedKeys(key.Value.Key, input), Move,
                            static value => value is ConsoleKey.Enter or ConsoleKey.Q or ConsoleKey.Escape or ConsoleKey.C);
                        if (next != selected)
                        {
                            selected = next;
                            context.UpdateTarget(Render(previews, selected, AnsiConsole.Console.Profile.Width, environment, startWithSetup));
                            context.Refresh();
                        }
                        if (action == ConsoleKey.Enter)
                        {
                            if (selected == previews.Count) return ConsoleKey.Escape;
                            if (previews[selected].Plan is not null) return ConsoleKey.Enter;
                        }
                        else if (action is not null) return action.Value;
                    }
                });
            if (command == ConsoleKey.C)
            {
                DrCommandPrompt.Open(environment);
                continue;
            }
            return command == ConsoleKey.Enter ? previews[selected] : null;
        }
    }

    internal static int Move(int selected, int count, ConsoleKey key) => key switch
    {
        ConsoleKey.DownArrow or ConsoleKey.J => Math.Min(count - 1, selected + 1),
        ConsoleKey.UpArrow or ConsoleKey.K => Math.Max(0, selected - 1),
        ConsoleKey.PageDown => Math.Min(count - 1, selected + 5),
        ConsoleKey.PageUp => Math.Max(0, selected - 5),
        ConsoleKey.Home => 0,
        ConsoleKey.End => count - 1,
        _ => selected
    };

    internal static IRenderable Render(IReadOnlyList<PlanPreview> previews, int selected, int width, DrEnvironment? environment = null,
        bool startWithSetup = false)
    {
        environment ??= DrEnvironment.FromValue(null);
        var planList = new Table().Border(TableBorder.Rounded).BorderColor(Color.Teal)
            .AddColumn("[bold aqua]Plan[/]").AddColumn("[bold aqua]Version[/]");
        var first = Math.Clamp(selected - 2, 0, Math.Max(0, previews.Count - 5));
        foreach (var index in Enumerable.Range(first, Math.Min(5, previews.Count - first)))
        {
            var preview = previews[index];
            var name = Markup.Escape(Shorten(preview.Name, Math.Clamp(width - 24, 18, 70)));
            var marker = index == selected ? "[black on aqua]>[/] " : "  ";
            var version = preview.Error is null ? $"[grey]{Markup.Escape(preview.Version)}[/]" : "[red]INVALID[/]";
            planList.AddRow(marker + (index == selected ? $"[bold aqua]{name}[/]" : name), version);
        }
        planList.AddRow(selected == previews.Count ? "[black on aqua]>[/] Exit" : "  Exit", "");

        var footer = selected < previews.Count && previews[selected].Error is { } error
            ? $"[red]Cannot open: {Markup.Escape(Shorten(error.ReplaceLineEndings(" "), Math.Max(20, width - 15)))}[/]"
            : startWithSetup
                ? "[grey]Enter start setup  |  C command  |  Up/Down browse  |  Esc exit[/]"
                : "[grey]Enter configure  |  C command  |  Up/Down browse  |  Esc exit[/]";
        var lines = new List<IRenderable>
        {
            new Markup($"[bold aqua]DRTUI[/] [grey]/[/] {environment.PromptMarkup} " +
                $"{(startWithSetup ? "Choose a plan to set up" : "Select a plan")} [grey]({previews.Count})[/]"),
            planList
        };
        if (selected < previews.Count && previews[selected].Plan is { } plan)
        {
            lines.Add(new Panel(Detail((string?)plan.Metadata["description"] ?? "No description provided.", width - 4))
                .Border(BoxBorder.Rounded).BorderColor(Color.Teal));
        }
        lines.Add(new Markup(footer));
        return new Rows(lines);
    }

    private static Markup Detail(string text, int width) => new(
        $"[grey]{Markup.Escape(Shorten(text.ReplaceLineEndings(" "), Math.Max(20, width - 2)))}[/]");

    private static string Shorten(string value, int length) => value.Length > length ? value[..(length - 3)] + "..." : value;
}