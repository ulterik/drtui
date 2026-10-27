using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Spectre.Console;

namespace Drtui;

internal sealed record SetupTask(string Kind, string Label, string Guidance, SetupField? Field = null);

internal static class PlanSetupWizard
{
    internal const string NewRepositoryChoice = "Create a new private repository";

    internal static IReadOnlyList<SetupTask> Tasks(Session session, IReadOnlyList<Check> checks)
    {
        var setup = PlanSetup.From(session, checks);
        var tasks = new List<SetupTask>();
        var incompatibleTargets = setup.LocalDependencies.Where(check =>
            check.Label.StartsWith("target compatibility ", StringComparison.Ordinal) && check.Error is not null).ToArray();
        if (incompatibleTargets.Length > 0)
            return incompatibleTargets.Select(check => new SetupTask("guidance", check.Label, check.Error!)).ToArray();
        tasks.AddRange(setup.LocalDependencies.Where(check => check.Label.StartsWith("hosting ", StringComparison.Ordinal) &&
            check.Error is not null).Select(check => new SetupTask("hosting", check.Label,
                "Choose a recovery compute provider. Cloudflare Containers/Sandbox and Linux/VPS providers use different recovery workflows.")));
        tasks.AddRange(setup.LocalDependencies.Where(check => check.Label.StartsWith("CLI ", StringComparison.Ordinal) &&
            check.Error is not null).Select(check => new SetupTask("tool", check.Label,
                "Install only this plan's required tool. Every installer requires confirmation; no recovery resources are created.")));
        tasks.AddRange(setup.Inputs.Concat(setup.VaultSettings).Where(field => field.Error is not null)
            .Select(field => new SetupTask("field", field.Label, field.Description ?? "Configure this plan requirement.", field)));
        tasks.AddRange(setup.LocalDependencies.Where(check => check.Label.StartsWith("Environment ", StringComparison.Ordinal) &&
            check.Error is not null).Select(check => new SetupTask("environment", check.Label,
                "Use a scoped bootstrap API token. Enter it masked for this process only, never into the plan or GitHub.")));
        tasks.AddRange(setup.LocalDependencies.Where(check => check.Error is not null &&
            !check.Label.StartsWith("hosting ", StringComparison.Ordinal) &&
            !check.Label.StartsWith("CLI ", StringComparison.Ordinal) &&
            !check.Label.StartsWith("Environment ", StringComparison.Ordinal))
            .Select(check => new SetupTask("guidance", check.Label, check.Error!)));
        return tasks;
    }

    internal static string? ValidateField(Session session, SetupField field, string entered)
    {
        if (session.Plan.Inputs[field.Label] is { } definition)
            return Session.ValidateInput(field.Type, field.Required, PlanConfiguration.Encode(field, entered),
                (string?)definition["pattern"]);
        if (field.Label.EndsWith(" / Cloudflare account ID", StringComparison.Ordinal))
            return Session.ValidateCloudflareAccountId(entered);
        return field.Required && string.IsNullOrWhiteSpace(entered) ? "required" : null;
    }

    internal static ProcessStartInfo LoginCommand(string tool)
    {
        string[] arguments = tool switch
        {
            "cf" => ["auth", "login"],
            "gh" => ["auth", "login", "--web", "--git-protocol", "https"],
            "hcloud" => ["context", "create", "drtui-recovery"],
            "doctl" => ["auth", "init"],
            _ => throw new InvalidOperationException("No verified automated login for this CLI; follow the vendor's instructions.")
        };
        var command = OperatingSystem.IsWindows() && tool == "cf"
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            : new ProcessStartInfo(tool);
        command.UseShellExecute = false;
        if (OperatingSystem.IsWindows() && tool == "cf")
            foreach (var argument in new[] { "/d", "/c", "cf" }) command.ArgumentList.Add(argument);
        foreach (var argument in arguments) command.ArgumentList.Add(argument);
        if (tool == "cf") command.Environment.Remove("CLOUDFLARE_API_TOKEN");
        return command;
    }

    internal static async Task<Session> OpenAsync(Session session, DrEnvironment environment)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
            throw new InvalidDataException("Guided setup requires an interactive terminal. Use check for non-interactive readiness.");
        var draft = PlanStorage.DraftPath(session);
        if (File.Exists(draft) && AnsiConsole.Confirm("Resume the saved local draft for this plan?", true))
        {
            var saved = PlanLoader.Load(draft);
            if ((string?)saved.Metadata["id"] != (string?)session.Plan.Metadata["id"])
                throw new InvalidDataException("Saved draft belongs to another plan.");
            session = new Session(saved);
        }
        var firstPass = true;
        while (true)
        {
            var checks = await session.TestAsync();
            var tasks = Tasks(session, checks);
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"{environment.PromptMarkup} [bold aqua]Guided plan setup[/]");
            AnsiConsole.WriteLine($"{tasks.Count} local requirements need attention. Remote access and recovery execution are not verified.");
            AnsiConsole.WriteLine("Recommended: private GitHub for the non-secret plan; Cloudflare Secrets Store for credentials.");
            AnsiConsole.WriteLine("Setup never provisions servers, changes DNS, or starts recovery.");

            var action = firstPass && tasks.Count > 0 ? "Configure missing requirements" :
                AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Next setup action").AddChoices(
                    "Configure missing requirements", "Review hosting selection", "Authentication and secret guidance",
                    "Save draft and publish to private GitHub", "Recheck local setup", "Back to configuration"));
            firstPass = false;
            if (action == "Back to configuration") return session;
            try
            {
                if (action == "Configure missing requirements")
                {
                    if (tasks.Count == 0) AnsiConsole.WriteLine("No missing local requirements. Authentication and provider execution remain unverified.");
                    var backToMenu = false;
                    foreach (var task in tasks)
                    {
                        AnsiConsole.MarkupLine($"[bold aqua]{Markup.Escape(task.Label)}[/]");
                        AnsiConsole.WriteLine(task.Guidance);
                        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().AddChoices(
                            "Configure now (recommended)", "Skip for now", "Back to setup menu"));
                        if (choice == "Back to setup menu")
                        {
                            backToMenu = true;
                            break;
                        }
                        if (choice == "Skip for now") continue;
                        if (task.Kind == "hosting")
                        {
                            ConfigureHosting(session, task.Label[8..]);
                            var updated = session.Check();
                            if (updated.Any(check => check.Label.StartsWith("target compatibility ", StringComparison.Ordinal) &&
                                check.Error is not null))
                            {
                                Save(session);
                                backToMenu = true;
                                break;
                            }
                        }
                        else if (task.Kind == "tool")
                            await CliInstaller.OpenAsync(checks.Where(check => check.Label == task.Label ||
                                check.Label.StartsWith("Environment ", StringComparison.Ordinal)).ToArray(), requiredOnly: true);
                        else if (task.Kind == "field")
                        {
                            var field = task.Field!;
                            PlanConfiguration.Edit(field, environment, entered => ValidateField(session, field, entered));
                        }
                        else if (task.Kind == "environment")
                        {
                            var variable = task.Label[12..];
                            var token = AnsiConsole.Prompt(new TextPrompt<string>("Bootstrap token (masked, session only):").Secret());
                            Environment.SetEnvironmentVariable(variable, token, EnvironmentVariableTarget.Process);
                        }
                        else Acknowledge();
                        Save(session);
                    }
                    if (backToMenu) continue;
                    if (AnsiConsole.Confirm("Review CLI authentication and recovery-secret setup?", true))
                        await AuthenticationAsync(session);
                    if (AnsiConsole.Confirm("Save and offer publication to your private GitHub repository?", true))
                        await PublishAsync(session);
                    Acknowledge();
                }
                else if (action == "Review hosting selection")
                {
                    foreach (var provider in HostingSetup.Providers(session.Plan).ToArray())
                        ConfigureHosting(session, provider.Key);
                    Save(session);
                }
                else if (action == "Authentication and secret guidance")
                    await AuthenticationAsync(session);
                else if (action == "Save draft and publish to private GitHub")
                    await PublishAsync(session);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or
                Win32Exception or JsonException or OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[red]Setup action failed:[/] {Markup.Escape(error.Message)}");
                AnsiConsole.WriteLine("No success is assumed. Fix the requirement or skip it; recovery remains blocked.");
                Acknowledge();
            }
        }
    }

    internal static void ConfigureHosting(Session session, string name)
    {
        var options = HostingSetup.Choices.Select(choice => choice.Name)
            .Concat(["Other Linux/VPS provider", "Leave unselected"]).ToArray();
        var selected = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Which provider will host the recovered workloads?").AddChoices(options));
        if (selected == "Leave unselected") return;
        var choice = HostingSetup.Choices.FirstOrDefault(choice => choice.Name == selected);
        var provider = choice?.Provider ?? AnsiConsole.Ask<string>("Provider identifier (lowercase, no spaces):");
        var executable = choice?.Executable ?? AnsiConsole.Ask<string>("Provider's CLI executable (not a command):");
        HostingSetup.Configure(session.Plan.Providers[name]!, provider, executable);
        AnsiConsole.WriteLine(choice?.Documentation ?? "Follow your hosting provider's official CLI installation and authentication documentation.");
        if (choice?.TargetKind == "cloudflare-container")
        {
            AnsiConsole.WriteLine("Cloudflare selected with cf. Containers/Sandbox runs Linux microVM compute through Workers/Durable Objects, not the SSH host interface used by this plan.");
            if (HostingSetup.CompatibilityIssue(session.Plan, session.Plan.Providers[name]!) is { } compatibility)
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(compatibility)}[/]");
        }
        else AnsiConsole.WriteLine("Use a dedicated recovery project and scoped credentials. Select a region, size, and immutable image supported by this provider.");
        AnsiConsole.WriteLine("This saves a provider selection only. No server is provisioned; the execution adapter is still planned.");
    }

    private static async Task AuthenticationAsync(Session session)
    {
        var tools = session.Check().Where(check => check.Label.StartsWith("CLI ", StringComparison.Ordinal))
            .Select(check => check.Label[4..]).ToArray();
        foreach (var tool in tools)
        {
            if (!Session.ToolExists(tool)) { AnsiConsole.WriteLine($"{tool}: install the required CLI first."); continue; }
            if (tool is "cf" or "gh" or "hcloud" or "doctl")
            {
                if (!AnsiConsole.Confirm($"Start {tool}'s interactive login? Vendor-managed credentials stay outside the plan.", false))
                    continue;
                await LoginAsync(tool);
            }
            else AnsiConsole.WriteLine($"{tool}: configure scoped authentication using the vendor's CLI; do not put tokens in plan inputs or shell command arguments.");
        }
        foreach (var requirement in PlanSetup.From(session, session.Check()).Secrets)
        {
            AnsiConsole.WriteLine($"Configure secret names in {requirement.Vault}: {string.Join(", ", requirement.Names)}");
            AnsiConsole.WriteLine("Use the provider's secure console. Secret existence and permissions are not checked by this local setup.");
        }
        AnsiConsole.WriteLine("cf login does not set the bootstrap CLOUDFLARE_API_TOKEN required by this plan; guided setup accepts that token masked, for this process only.");
        AnsiConsole.WriteLine("R2 credentials should be read-only for the backup bucket. DNS/Tunnel credentials should be scoped to the recovery account/zone.");
        Acknowledge();
    }

    private static void Save(Session session)
    {
        var path = PlanStorage.SaveDraft(session);
        AnsiConsole.WriteLine($"Saved a restricted, non-secret local draft: {path}");
    }

    private static async Task LoginAsync(string tool)
    {
        using var process = Process.Start(LoginCommand(tool)) ?? throw new IOException("Login process did not start.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException($"{tool} login exited with code {process.ExitCode}.");
        AnsiConsole.WriteLine($"{tool} login completed. This does not verify recovery-provider access.");
    }

    internal static string? SelectRepository(GitHubRepositoryDiscovery discovery)
    {
        if (discovery.Notice is not null) AnsiConsole.WriteLine(discovery.Notice);
        if (discovery.Repositories.Count == 0)
            AnsiConsole.WriteLine("gh found no writable private repositories. You can create one now or keep the local draft.");
        const string back = "Keep the local draft without uploading";
        var selected = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Choose a private plan repository discovered by gh")
            .PageSize(10).EnableSearch()
            .UseConverter(name => Markup.Escape(name == discovery.PreferredRepository ? name + " (saved plan repository)" :
                string.Equals(name, discovery.CurrentRepository, StringComparison.OrdinalIgnoreCase) ? name + " (current repository)" : name))
            .AddChoices(discovery.Repositories.Concat([NewRepositoryChoice, back])));
        return selected == back ? null : selected;
    }

    internal static async Task<string?> ChooseRepositoryAsync(GitHubPlanStorage storage, GitHubRepositoryDiscovery discovery)
    {
        while (true)
        {
            var selected = SelectRepository(discovery);
            if (selected != NewRepositoryChoice) return selected;
            var owners = await storage.DiscoverOwnersAsync();
            const string back = "Back to repository selection";
            AnsiConsole.WriteLine("Choose your account or an organization. Organization policies and your token permissions may restrict private repository creation.");
            var login = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Who should own the new private repository?")
                .PageSize(10).EnableSearch()
                .UseConverter(value => Markup.Escape(value == back ? value :
                    value + (owners.First(owner => owner.Login == value).IsOrganization ? " (organization)" : " (your account)")))
                .AddChoices(owners.Select(owner => owner.Login).Concat([back])));
            if (login == back) continue;
            var name = AnsiConsole.Prompt(new TextPrompt<string>("New private repository name:")
                .DefaultValue("drtui-recovery-plans")
                .Validate(value => GitHubPlanStorage.ValidNewRepositoryName(value) ? ValidationResult.Success() :
                    ValidationResult.Error("Use 1-100 letters, digits, dots, hyphens, or underscores, not . or ..; enter a name, not owner/repo.")));
            AnsiConsole.WriteLine("This creates an empty private repository only. Local code is not pushed; plan upload is a separate confirmation.");
            if (!AnsiConsole.Confirm($"Create private GitHub repository {login}/{name}?", false)) continue;
            var repository = await storage.CreateRepositoryAsync(login, name);
            AnsiConsole.WriteLine($"GitHub confirmed the new private repository: {repository}");
            return repository;
        }
    }

    private static async Task PublishAsync(Session session)
    {
        Save(session);
        if (!Session.ToolExists("gh"))
        {
            AnsiConsole.WriteLine("GitHub CLI is needed to discover your private repositories.");
            await CliInstaller.OpenAsync([new Check("CLI gh", "not found on PATH")], requiredOnly: true);
            if (!Session.ToolExists("gh"))
            {
                AnsiConsole.WriteLine("GitHub CLI is still missing. The local draft remains saved; restart setup after gh is available on PATH.");
                Acknowledge();
                return;
            }
        }
        var storage = new GitHubPlanStorage();
        if (!await storage.IsAuthenticatedAsync())
        {
            AnsiConsole.WriteLine("gh authentication is unavailable. Sign in before discovering repositories.");
            if (!AnsiConsole.Confirm("Start GitHub's browser login now?", true))
            {
                Acknowledge();
                return;
            }
            await LoginAsync("gh");
            if (!await storage.IsAuthenticatedAsync())
                throw new InvalidDataException("gh authentication is still unavailable. Check GitHub access and retry; the local draft remains saved.");
        }
        var labels = session.Plan.Metadata["labels"] as System.Text.Json.Nodes.JsonObject;
        if (labels is null)
        {
            labels = new System.Text.Json.Nodes.JsonObject();
            session.Plan.Metadata["labels"] = labels;
        }
        var current = (string?)labels[PlanStorage.RepositoryLabel];
        var discovery = await storage.DiscoverRepositoriesAsync(current);
        var repository = await ChooseRepositoryAsync(storage, discovery);
        if (repository is null)
        {
            Acknowledge();
            return;
        }
        labels[PlanStorage.RepositoryLabel] = repository;
        Save(session);
        AnsiConsole.WriteLine($"Upload path: {repository}/plans/{session.Plan.Metadata["id"]}/plan.drp.yaml");
        AnsiConsole.WriteLine("Only non-secret plan values and secret references are saved. GitHub history retains uploaded versions.");
        if (AnsiConsole.Confirm("Verify the repository is private and create/update this plan file?", false))
        {
            await storage.PublishAsync(session, repository);
            AnsiConsole.WriteLine("GitHub confirmed the plan upload. Keep gh recovery access available independently of the failed host.");
        }
        Acknowledge();
    }

    private static void Acknowledge() =>
        AnsiConsole.Prompt(new SelectionPrompt<string>().AddChoices("Continue"));
}
