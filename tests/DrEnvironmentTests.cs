using Drtui;
using Spectre.Console;
using Xunit;

namespace Drtui.Tests;

public class DrEnvironmentTests
{
    [Fact]
    public void MissingOrLocalEnvironmentCannotRunProductionFailover()
    {
        foreach (var value in new string?[] { null, "", "LOCAL", "local" })
        {
            var environment = DrEnvironment.FromValue(value);
            Assert.Equal(DrExecutionState.Local, environment.State);
            Assert.Equal(DrEnvironment.LocalFailoverError, environment.FailoverError);
            Assert.Contains("[[LOCAL-TEST]] PROXMOX", environment.PromptMarkup);
        }
    }

    [Fact]
    public void RemoteEnvironmentUsesAWarningPromptAndInvalidValuesFailClosed()
    {
        var remote = DrEnvironment.FromValue(" REMOTE ");
        Assert.Equal(DrExecutionState.Remote, remote.State);
        Assert.Null(remote.FailoverError);
        Assert.Contains("[[DR-ACTIVE]] VPS-STANDBY", remote.PromptMarkup);
        Assert.Throws<InvalidDataException>(() => DrEnvironment.FromValue("REMOTE-ish"));
    }

    [Theory]
    [InlineData("LOCAL", "[LOCAL-TEST] PROXMOX")]
    [InlineData("REMOTE", "[DR-ACTIVE] VPS-STANDBY")]
    public void StateIndicatorRendersLiteralBrackets(string value, string expected)
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            Out = new AnsiConsoleOutput(writer)
        });

        console.MarkupLine(DrEnvironment.FromValue(value).PromptMarkup);

        Assert.Contains(expected, writer.ToString());
    }

    [Fact]
    public void LocalAndRemotePromptColorsDifferWhenAnsiIsEnabled()
    {
        static string RenderBadge(string state)
        {
            using var writer = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.Yes,
                Out = new AnsiConsoleOutput(writer)
            });
            console.Markup(DrEnvironment.FromValue(state).PromptMarkup);
            return writer.ToString();
        }

        var local = RenderBadge("LOCAL");
        var remote = RenderBadge("REMOTE");
        Assert.Contains("\x1b[", local);
        Assert.Contains("\x1b[", remote);
        Assert.NotEqual(local, remote);
    }
}