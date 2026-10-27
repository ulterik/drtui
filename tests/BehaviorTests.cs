using System.Text;
using System.Text.Json.Nodes;
using Drtui;
using Spectre.Console;
using Xunit;

namespace Drtui.Tests;

public class BehaviorTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Theory]
    [InlineData("coolify-s3-cloudflare")]
    [InlineData("proxmox-pbs")]
    public void ExamplesLoadAndRemainBlockedForLiveExecution(string folder)
    {
        var path = Path.Combine(Root, "examples", folder, "plan.drp.yaml");
        var session = new Session(PlanLoader.Load(path));
        var checks = session.Check(_ => false, _ => null);
        Assert.Contains(checks, check => check.Label == "provider host" && check.Error is not null);
        if (folder == "proxmox-pbs")
        {
            Assert.DoesNotContain(checks, check => check.Label == "CLI aws");
            Assert.Contains(checks, check => check.Label == "CLI cf" && check.Error is not null);
            Assert.Contains(checks, check => check.Label == "Environment CLOUDFLARE_API_TOKEN" && check.Error is not null);
            Assert.Contains(checks, check => check.Label.StartsWith("keyvault ") && check.Error == "configure a 32-character hex Cloudflare account ID");
        }
        else
        {
            Assert.DoesNotContain(checks, check => check.Label == "CLI aws");
            Assert.Contains(checks, check => check.Label == "CLI cf" && check.Error is not null);
            Assert.Contains(checks, check => check.Label == "CLI gh" && check.Error is not null);
            Assert.Contains(checks, check => check.Label == "CLI coolify" && check.Error is not null);
            Assert.DoesNotContain(checks, check => check.Label == "CLI terraform");
            Assert.Contains(checks, check => check.Label.StartsWith("hosting ", StringComparison.Ordinal) && check.Error is not null);
            Assert.DoesNotContain(checks, check => check.Label == "CLI cloudflared");
            Assert.DoesNotContain(checks, check => check.Label == "CLI wrangler");
            Assert.Contains(checks, check => check.Label.StartsWith("keyvault ") && check.Error == "configure a 32-character hex Cloudflare account ID");
            Assert.Contains(checks, check => check.Label == "Environment CLOUDFLARE_API_TOKEN" && check.Error is not null);
        }
    }

    [Fact]
    public void GitHubProviderRequiresGhWithoutBlockingOnOtherApprovedTools()
    {
        var document = JsonNode.Parse("""{"metadata":{"name":"Repository restore"},"providers":{"repo":{"kind":"storage","source":"oci://ghcr.io/drtui/github-provider"}},"steps":[]}""")!.AsObject();
        var checks = new Session(new Plan(document)).Check(_ => false);

        Assert.Contains(checks, check => check.Label == "CLI gh" && check.Error is not null);
        Assert.DoesNotContain(checks, check => check.Label is "CLI aws" or "CLI cf" or "CLI terraform" or "CLI proxmox-backup-client");
    }

    [Fact]
    public void PlanPickerLoadsNameAndVersion()
    {
        var preview = PlanPreview.Load(Path.Combine(Root, "examples", "proxmox-pbs", "plan.drp.yaml"));
        Assert.Null(preview.Error);
        Assert.Equal("Proxmox Backup Server archive restore", preview.Name);
        Assert.Equal("0.1.0", preview.Version);
    }

    [Fact]
    public void InvalidPlanPreviewDoesNotStopBrowsing()
    {
        var preview = PlanPreview.Load(Path.Combine(Root, "examples", "missing", "plan.drp.yaml"));
        Assert.Null(preview.Plan);
        Assert.NotNull(preview.Error);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void PlanPickerShowsOnlyHighlightedPlanDescription(int width)
    {
        var previews = new[] { "proxmox-pbs", "coolify-s3-cloudflare" }
            .Select(folder => PlanPreview.Load(Path.Combine(Root, "examples", folder, "plan.drp.yaml")))
            .ToArray();
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            Out = new AnsiConsoleOutput(writer)
        });
        console.Profile.Width = width;

        console.Write(PlanPicker.Render(previews, 0, width));
        var first = writer.ToString();
        Assert.Contains(previews[0].Name, first);
        Assert.Contains(previews[0].Version, first);
        Assert.Contains("Validate and restore an exact PBS snapshot archive", first);
        Assert.DoesNotContain("pbs (backup)", first);
        Assert.DoesNotContain("pbs.lab.example", first);
        Assert.DoesNotContain("Cloudflare account ID", first);
        Assert.DoesNotContain("PBS_API_TOKEN", first);
        Assert.DoesNotContain("Rebuild a Coolify host", first);
        Assert.True(first.Split('\n').Length <= 24, $"Picker uses more than 24 rows at {width} columns:\n{first}");
        Assert.All(first.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= width, $"Line exceeds {width} columns: {line}"));

        writer.GetStringBuilder().Clear();
        console.Write(PlanPicker.Render(previews, 1, width));
        Assert.Contains(previews[1].Name, writer.ToString());
        Assert.Contains("Rebuild a Coolify host", writer.ToString());
        Assert.DoesNotContain("aws_secrets (keyvault)", writer.ToString());
        Assert.DoesNotContain("AWS profile", writer.ToString());
        Assert.DoesNotContain("pbs.lab.example", writer.ToString());
        Assert.True(writer.ToString().Split('\n').Length <= 24, $"Picker uses more than 24 rows at {width} columns:\n{writer}");
        Assert.All(writer.ToString().Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= width, $"Line exceeds {width} columns: {line}"));
    }

    [Fact]
    public void PlanPickerPreviewFitsWithAFullPageOfPlans()
    {
        var preview = PlanPreview.Load(Path.Combine(Root, "examples", "coolify-s3-cloudflare", "plan.drp.yaml"));
        var previews = Enumerable.Repeat(preview, 7).ToArray();
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;
        console.Write(PlanPicker.Render(previews, 3, 80));

        Assert.True(writer.ToString().Split('\n').Length <= 24, $"Picker uses more than 24 rows:\n{writer}");
    }

    [Fact]
    public void PlanPickerDoesNotDisplayProviderCredentials()
    {
        var document = JsonNode.Parse("""{"metadata":{"name":"Private","version":"0.1.0"},"inputs":{"api_key":{"type":"secret","default":"hidden-input"}},"providers":{"storage":{"kind":"storage","source":"file://storage","config":{"server":"pbs.example","password":"hidden-password","apiToken":"hidden-token"}}},"steps":[]}""")!.AsObject();
        var preview = new PlanPreview("private/plan.drp.yaml", new Plan(document), null);
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;
        console.Write(PlanPicker.Render([preview], 0, 80));

        Assert.DoesNotContain("pbs.example", writer.ToString());
        Assert.DoesNotContain("hidden-input", writer.ToString());
        Assert.DoesNotContain("hidden-password", writer.ToString());
        Assert.DoesNotContain("hidden-token", writer.ToString());
    }

    [Fact]
    public void PlanPickerDisplaysInvalidPlanError()
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Write(PlanPicker.Render([new PlanPreview("broken/plan.drp.yaml", null, "Invalid [schema] format")], 0, 80));
        Assert.Contains("INVALID", writer.ToString());
        Assert.Contains("Cannot open", writer.ToString());
        Assert.Contains("Invalid [schema] format", writer.ToString());
    }

    [Fact]
    public void PlanPickerNavigationKeepsSelectionWithinBounds()
    {
        Assert.Equal(1, PlanPicker.Move(0, 3, ConsoleKey.DownArrow));
        Assert.Equal(0, PlanPicker.Move(0, 3, ConsoleKey.UpArrow));
        Assert.Equal(2, PlanPicker.Move(1, 3, ConsoleKey.End));
        Assert.Equal(0, PlanPicker.Move(2, 3, ConsoleKey.Home));
        Assert.Equal(2, PlanPicker.Move(1, 3, ConsoleKey.PageDown));
    }

    [Theory]
    [InlineData("LOCAL", "[LOCAL-TEST] PROXMOX")]
    [InlineData("REMOTE", "[DR-ACTIVE] VPS-STANDBY")]
    public void PlanPickerShowsTheStateBeforeOpeningAPlan(string value, string badge)
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;
        var preview = PlanPreview.Load(Path.Combine(Root, "examples", "proxmox-pbs", "plan.drp.yaml"));

        console.Write(PlanPicker.Render([preview], 0, 80, DrEnvironment.FromValue(value)));

        Assert.Contains(badge, writer.ToString());
        Assert.Contains("C command", writer.ToString());
        Assert.True(writer.ToString().Split('\n').Length <= 24, $"Picker state label overflowed:\n{writer}");
    }

    [Fact]
    public void InvalidInputOverrideIsRejected()
    {
        var session = new Session(PlanLoader.Load(Path.Combine(Root, "examples", "coolify-s3-cloudflare", "plan.drp.yaml")));
        session.Plan.Inputs["previous_primary_ip"] = JsonNode.Parse("""{"type":"ip","required":true}""");
        session.Inputs["previous_primary_ip"] = "\"not an IP\"";
        Assert.Contains(session.Check(_ => true), check => check.Label == "input previous_primary_ip" && check.Error is not null);
        session.Inputs["previous_primary_ip"] = "\"203.0.113.8\"";
        Assert.Contains(session.Check(_ => true), check => check.Label == "input previous_primary_ip" && check.Error is null);
    }

    [Fact]
    public void LocalEditsRecheckInputsWithoutRerunningUnchangedAwsProfile()
    {
        var session = new Session(new Plan(JsonNode.Parse("""
            {
              "inputs":{"previous_primary_ip":{"type":"ip","required":true}},
              "providers":{"aws_secrets":{"kind":"keyvault","config":{"region":"test-region"}}},
              "steps":[
                {"uses":"aws_secrets/initialize","with":{"authConfig":{"method":"aws-cli-profile","parameters":{"profile":"disaster-recovery"}}}},
                {"uses":"aws_secrets/fetchPlanSecrets"}
              ]
            }
            """)!.AsObject()));
        session.Inputs["previous_primary_ip"] = "\"invalid\"";
        var previous = new[] { new Check("AWS profile disaster-recovery") };

        var checks = session.CheckWithCachedProfiles(previous, _ => true);
        Assert.Contains(checks, check => check.Label == "input previous_primary_ip" && check.Error == "invalid IP address");
        Assert.Contains(checks, check => check.Label == "AWS profile disaster-recovery" && check.Error is null);

        session.Vaults["aws_secrets"] = session.Vaults["aws_secrets"] with { Profile = "new-profile" };
        Assert.DoesNotContain(session.CheckWithCachedProfiles(previous, _ => true),
            check => check.Label == "AWS profile disaster-recovery");
        Assert.DoesNotContain(session.CheckWithCachedProfiles(previous, _ => false),
            check => check.Label == "AWS profile disaster-recovery");
    }

    [Fact]
    public void PlanSetupSeparatesEditableFieldsFromExternalNeeds()
    {
        var session = new Session(PlanLoader.Load(Path.Combine(Root, "examples", "proxmox-pbs", "plan.drp.yaml")));
        var setup = PlanSetup.From(session, session.Check(_ => false));
        Assert.Equal(3, setup.Inputs.Count);
        Assert.Equal("vm/101", Assert.Single(setup.Inputs, field => field.Label == "pbs_group").DisplayValue);
        Assert.True(Assert.Single(setup.Inputs, field => field.Label == "pbs_group").IsDefault);
        var account = Assert.Single(setup.VaultSettings);
        Assert.Contains("Cloudflare account ID", account.Label);
        Assert.Equal("required", account.Error);
        Assert.Contains(setup.LocalDependencies, check => check.Label == "Environment CLOUDFLARE_API_TOKEN");
        Assert.Contains(setup.LocalDependencies, check => check.Label == "CLI proxmox-backup-client" && check.Error is not null);
        Assert.DoesNotContain(setup.LocalDependencies, check => check.Label == "provider host");
        Assert.Equal("PBS_API_TOKEN", Assert.Single(setup.Secrets).Names.Single());
        Assert.Contains(setup.ProviderBindings, binding => binding.Name == "pbs" &&
            binding.Settings.Any(setting => setting.Name == "server" && setting.Value == "pbs.lab.example") &&
            binding.Settings.Any(setting => setting.Name == "datastore" && setting.Value == "recovery"));
        Assert.DoesNotContain(setup.ProviderBindings.SelectMany(binding => binding.Settings), setting =>
            setting.Name == "password" || setting.Value.Contains("PBS_API_TOKEN"));

        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;
        console.Write(PlanConfiguration.RenderProviders(setup.ProviderBindings));
        Assert.Contains("Setting", writer.ToString());
        Assert.Contains("Value", writer.ToString());
        Assert.Contains("Status", writer.ToString());
        Assert.Contains("pbs.lab.example", writer.ToString());
        Assert.Contains("plan value (unverified)", writer.ToString());
        Assert.DoesNotContain("PBS_API_TOKEN", writer.ToString());

        setup.Inputs.Single(field => field.Label == "pbs_group").Save(null);
        var updated = PlanSetup.From(session, session.Check(_ => true));
        Assert.Equal("required input is missing", updated.Inputs.Single(field => field.Label == "pbs_group").Error);
        Assert.DoesNotContain(updated.LocalDependencies, check => check.Label.StartsWith("CLI ") && check.Error is not null);
    }

    [Fact]
    public void CloudflareAccountAndTokenPresenceAreValidatedWithoutExposingTheToken()
    {
        var session = new Session(PlanLoader.Load(Path.Combine(Root, "examples", "proxmox-pbs", "plan.drp.yaml")));
        var field = Assert.Single(PlanSetup.From(session, session.Check(_ => true, _ => null)).VaultSettings);
        field.Save("invalid-account");
        Assert.Equal("expected 32 hex characters",
            Assert.Single(PlanSetup.From(session, session.Check(_ => true, _ => null)).VaultSettings).Error);

        field.Save("0123456789abcdef0123456789abcdef");
        var checks = session.Check(_ => true, name => name == "CLOUDFLARE_API_TOKEN" ? "hidden-token-value" : null);
        Assert.Contains(checks, check => check.Label.StartsWith("keyvault ") && check.Error is null);
        Assert.Contains(checks, check => check.Label == "Environment CLOUDFLARE_API_TOKEN" && check.Error is null);
        Assert.Contains(checks, check => check.Label == "CLI cf" && check.Error is null);
        Assert.DoesNotContain(checks, check => check.Label == "CLI aws");
        Assert.DoesNotContain("hidden-token-value", string.Join(" ", checks));

        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;
        console.Write(PlanConfiguration.Render(session, PlanSetup.From(session, checks), 80, 4));
        Assert.Contains("Environment CLOUDFLARE_API_TOKEN", writer.ToString());
        Assert.Contains("present (unverified)", writer.ToString());
        Assert.DoesNotContain("hidden-token-value", writer.ToString());
    }

    [Theory]
    [InlineData("proxmox-pbs", 80)]
    [InlineData("proxmox-pbs", 120)]
    [InlineData("coolify-s3-cloudflare", 80)]
    [InlineData("coolify-s3-cloudflare", 120)]
    public void SelectedPlanShowsItsSettingsAndNecessarySetup(string folder, int width)
    {
        var session = new Session(PlanLoader.Load(Path.Combine(Root, "examples", folder, "plan.drp.yaml")));
        var setup = PlanSetup.From(session, session.Check(_ => false));
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            Out = new AnsiConsoleOutput(writer)
        });
        console.Profile.Width = width;
        console.Write(PlanConfiguration.Render(session, setup, width));

        var display = writer.ToString();
        Assert.Contains("CLI tools", display);
        Assert.Contains("╭", display);
        Assert.Contains("│ CLI tools", display);
        var settingsHeader = Assert.Single(display.Split('\n'), line => line.Contains("Settings (", StringComparison.Ordinal));
        Assert.Contains("Status", settingsHeader);
        Assert.DoesNotContain("Value", settingsHeader);
        Assert.Contains("Value:", display);
        Assert.Contains("Inputs", display);
        Assert.Contains(folder == "proxmox-pbs" ? "default (unverified)" : "missing", display);
        Assert.Contains("Live recovery unavailable", display);
        Assert.Contains("I install", display);
        Assert.DoesNotContain("Edit pbs_archive", display);
        Assert.True(display.Split('\n').Length <= 24, $"Configuration exceeds 24 rows at {width} columns:\n{display}");
        Assert.All(display.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= width, $"Line exceeds {width} columns: {line}"));
        if (folder == "proxmox-pbs")
        {
            Assert.Contains("pbs_snapshot", display);
            Assert.Contains("Cloudflare account ID", display);
            Assert.Contains("Keyvault settings", display);
            Assert.Contains("proxmox-backup-client", display);
            Assert.Contains("cf", display);
            Assert.DoesNotContain("PBS_API_TOKEN", display);
            Assert.DoesNotContain("CLI aws", display);
            Assert.DoesNotContain("PBS_FINGERPRINT", display);
        }
        else
        {
            Assert.DoesNotContain("aws", display);
            Assert.DoesNotContain("wrangler", display);
            Assert.Contains("cloudflare_account_id", display);
            Assert.DoesNotContain("CLOUDFLARE_API_TOKEN", display);
            Assert.DoesNotContain("PBS_API_TOKEN", display);
        }

        writer.GetStringBuilder().Clear();
        console.Write(PlanConfiguration.Render(session, setup, width, 100));
        var scrolled = writer.ToString();
        Assert.Contains("Secrets", scrolled);
        Assert.Contains("not verified", scrolled);
        writer.GetStringBuilder().Clear();
        console.Write(PlanConfiguration.Render(session, setup, width, setup.Inputs.Count));
        Assert.Contains("Keyvault settings", writer.ToString());
        Assert.True(scrolled.Split('\n').Length <= 24, $"Scrolled configuration exceeds 24 rows at {width} columns:\n{scrolled}");
        Assert.All(scrolled.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= width, $"Line exceeds {width} columns: {line}"));
        if (folder == "proxmox-pbs")
        {
            Assert.Contains("PBS_API_TOKEN", scrolled);
            Assert.Contains("<hidden>", scrolled);

            writer.GetStringBuilder().Clear();
            console.Write(PlanConfiguration.Render(session, setup, width, 3));
            var middle = writer.ToString();
            Assert.Contains("Inputs", middle);
            Assert.Contains("Keyvault settings", middle);
            Assert.Contains("Local setup", middle);
            Assert.Contains("Environment CLOUDFLARE_API_TOKEN", middle);
            Assert.True(middle.Split('\n').Length <= 24, $"Mixed section page exceeds 24 rows at {width} columns:\n{middle}");
        }
        else Assert.Contains("CLOUDFLARE_API_TOKEN", scrolled);
    }

    [Fact]
    public void ConfigurationNavigationKeepsSelectionWithinBounds()
    {
        Assert.Equal(1, PlanConfiguration.Move(0, 8, ConsoleKey.DownArrow));
        Assert.Equal(0, PlanConfiguration.Move(0, 8, ConsoleKey.UpArrow));
        Assert.Equal(4, PlanConfiguration.Move(0, 8, ConsoleKey.PageDown));
        Assert.Equal(7, PlanConfiguration.Move(5, 8, ConsoleKey.PageDown));
        Assert.Equal(0, PlanConfiguration.Move(2, 8, ConsoleKey.PageUp));
        Assert.Equal(7, PlanConfiguration.Move(0, 8, ConsoleKey.End));
        Assert.Equal(0, PlanConfiguration.Move(7, 8, ConsoleKey.Home));
        Assert.Equal(0, PlanConfiguration.Move(0, 0, ConsoleKey.End));
    }

    [Theory]
    [InlineData("LOCAL", "[LOCAL-TEST] PROXMOX")]
    [InlineData("REMOTE", "[DR-ACTIVE] VPS-STANDBY")]
    public void ConfigurationShowsEnvironmentNextToItsInputActions(string value, string badge)
    {
        var session = new Session(PlanLoader.Load(Path.Combine(Root, "examples", "proxmox-pbs", "plan.drp.yaml")));
        var setup = PlanSetup.From(session, session.Check(_ => false, _ => null));
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;

        console.Write(PlanConfiguration.Render(session, setup, 80, environment: DrEnvironment.FromValue(value)));

        Assert.Contains(badge, writer.ToString());
        Assert.Contains("C command", writer.ToString());
        Assert.True(writer.ToString().Split('\n').Length <= 24, $"State label caused screen overflow:\n{writer}");
    }

    [Fact]
    public void ConfigurationPreservesLongSettingNamesAtEightyColumns()
    {
        var session = new Session(PlanLoader.Load(Path.Combine(Root, "examples", "proxmox-pbs", "plan.drp.yaml")));
        var setup = PlanSetup.From(session, session.Check(_ => true, _ => null));
        var vault = Assert.Single(setup.VaultSettings) with { Label = "long_provider / " + new string('x', 25) };
        setup = setup with { Inputs = [], VaultSettings = [vault], LocalDependencies = [], Secrets = [] };
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;

        console.Write(PlanConfiguration.Render(session, setup, 80));

        Assert.Contains(new string('x', 25), writer.ToString());
        Assert.All(writer.ToString().Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= 80));
    }

    [Fact]
    public void MultilineInputDoesNotBreakTheFramedConfigurationLayout()
    {
        var session = new Session(PlanLoader.Load(Path.Combine(Root, "examples", "proxmox-pbs", "plan.drp.yaml")));
        session.Inputs["pbs_group"] = System.Text.Json.JsonSerializer.Serialize("vm/101\nnext");
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;

        console.Write(PlanConfiguration.Render(session, PlanSetup.From(session, session.Check(_ => false, _ => null)), 80, 1));

        Assert.Contains("vm/101 next", writer.ToString());
        Assert.True(writer.ToString().Split('\n').Length <= 24, $"Multiline input pushed controls off screen:\n{writer}");
    }

    [Fact]
    public void ConfigurationUsesAnsiColorsWhenAvailable()
    {
        var session = new Session(PlanLoader.Load(Path.Combine(Root, "examples", "proxmox-pbs", "plan.drp.yaml")));
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.Yes, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;

        console.Write(PlanConfiguration.Render(session, PlanSetup.From(session, session.Check(_ => false, _ => null)), 80));

        Assert.Contains("\x1b[", writer.ToString());
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void SelectedSettingShowsItsFullValueBelowTheTable(int width)
    {
        var session = new Session(PlanLoader.Load(Path.Combine(Root, "examples", "proxmox-pbs", "plan.drp.yaml")));
        var setup = PlanSetup.From(session, session.Check(_ => false, _ => null));
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = width;

        console.Write(PlanConfiguration.Render(session, setup, width, 2));

        var output = writer.ToString();
        var selectedValue = setup.Inputs.Single(field => field.Label == "pbs_snapshot").DisplayValue;
        Assert.Contains("Value: " + selectedValue, output);
        Assert.True(output.IndexOf("Value: " + selectedValue, StringComparison.Ordinal) >
            output.LastIndexOf('╰'), $"Selected value was not below the settings table:\n{output}");
        Assert.True(output.Split('\n').Length <= 24, $"Selected value pushed controls off screen:\n{output}");
        Assert.All(output.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= width));
    }

    [Fact]
    public void SelectedLongValueWrapsWithoutPushingControlsOffScreen()
    {
        var session = new Session(PlanLoader.Load(Path.Combine(Root, "examples", "proxmox-pbs", "plan.drp.yaml")));
        var fullValue = "s3://bucket/" + new string('a', 78) + "[archive]";
        session.Inputs["pbs_snapshot"] = System.Text.Json.JsonSerializer.Serialize(fullValue);
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;

        console.Write(PlanConfiguration.Render(session, PlanSetup.From(session, session.Check(_ => false, _ => null)), 80, 2));

        var output = writer.ToString();
        Assert.Contains(fullValue, string.Concat(output.Split('\n')
            .SkipWhile(line => !line.Contains("Value:", StringComparison.Ordinal))
            .Take(3).Select(line => line.TrimEnd('\r'))));
        Assert.True(output.Split('\n').Length <= 24, $"Long selected value pushed controls off screen:\n{output}");
        Assert.All(output.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= 80));
    }

    [Fact]
    public void SelectedValueColorReflectsVerificationWithoutExposingSecrets()
    {
        var session = new Session(PlanLoader.Load(Path.Combine(Root, "examples", "proxmox-pbs", "plan.drp.yaml")));
        var checks = session.Check(_ => true, name => name == "CLOUDFLARE_API_TOKEN" ? "hidden-token-value" : null);
        var setup = PlanSetup.From(session, checks);
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.Yes, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;

        console.Write(PlanConfiguration.Render(session, setup, 80, 2));
        Assert.Contains(ColoredValue("yellow", setup.Inputs.Single(field => field.Label == "pbs_snapshot").DisplayValue), writer.ToString());

        writer.GetStringBuilder().Clear();
        console.Write(PlanConfiguration.Render(session, setup, 80, 4));
        Assert.Contains(ColoredValue("yellow", "<set>"), writer.ToString());
        Assert.DoesNotContain("hidden-token-value", writer.ToString());

        writer.GetStringBuilder().Clear();
        console.Write(PlanConfiguration.Render(session, setup, 80, 5));
        var secretLine = writer.ToString().Split('\n').FirstOrDefault(line => line.Contains("Value:", StringComparison.Ordinal)) ?? "<missing Value readout>";
        Assert.True(secretLine.Contains(ColoredValue("yellow", "<hidden>"), StringComparison.Ordinal),
            $"Secret readout: {System.Text.Json.JsonSerializer.Serialize(secretLine)}");
        Assert.DoesNotContain("hidden-token-value", writer.ToString());

        writer.GetStringBuilder().Clear();
        var verified = setup with { Inputs = [], VaultSettings = [],
            LocalDependencies = [new Check("keyvault local-check")], Secrets = [] };
        console.Write(PlanConfiguration.Render(session, verified, 80));
        Assert.Contains(ColoredValue("green", "-"), writer.ToString());
    }

    private static string ColoredValue(string color, string value)
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.Yes, Out = new AnsiConsoleOutput(writer) });
        console.Markup($"[bold {color}]Value: {Markup.Escape(value)}[/]");
        return writer.ToString();
    }

    [Fact]
    public void EditingAStringAcceptsPlainTextAndHighlightsMissingValues()
    {
        var session = new Session(PlanLoader.Load(Path.Combine(Root, "examples", "coolify-s3-cloudflare", "plan.drp.yaml")));
        var field = PlanSetup.From(session, session.Check(_ => true)).Inputs.Single(item => item.Label == "cloudflare_account_id");
        field.Save(PlanConfiguration.Encode(field, "0123456789abcdef0123456789abcdef"));
        Assert.Equal("\"0123456789abcdef0123456789abcdef\"", session.Inputs["cloudflare_account_id"]);
        var changed = PlanSetup.From(session, session.Check(_ => true)).Inputs.Single(item => item.Label == "cloudflare_account_id");
        Assert.Equal("0123456789abcdef0123456789abcdef", changed.DisplayValue);
        Assert.False(changed.IsDefault);

        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;
        console.Write(PlanConfiguration.Render(session, PlanSetup.From(session, session.Check(_ => true)), 80, 2));
        Assert.Contains("set (unverified)", writer.ToString());

        field.Save(PlanConfiguration.Encode(field, ""));
        var updated = PlanSetup.From(session, session.Check(_ => true));
        Assert.Equal("required input is missing", updated.Inputs.Single(item => item.Label == "cloudflare_account_id").Error);

        writer.GetStringBuilder().Clear();
        console.Write(PlanConfiguration.Render(session, updated, 80, 2));
        Assert.Contains("required input is missing", writer.ToString());
        Assert.Contains("cloudflare_account_id", writer.ToString());
    }

    [Fact]
    public void ConfigurationHidesSectionsThePlanDoesNotUse()
    {
        var document = JsonNode.Parse("""{"metadata":{"name":"Standalone","version":"0.1.0"},"providers":{"storage":{"kind":"storage","source":"file://storage"}},"steps":[]}""")!.AsObject();
        var session = new Session(new Plan(document));
        var setup = PlanSetup.From(session, session.Check(_ => true));
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 80;
        console.Write(PlanConfiguration.Render(session, setup, 80));

        Assert.DoesNotContain("Inputs", writer.ToString());
        Assert.DoesNotContain("Keyvault settings", writer.ToString());
        Assert.Contains("Providers: storage", writer.ToString());
        Assert.Contains("CLI tools", writer.ToString());
        Assert.Contains("None required", writer.ToString());
        Assert.Contains("No plan settings", writer.ToString());
    }

    [Fact]
    public void DependencyOrderRejectsCycles()
    {
        var steps = JsonNode.Parse("""[{"id":"first","needs":["second"]},{"id":"second","needs":["first"]}]""")!.AsArray();
        Assert.Throws<InvalidDataException>(() => PlanLoader.ValidateDependencies(steps));
    }

    [Fact]
    public async Task PbsProbeUsesStdinAndPinnedFingerprint()
    {
        var runner = new FakeRunner();
        var passwords = new List<byte[]>();
        var client = new PbsClient(Config(), (_, _, _) =>
        {
            var password = Encoding.UTF8.GetBytes("test-token");
            passwords.Add(password);
            return Task.FromResult(password);
        }, runner);
        var status = await client.ProbeRepositoryAsync();
        Assert.Equal("3.2", status.ServerVersion);
        Assert.Equal((ulong)42, status.TotalBytes);
        Assert.Equal(2, runner.Commands.Count);
        Assert.Equal("test-token\n", runner.Input);
        Assert.All(passwords.SelectMany(password => password), item => Assert.Equal((byte)0, item));
        Assert.All(runner.Commands, command =>
        {
            Assert.Equal("0", command.Environment["PBS_PASSWORD_FD"]);
            Assert.Equal(Config().Fingerprint, command.Environment["PBS_FINGERPRINT"]);
            Assert.DoesNotContain("test-token", string.Join(" ", command.Arguments));
        });
    }

    [Fact]
    public async Task PbsRestoreRejectsWorkDirectoryEscapeBeforeResolvingSecrets()
    {
        var client = new PbsClient(Config(), (_, _, _) => throw new Exception("secret was resolved"), new FakeRunner());
        await Assert.ThrowsAsync<ArgumentException>(() => client.RestoreArchiveAsync(
            "vm/101/2026-09-21T08:30:00Z", "etc.pxar", "../outside"));
    }

    [Fact]
    public async Task PbsAuthenticationFailureDoesNotExposeStderr()
    {
        var runner = new FakeRunner { Failure = "authentication failed for token secret-value" };
        var client = new PbsClient(Config(), (_, _, _) => Task.FromResult(Encoding.UTF8.GetBytes("test-token")), runner);
        var error = await Assert.ThrowsAsync<PbsProviderException>(() => client.ListSnapshotsAsync());
        Assert.Equal("AUTHENTICATION_FAILED", error.Code);
        Assert.DoesNotContain("secret-value", error.Message);
    }

    [Fact]
    public async Task PbsSnapshotListPreservesNamespaceAndParsesResult()
    {
        var runner = new FakeRunner { SnapshotOutput = "[{\"snapshot\":\"vm/101/2026-09-21T08:30:00Z\",\"size\":4096}]" };
        var client = new PbsClient(Config(), (_, _, _) => Task.FromResult(Encoding.UTF8.GetBytes("test-token")), runner);
        var snapshots = await client.ListSnapshotsAsync("vm/101", "dr/site-a");
        Assert.Single(snapshots);
        Assert.Equal((ulong)4096, snapshots[0].SizeBytes);
        Assert.Contains("dr/site-a", runner.Commands[0].Arguments);
    }

    private static PbsConfig Config() => new("pbs.example", "recovery", "drtui@pbs!dr",
        string.Join(":", Enumerable.Repeat("AA", 32)), Path.Combine(Path.GetTempPath(), "drtui-pbs"), "secrets", "token");

    private sealed class FakeRunner : IPbsCommandRunner
    {
        public List<PbsCommand> Commands { get; } = [];
        public string Input { get; private set; } = "";
        public string? Failure { get; init; }
        public string? SnapshotOutput { get; init; }

        public Task<PbsCommandResult> RunAsync(PbsCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            Input = Encoding.UTF8.GetString(command.Stdin);
            if (Failure is not null)
                return Task.FromResult(new PbsCommandResult([], Encoding.UTF8.GetBytes(Failure), 255));
            var output = command.Arguments[0] == "version"
                ? "{\"server-version\":\"3.2\"}"
                : command.Arguments[0] == "snapshot" ? SnapshotOutput ?? "[]"
                : "{\"total\":42,\"used\":2,\"avail\":40}";
            return Task.FromResult(new PbsCommandResult(Encoding.UTF8.GetBytes(output), [], 0));
        }
    }
}