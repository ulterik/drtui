using System.Text.Json;
using System.Text.Json.Nodes;
using Drtui;
using Spectre.Console;
using Xunit;

namespace Drtui.Tests;

public class DrCommandTests
{
    [Theory]
    [InlineData("failover")]
    [InlineData("failover --force")]
    [InlineData("restore-postgresql --force")]
    [InlineData("dns-cutover --force")]
    public void LocalProductionCommandsAreRejectedRegardlessOfForce(string command)
    {
        var result = DrCommandEngine.Execute(DrEnvironment.FromValue("LOCAL"), command);
        using var payload = JsonDocument.Parse(result.Output);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(DrEnvironment.LocalFailoverError, payload.RootElement.GetProperty("error").GetString());
        Assert.Equal("--force", payload.RootElement.GetProperty("requiredFlag").GetString());
    }

    [Fact]
    public void RemoteFailoverRequiresForceAndStillCannotRunWithoutAProviderHost()
    {
        var remote = DrEnvironment.FromValue("REMOTE");
        using var missingForce = JsonDocument.Parse(DrCommandEngine.Execute(remote, "failover").Output);
        Assert.Contains("--force", missingForce.RootElement.GetProperty("error").GetString());

        var blocked = DrCommandEngine.Execute(remote, "failover --force");
        using var result = JsonDocument.Parse(blocked.Output);
        Assert.Equal(1, blocked.ExitCode);
        Assert.Contains("Provider host not implemented", result.RootElement.GetProperty("error").GetString());
        Assert.Equal("--force", result.RootElement.GetProperty("requiredFlag").GetString());
    }

    [Theory]
    [InlineData("LOCAL", "[LOCAL-TEST] PROXMOX")]
    [InlineData("REMOTE", "[DR-ACTIVE] VPS-STANDBY")]
    public void CommandInputPromptContainsColoredStateAndDirectory(string state, string badge)
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Markup(DrCommandPrompt.PromptMarkup(DrEnvironment.FromValue(state), @"C:\dr[site]"));

        Assert.Contains(badge, writer.ToString());
        Assert.Contains(@"C:\dr[site]", writer.ToString());
        Assert.EndsWith("> ", writer.ToString());
    }

    [Fact]
    public void CommandResponsesAreJsonAndNeverExecuteShellInput()
    {
        var local = DrEnvironment.FromValue(null);
        using var status = JsonDocument.Parse(DrCommandEngine.Execute(local, "status").Output);
        Assert.Equal("LOCAL", status.RootElement.GetProperty("state").GetString());
        Assert.False(status.RootElement.GetProperty("liveRecovery").GetBoolean());

        var invalid = DrCommandEngine.Execute(local, "status; curl example.com");
        using var invalidPayload = JsonDocument.Parse(invalid.Output);
        Assert.Equal(1, invalid.ExitCode);
        Assert.Contains("Unrecognized", invalidPayload.RootElement.GetProperty("error").GetString());
        Assert.True(DrCommandEngine.Execute(local, "exit").ExitPrompt);
    }

    [Fact]
    public void SelectedPlanCheckReportsReadinessWithoutExecutingRecovery()
    {
        var document = JsonNode.Parse("""{"metadata":{"name":"Preview"},"providers":{"storage":{"kind":"storage","source":"file://storage"}},"steps":[]}""")!.AsObject();
        var result = DrCommandEngine.Execute(DrEnvironment.FromValue("LOCAL"), "check", new Session(new Plan(document)));
        using var payload = JsonDocument.Parse(result.Output);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("local-readiness-only", payload.RootElement.GetProperty("scope").GetString());
        Assert.Equal(0, payload.RootElement.GetProperty("recoveryStepsExecuted").GetInt32());
        Assert.Contains(payload.RootElement.GetProperty("checks").EnumerateArray(), check =>
            check.GetProperty("label").GetString() == "provider host");

        using var withoutPlan = JsonDocument.Parse(DrCommandEngine.Execute(DrEnvironment.FromValue("LOCAL"), "check").Output);
        Assert.Contains("Select a plan first", withoutPlan.RootElement.GetProperty("error").GetString());
    }
}