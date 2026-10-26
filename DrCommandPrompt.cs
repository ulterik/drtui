using Spectre.Console;

namespace Drtui;

internal static class DrCommandPrompt
{
    internal static string PromptMarkup(DrEnvironment environment, string directory) =>
        $"{environment.PromptMarkup} [grey]{Markup.Escape(directory)}[/] [bold]>[/] ";

    internal static void Open(DrEnvironment environment, Session? session = null)
    {
        AnsiConsole.Clear();
        while (true)
        {
            var prompt = new TextPrompt<string>(PromptMarkup(environment, Environment.CurrentDirectory)).AllowEmpty();
            var result = DrCommandEngine.Execute(environment, AnsiConsole.Prompt(prompt), session);
            AnsiConsole.WriteLine(result.Output);
            if (result.ExitPrompt) return;
        }
    }
}