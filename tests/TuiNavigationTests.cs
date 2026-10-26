using Drtui;
using Spectre.Console;
using Xunit;

namespace Drtui.Tests;

public class TuiNavigationTests
{
    [Fact]
    public void RepeatedNavigationMovesToTheLastSelectedRowBeforeEnter()
    {
        var result = TuiNavigation.Reduce(0, 5,
            [ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter, ConsoleKey.DownArrow],
            PlanPicker.Move, static key => key == ConsoleKey.Enter);

        Assert.Equal(2, result.Selected);
        Assert.Equal(ConsoleKey.Enter, result.Command);
    }

    [Fact]
    public void CommandsStopBufferedNavigationWithoutSkippingBoundaries()
    {
        var result = TuiNavigation.Reduce(0, 3,
            [ConsoleKey.UpArrow, ConsoleKey.PageDown, ConsoleKey.Escape, ConsoleKey.Home],
            PlanConfiguration.Move, static key => key == ConsoleKey.Escape);

        Assert.Equal(2, result.Selected);
        Assert.Equal(ConsoleKey.Escape, result.Command);
        Assert.Equal((0, (ConsoleKey?)null), TuiNavigation.Reduce(0, 0,
            [ConsoleKey.DownArrow], PlanConfiguration.Move, static _ => false));
    }

    [Fact]
    public void HeldKeyStillLeavesTimeForRedraws()
    {
        var input = new EndlessArrowKeys();
        var batch = TuiNavigation.BufferedKeys(ConsoleKey.DownArrow, input).ToArray();

        Assert.Equal(8, batch.Length);
        Assert.Equal(7, input.ReadCount);
        Assert.All(batch, key => Assert.Equal(ConsoleKey.DownArrow, key));
    }

    private sealed class EndlessArrowKeys : IAnsiConsoleInput
    {
        public int ReadCount { get; private set; }
        public bool IsKeyAvailable() => true;
        public ConsoleKeyInfo? ReadKey(bool intercept)
        {
            ReadCount++;
            return new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false);
        }

        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken) =>
            Task.FromResult(ReadKey(intercept));
    }
}