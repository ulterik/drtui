# drtui

drtui is a .NET 10 terminal interface for preparing disaster recovery plans.
It opens plans, validates their YAML schema and step dependencies, and runs
local readiness checks. It does **not** execute recovery steps yet.

## Getting started

Install the .NET 10 SDK, then from the repository root run:

```sh
dotnet run --project Drtui.csproj
```

The default plan directory is `examples`. Pass a plan file or a directory of
plan folders to browse elsewhere:

```sh
dotnet run --project Drtui.csproj -- examples/proxmox-pbs/plan.drp.yaml
dotnet run --project Drtui.csproj -- examples
```

Browse plans with Up/Down (or `j`/`k`). The highlighted plan shows a short
description; press Enter to open its configuration. Invalid plans show a
validation error and cannot be opened. Select **Exit**, press `q`, or press Esc
to leave the browser.

## DR environment

The TUI reads `DR_ENV` at startup. An unset value defaults to `LOCAL`; values
other than `LOCAL` and `REMOTE` are rejected. A blue `[LOCAL-TEST] PROXMOX`
indicator or red `[DR-ACTIVE] VPS-STANDBY` indicator appears in the plan picker,
configuration screen, and before text input. This is the application's own
prompt; a Bash `PS1` setting does not change Spectre.Console or a PowerShell
prompt. Restart the app after changing `DR_ENV`.

Press `c` in the picker or configuration screen to enter a command prompt, or
launch that prompt directly:

```powershell
$env:DR_ENV = 'LOCAL'
dotnet run --project Drtui.csproj -- shell
```

The prompt accepts `status`, `help`, and `exit`. In a selected plan's
configuration, `check` emits JSON for basic local readiness without executing
recovery steps; for a standalone readiness report, use the `check` subcommand
below. A single non-interactive command emits JSON without terminal styling:

```powershell
dotnet run --project Drtui.csproj -- command status
dotnet run --project Drtui.csproj -- command failover
```

Production commands (`failover`, `restore-postgresql`, `restore-mongodb`,
`deploy-coolify`, and `dns-cutover`) always return `DR_ERR_01: Cannot execute
production failover from local node.` in LOCAL. REMOTE requires `--force`, but
still rejects execution because the provider host is not implemented. Setting
`DR_ENV=REMOTE` does not perform or authorize recovery. LOCAL template
scaffolding, WAL archiving configuration, rclone cron generation, and full DR
restore tests are not implemented; the `check` command is a readiness check,
not a restore simulation. No Cloudflare DNS or database changes are issued.

After selection, CLI tools appear above a scrollable Setting / Status table.
Use Up/Down (or `j`/`k`), PageUp/PageDown, Home, and End to browse inputs,
keyvault settings, local checks, and secret names. Each page shows up to four
settings. **Default (unverified)** means a value came from the YAML plan;
**set (unverified)** means a value was entered locally. A present environment
variable or listed AWS profile does not prove remote access, and secret values
are never shown. The selected row's value appears just below the table in
yellow when unverified or missing, and green when a local check has passed;
tokens and secrets remain masked. Press `p` for a read-only provider settings
table with Setting / Value / Status columns; only non-secret plan values appear
there.

The Proxmox PBS example uses Cloudflare Secrets Store for its keyvault.
Configure your 32-character Cloudflare account ID in the plan's configuration
screen, install `wrangler` (the Cloudflare CLI) and `proxmox-backup-client` on
your PATH, and set `CLOUDFLARE_API_TOKEN` in your shell before starting the CLI.
The token must be authorized to access the account's `PBS_API_TOKEN` secret;
local readiness checks only test whether the tools are on PATH and the
environment variable is present; they do not verify Cloudflare login or access.
The Coolify example continues to use an AWS CLI profile and region.

Highlight an input or keyvault setting and press Enter to edit it. Enter strings
(including IPs and paths) as plain text, for example `203.0.113.8`; enter
numbers, booleans, arrays, and objects as JSON. Clearing a required value marks
it as missing. Edits stay in memory; they do not change the plan file. Local
checks run on selection and after edits. Press `r` to recheck after changing
local setup, or Esc to return to plans. AWS profile results are reused after
ordinary input edits; changing the profile or pressing `r` runs a fresh AWS
profile check. Ctrl+C exits.

Press `i` on the configuration screen to see the approved CLI list: Cloudflare
(`wrangler`), AWS (`aws`), Terraform (`terraform`), GitHub (`gh`), and Proxmox
(`proxmox-backup-client`). The table shows which tools the selected plan needs
and which are optional; missing optional tools do not block readiness. On
Windows, the menu offers confirmed Winget installs for AWS CLI, Terraform, and
GitHub CLI. Wrangler installs through npm (with a Winget Node.js LTS option if
npm is missing); it has no verified Winget package. The PBS client also has no
verified Winget package. On Windows, its menu entry gives a guided Debian WSL
setup: configure Proxmox's signed client-only APT repository for the Debian
release, then install `proxmox-backup-client` inside WSL. Nothing runs
automatically; setting up WSL may require administrator approval and a reboot.
WSL `/mnt/c` access can produce `EACCES` and does not provide Windows VSS
snapshots. Installing the client in WSL does **not** satisfy the Windows-native
CLI check or make the Windows PBS runner invoke WSL. To check the Linux client,
run the .NET TUI inside that distro with .NET 10 installed; live recovery is
still unavailable. Proxmox staff do not consider Windows-file backups through
WSL tested or supported. The official Bookworm repository currently publishes
no `arm64` client package; on an ARM64 Windows host, use an x86-64 Linux VM or
remote Debian host, or build and validate the client separately. Adding the
repository alone does not install the client.
No command runs until you confirm it. The screen rechecks tools afterward,
although you may need a new terminal for a changed PATH. A successful install
does not make live plan execution available.

For a non-interactive readiness report, use `check`:

```sh
dotnet run --project Drtui.csproj -- check examples
```

The command exits nonzero if a plan is invalid or any check is blocked. The
provider host check is always blocked until execution support is implemented.

Readiness reports missing CLIs required by each plan (including `gh` for
GitHub-backed providers) and lists required secret **names**, not values.
The non-interactive readiness command never installs tools, authenticates to
AWS or Cloudflare, connects to PBS, or runs plan steps. Example provider digests
are placeholders, not production pins.

Run the .NET tests with:

```sh
dotnet test tests/Drtui.Tests.csproj
```

The PBS command adapter is available as a .NET component in
[PbsClient.cs](PbsClient.cs), but no provider host invokes it yet. The .NET CLI
and tests are the only application implementation in this repository.

See [the architecture](docs/architecture.md) and
[implementation plan](docs/implementation-plan.md) for the planned execution
workflow and security model.