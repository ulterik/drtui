using System.Text.Json;

namespace Drtui;

internal sealed record DrCommandResult(string Output, int ExitCode = 0, bool ExitPrompt = false);

internal static class DrCommandEngine
{
    private static readonly HashSet<string> ProductionActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "failover", "restore-postgresql", "restore-mongodb", "deploy-coolify", "dns-cutover"
    };

    internal static DrCommandResult Execute(DrEnvironment environment, string commandLine, Session? session = null)
    {
        var parts = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Error("DR_ERR_03: Enter a command.");

        var command = parts[0].ToLowerInvariant();
        if (ProductionActions.Contains(command))
        {
            if (environment.FailoverError is { } localError)
                return Error(localError, "--force");
            if (parts.Length != 2 || parts[1] != "--force")
                return Error("DR_ERR_04: Production actions require --force and confirmation.", "--force");
            return Error("DR_ERR_05: Provider host not implemented; live recovery is unavailable.", "--force");
        }

        if (parts.Length != 1) return Error("DR_ERR_06: Unrecognized command or arguments.");
        return command switch
        {
            "check" when session is not null => new DrCommandResult(JsonSerializer.Serialize(new
            {
                state = environment.State.ToString().ToUpperInvariant(),
                scope = "local-readiness-only",
                recoveryStepsExecuted = 0,
                plan = (string?)session.Plan.Metadata["name"],
                checks = session.Check().Select(check => new { label = check.Label, error = check.Error })
            })),
            "check" => Error("DR_ERR_07: Select a plan first, or use the check subcommand with a plan path."),
            "status" => new DrCommandResult(JsonSerializer.Serialize(new
            {
                state = environment.State.ToString().ToUpperInvariant(),
                providerHost = "not implemented",
                liveRecovery = false
            })),
            "help" => new DrCommandResult(JsonSerializer.Serialize(new
            {
                commands = new[] { "status", "check", "help", "exit", "failover --force" },
                readiness = "Check reports local readiness for a selected plan; recovery execution is unavailable."
            })),
            "exit" or "back" => new DrCommandResult("{\"exit\":true}", ExitPrompt: true),
            _ => Error("DR_ERR_06: Unrecognized command or arguments.")
        };
    }

    private static DrCommandResult Error(string message, string? requiredFlag = null) =>
        new(JsonSerializer.Serialize(new { error = message, requiredFlag }), ExitCode: 1);
}