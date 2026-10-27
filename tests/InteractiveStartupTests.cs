using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Drtui;
using Spectre.Console;
using Xunit;

namespace Drtui.Tests;

[CollectionDefinition("Interactive startup", DisableParallelization = true)]
public class InteractiveStartupCollection;

[Collection("Interactive startup")]
public class InteractiveStartupTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task StartupEntersSetupWithoutAnOptInAndCanReturnToConfiguration(
        bool startWithSetup, bool missingConfiguration)
    {
        var document = JsonNode.Parse("""
            {
              "metadata":{"id":"startup","name":"Interactive startup","version":"1.0.0"},
              "inputs":{"setting":{"type":"string","required":true,"default":"configured"}},
              "providers":{"storage":{"kind":"storage","requiredClis":[]}},
              "steps":[]
            }
            """)!.AsObject();
        document["metadata"]!["id"] = "startup-" + Guid.NewGuid().ToString("N");
        if (missingConfiguration) document["inputs"]!["setting"]!.AsObject().Remove("default");
        var session = new Session(new Plan(document));
        var keys = new List<ConsoleKey>();
        if (startWithSetup || missingConfiguration)
        {
            if (missingConfiguration)
                keys.AddRange([ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter]);
            keys.AddRange(Enumerable.Repeat(ConsoleKey.DownArrow, 5));
            keys.Add(ConsoleKey.Enter);
        }
        keys.Add(ConsoleKey.Escape);

        using var writer = new StringWriter();
        var inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(writer)
        });
        inner.Profile.Width = 80;
        var console = DispatchProxy.Create<IAnsiConsole, ScriptedConsole>();
        var scripted = (ScriptedConsole)console;
        scripted.Inner = inner;
        var input = new ScriptedInput(keys);
        scripted.Input = input;
        var previous = AnsiConsole.Console;
        try
        {
            AnsiConsole.Console = console;
            await PlanConfiguration.OpenAsync(session, startWithSetup: startWithSetup);
        }
        finally { AnsiConsole.Console = previous; }

        var output = writer.ToString();
        Assert.Equal(startWithSetup || missingConfiguration, output.Contains("Guided plan setup", StringComparison.Ordinal));
        Assert.DoesNotContain("Start guided setup?", output);
        Assert.DoesNotContain("Review CLI authentication and recovery-secret setup?", output);
        Assert.Contains("Live recovery unavailable", output);
        Assert.Equal(0, input.Remaining);
    }

    [Fact]
    public void DefaultPlanChoiceAdvertisesSetupInsteadOfRequiringKnowledgeOfTheSettingsScreen()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var preview = PlanPreview.Load(Path.Combine(root, "examples", "coolify-s3-cloudflare", "plan.drp.yaml"));
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;
        console.Write(PlanPicker.Render([preview], 0, 80, startWithSetup: true));
        Assert.Contains("Choose a plan to set up", writer.ToString());
        Assert.Contains("Enter start setup", writer.ToString());
        Assert.DoesNotContain("Enter configure", writer.ToString());
        Assert.All(writer.ToString().Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= 80));
    }

    [Fact]
    public void ProviderPromptIncludesCloudflareAndAcceptsItsSelectionWithTheCfCli()
    {
        var session = new Session(new Plan(JsonNode.Parse("""
            {
              "metadata":{"id":"cloudflare-choice"},
              "providers":{"hosting":{"kind":"infrastructure","config":{"provisioning":"native-cli"}}},
              "steps":[{"uses":"engine/probeSSH"}]
            }
            """)!.AsObject()));
        using var writer = new StringWriter();
        var console = DispatchProxy.Create<IAnsiConsole, ScriptedConsole>();
        var scripted = (ScriptedConsole)console;
        scripted.Inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(writer)
        });
        var input = new ScriptedInput([ConsoleKey.Enter]);
        scripted.Input = input;
        var previous = AnsiConsole.Console;
        try
        {
            AnsiConsole.Console = console;
            PlanSetupWizard.ConfigureHosting(session, "hosting");
        }
        finally { AnsiConsole.Console = previous; }
        Assert.Contains("Which provider will host the recovered workloads?", writer.ToString());
        Assert.Contains("Cloudflare (Containers / Sandbox)", writer.ToString());
        Assert.Contains("Cloudflare selected with cf", writer.ToString());
        Assert.Equal("cloudflare", (string?)session.Plan.Providers["hosting"]?["config"]?["provider"]);
        Assert.Equal("cf", (string?)session.Plan.Providers["hosting"]?["config"]?["clientPath"]);
        Assert.Equal(0, input.Remaining);
    }

    [Theory]
    [InlineData(0, "owner/current")]
    [InlineData(1, "org/backup")]
    [InlineData(2, PlanSetupWizard.NewRepositoryChoice)]
    [InlineData(3, null)]
    public void GitHubRepositoryChoiceUsesDiscoveredNamesWithoutTextEntry(int downPresses, string? expected)
    {
        using var writer = new StringWriter();
        var console = DispatchProxy.Create<IAnsiConsole, ScriptedConsole>();
        var scripted = (ScriptedConsole)console;
        scripted.Inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(writer)
        });
        scripted.Inner.Profile.Width = 100;
        var input = new ScriptedInput(Enumerable.Repeat(ConsoleKey.DownArrow, downPresses).Append(ConsoleKey.Enter));
        scripted.Input = input;
        var previous = AnsiConsole.Console;
        try
        {
            AnsiConsole.Console = console;
            var selected = PlanSetupWizard.SelectRepository(new GitHubRepositoryDiscovery(
                ["owner/current", "org/backup"], "owner/current", null, null));
            Assert.Equal(expected, selected);
        }
        finally { AnsiConsole.Console = previous; }
        Assert.Contains("discovered by gh", writer.ToString());
        Assert.Contains("owner/current (current repository)", writer.ToString());
        Assert.DoesNotContain("Existing private GitHub repository (owner/repo):", writer.ToString());
        Assert.Equal(0, input.Remaining);
    }

    [Fact]
    public void NoWritablePrivateRepositoriesKeepsTheDraftWithoutRequestingTextInput()
    {
        using var writer = new StringWriter();
        var console = DispatchProxy.Create<IAnsiConsole, ScriptedConsole>();
        var scripted = (ScriptedConsole)console;
        scripted.Inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes, Out = new AnsiConsoleOutput(writer)
        });
        var input = new ScriptedInput([ConsoleKey.DownArrow, ConsoleKey.Enter]);
        scripted.Input = input;
        var previous = AnsiConsole.Console;
        try
        {
            AnsiConsole.Console = console;
            Assert.Null(PlanSetupWizard.SelectRepository(new GitHubRepositoryDiscovery([], null, null, null)));
        }
        finally { AnsiConsole.Console = previous; }
        Assert.Contains("no writable private repositories", writer.ToString());
        Assert.Contains(PlanSetupWizard.NewRepositoryChoice, writer.ToString());
        Assert.Equal(0, input.Remaining);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NewRepositoryFlowDiscoversPersonalAndOrganizationOwnersAndRequiresCreationConfirmation(
        bool existingRepositories, bool useOrganization)
    {
        var requests = new List<IReadOnlyList<string>>();
        var owner = useOrganization ? "my-org" : "my-user";
        var storage = new GitHubPlanStorage((arguments, _, _) =>
        {
            requests.Add(arguments);
            return Task.FromResult(new SetupCommandResult(0, requests.Count switch
            {
                1 => "my-user",
                2 => """{"login":"my-org"}""",
                3 => "https://github.com/" + owner + "/drtui-recovery-plans",
                4 => JsonSerializer.Serialize(new { nameWithOwner = owner + "/drtui-recovery-plans", isPrivate = true, viewerPermission = "ADMIN" }),
                _ => throw new InvalidOperationException("No upload should be attempted.")
            }));
        });
        var keys = new List<ConsoleKey>();
        if (existingRepositories) keys.Add(ConsoleKey.DownArrow);
        keys.Add(ConsoleKey.Enter);
        if (useOrganization) keys.Add(ConsoleKey.DownArrow);
        keys.AddRange([ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Y, ConsoleKey.Enter]);
        using var writer = new StringWriter();
        var console = DispatchProxy.Create<IAnsiConsole, ScriptedConsole>();
        var scripted = (ScriptedConsole)console;
        scripted.Inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes, Out = new AnsiConsoleOutput(writer)
        });
        scripted.Inner.Profile.Width = 120;
        var input = new ScriptedInput(keys);
        scripted.Input = input;
        var previous = AnsiConsole.Console;
        try
        {
            AnsiConsole.Console = console;
            var repository = await PlanSetupWizard.ChooseRepositoryAsync(storage, new GitHubRepositoryDiscovery(
                existingRepositories ? ["owner/existing"] : [], null, null, null));
            Assert.Equal(owner + "/drtui-recovery-plans", repository);
        }
        finally { AnsiConsole.Console = previous; }
        Assert.Contains("my-user (your account)", writer.ToString());
        Assert.Contains("my-org (organization)", writer.ToString());
        Assert.Contains("Create private GitHub repository", writer.ToString());
        Assert.Contains("plan upload is a separate confirmation", writer.ToString());
        Assert.Equal(new[] { "repo", "create", owner + "/drtui-recovery-plans", "--private" }, requests[2]);
        Assert.Equal(4, requests.Count);
        Assert.Equal(0, input.Remaining);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BackingOutOfOwnerSelectionOrDecliningCreationNeverCreatesARepository(bool backFromOwners)
    {
        var calls = 0;
        var storage = new GitHubPlanStorage((_, _, _) => Task.FromResult(new SetupCommandResult(0, ++calls switch
        {
            1 => "my-user",
            2 => """{"login":"my-org"}""",
            _ => throw new InvalidOperationException("No repository should be created.")
        })));
        var keys = new List<ConsoleKey> { ConsoleKey.Enter };
        keys.AddRange(backFromOwners
            ? [ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter]
            : new[] { ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.Enter });
        keys.AddRange([ConsoleKey.DownArrow, ConsoleKey.Enter]);
        using var writer = new StringWriter();
        var console = DispatchProxy.Create<IAnsiConsole, ScriptedConsole>();
        var scripted = (ScriptedConsole)console;
        scripted.Inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes, Out = new AnsiConsoleOutput(writer)
        });
        var input = new ScriptedInput(keys);
        scripted.Input = input;
        var previous = AnsiConsole.Console;
        try
        {
            AnsiConsole.Console = console;
            Assert.Null(await PlanSetupWizard.ChooseRepositoryAsync(storage, new GitHubRepositoryDiscovery([], null, null, null)));
        }
        finally { AnsiConsole.Console = previous; }
        Assert.Equal(2, calls);
        Assert.Equal(0, input.Remaining);
    }

    [Fact]
    public async Task CloudflareSetupShowsCommonMissingSettingsAndSeparatesImplementationBlockers()
    {
        var document = JsonNode.Parse("""
            {
              "metadata":{"id":"placeholder"},
              "inputs":{
                "backup_location":{"type":"string","required":true,"description":"Exact backup object"},
                "vm_flavor":{"type":"string","required":true}
              },
              "providers":{"hosting":{"kind":"infrastructure","requiredClis":["cf"],
                "config":{"provisioning":"native-cli","provider":"cloudflare","clientPath":"cf","targetKind":"cloudflare-container"}}},
              "steps":[
                {"uses":"hosting/provision","with":{"flavor":"${{ inputs.vm_flavor }}"}},
                {"uses":"engine/probeSSH"}
              ]
            }
            """)!.AsObject();
        document["metadata"]!["id"] = "cloudflare-setup-" + Guid.NewGuid().ToString("N");
        var session = new Session(new Plan(document));
        using var writer = new StringWriter();
        var console = DispatchProxy.Create<IAnsiConsole, ScriptedConsole>();
        var scripted = (ScriptedConsole)console;
        scripted.Inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes, Out = new AnsiConsoleOutput(writer)
        });
        scripted.Inner.Profile.Width = 120;
        var input = new ScriptedInput(new[] { ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter }
            .Concat(Enumerable.Repeat(ConsoleKey.DownArrow, 5)).Append(ConsoleKey.Enter));
        scripted.Input = input;
        var previous = AnsiConsole.Console;
        try
        {
            AnsiConsole.Console = console;
            Assert.Same(session, await PlanSetupWizard.OpenAsync(session, DrEnvironment.FromValue(null)));
        }
        finally { AnsiConsole.Console = previous; }
        var output = writer.ToString();
        Assert.Contains("backup_location:", output);
        Assert.Contains("required input is missing", output);
        Assert.Contains("Implementation blockers (not missing configuration)", output);
        Assert.Contains("SSH host recovery", output);
        Assert.Contains("vm_flavor", output);
        Assert.DoesNotContain("vm_flavor: required input is missing", output);
        Assert.DoesNotContain("1 local requirements need attention", output);
        Assert.DoesNotContain("Review CLI authentication and recovery-secret setup?", output);
        Assert.Equal("cloudflare", (string?)session.Plan.Providers["hosting"]?["config"]?["provider"]);
        Assert.Equal(0, input.Remaining);
    }

    [Fact]
    public async Task CompletedLocalSetupReviewsImplementationStatusWithoutRestartingAuthenticationOrPublication()
    {
        var document = JsonNode.Parse("""
            {"metadata":{"id":"placeholder"},"inputs":{},"providers":{},"steps":[]}
            """)!.AsObject();
        document["metadata"]!["id"] = "completed-setup-" + Guid.NewGuid().ToString("N");
        var session = new Session(new Plan(document));
        using var writer = new StringWriter();
        var console = DispatchProxy.Create<IAnsiConsole, ScriptedConsole>();
        var scripted = (ScriptedConsole)console;
        scripted.Inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes, Out = new AnsiConsoleOutput(writer)
        });
        scripted.Inner.Profile.Width = 120;
        var input = new ScriptedInput(new[] { ConsoleKey.Enter, ConsoleKey.Enter }
            .Concat(Enumerable.Repeat(ConsoleKey.DownArrow, 5)).Append(ConsoleKey.Enter));
        scripted.Input = input;
        var previous = AnsiConsole.Console;
        try
        {
            AnsiConsole.Console = console;
            await PlanSetupWizard.OpenAsync(session, DrEnvironment.FromValue(null));
        }
        finally { AnsiConsole.Console = previous; }
        var output = writer.ToString();
        Assert.Contains("0 local configuration items need attention", output);
        Assert.Contains("Local configuration complete (review status)", output);
        Assert.Contains("provider host: not implemented", output);
        Assert.DoesNotContain("Review CLI authentication and recovery-secret setup?", output);
        Assert.DoesNotContain("Save and offer publication", output);
        Assert.Equal(0, input.Remaining);
    }

    [Theory]
    [InlineData(40, ConsoleKey.Enter)]
    [InlineData(80, ConsoleKey.Escape)]
    [InlineData(100, ConsoleKey.Q)]
    public void ManualInstallerGuidanceUsesOneStaticFooterWithoutRedrawingOverTheInstructions(int width, ConsoleKey exitKey)
    {
        using var writer = new StringWriter();
        var console = DispatchProxy.Create<IAnsiConsole, ScriptedConsole>();
        var scripted = (ScriptedConsole)console;
        scripted.Inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes, Out = new AnsiConsoleOutput(writer)
        });
        scripted.Inner.Profile.Width = width;
        var input = new ScriptedInput([ConsoleKey.DownArrow, exitKey]);
        scripted.Input = input;
        var previous = AnsiConsole.Console;
        try
        {
            AnsiConsole.Console = console;
            CliInstaller.ShowManualGuidance("coolify");
        }
        finally { AnsiConsole.Console = previous; }
        var output = writer.ToString();
        const string footer = "Enter / Esc / Q: back to settings";
        Assert.Equal(1, output.Split(footer, StringSplitOptions.None).Length - 1);
        Assert.Contains("Coolify installation", output);
        Assert.True(output.IndexOf("Coolify installation", StringComparison.Ordinal) < output.IndexOf(footer, StringComparison.Ordinal));
        Assert.DoesNotContain("> Back to settings", output);
        Assert.Equal(0, input.Remaining);
    }

    public class ScriptedConsole : DispatchProxy
    {
        public IAnsiConsole Inner { get; set; } = null!;
        public IAnsiConsoleInput Input { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_Input" ? Input : targetMethod!.Invoke(Inner, args);
    }

    private sealed class ScriptedInput(IEnumerable<ConsoleKey> keys) : IAnsiConsoleInput
    {
        private readonly Queue<ConsoleKey> pending = new(keys);
        internal int Remaining => pending.Count;
        public bool IsKeyAvailable() => false;

        public ConsoleKeyInfo? ReadKey(bool intercept)
        {
            if (!pending.TryDequeue(out var key))
                throw new InvalidOperationException("Startup requested unexpected additional input.");
            var character = key == ConsoleKey.Enter ? '\r' : key == ConsoleKey.Y ? 'y' : '\0';
            return new ConsoleKeyInfo(character, key, false, false, false);
        }

        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadKey(intercept));
        }
    }
}
