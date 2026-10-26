using Spectre.Console;

namespace Drtui;

internal static class TuiNavigation
{
    internal static IEnumerable<ConsoleKey> BufferedKeys(ConsoleKey first, IAnsiConsoleInput input)
    {
        yield return first;
        for (var count = 1; count < 8 && input.IsKeyAvailable(); count++)
        {
            var next = input.ReadKey(true);
            yield return next?.Key ?? ConsoleKey.Escape;
            if (next is null) yield break;
        }
    }

    internal static (int Selected, ConsoleKey? Command) Reduce(int selected, int count,
        IEnumerable<ConsoleKey> keys, Func<int, int, ConsoleKey, int> move, Func<ConsoleKey, bool> isCommand)
    {
        foreach (var key in keys)
        {
            if (isCommand(key)) return (selected, key);
            selected = move(selected, count, key);
        }
        return (selected, null);
    }
}