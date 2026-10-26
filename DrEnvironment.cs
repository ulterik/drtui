namespace Drtui;

internal enum DrExecutionState { Local, Remote }

internal sealed record DrEnvironment(DrExecutionState State)
{
    public const string LocalFailoverError = "DR_ERR_01: Cannot execute production failover from local node.";

    public static DrEnvironment FromValue(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        null or "" or "LOCAL" => new DrEnvironment(DrExecutionState.Local),
        "REMOTE" => new DrEnvironment(DrExecutionState.Remote),
        _ => throw new InvalidDataException("DR_ERR_02: DR_ENV must be LOCAL or REMOTE.")
    };

    public string PromptMarkup => State == DrExecutionState.Remote
        ? "[white on red][[DR-ACTIVE]] VPS-STANDBY[/]"
        : "[white on blue][[LOCAL-TEST]] PROXMOX[/]";

    public string? FailoverError => State == DrExecutionState.Local ? LocalFailoverError : null;
}