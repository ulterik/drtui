# drtui

drtui is a .NET 10 terminal interface for preparing disaster recovery plans.
It opens plans, validates their YAML schema and step dependencies, and runs
local readiness checks. It does **not** execute recovery steps yet.

## Getting started

Install the .NET 10 SDK, then from the repository root run:

```sh
dotnet run
```

The default plan directory is `examples`. Pass a plan file or a directory of
plan folders to browse elsewhere:

```sh
dotnet run --project Drtui.csproj -- examples/proxmox-pbs/plan.drp.yaml
dotnet run --project Drtui.csproj -- examples
```

Browse plans with Up/Down (or `j`/`k`). The highlighted plan shows a short
description; press Enter to start interactive setup. Plain `dotnet run` takes
you straight into guided setup after this plan choice, including the option to
resume a saved draft. No setup command, special key, or setup opt-in is needed.
Invalid plans show a
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
screen, install `cf` (the Cloudflare CLI) and `proxmox-backup-client` on
your PATH, and set `CLOUDFLARE_API_TOKEN` in your shell before starting the CLI.
The token must be authorized to access the account's `PBS_API_TOKEN` secret;
local readiness checks only test whether the tools are on PATH and the
environment variable is present; they do not verify Cloudflare login or access.
The Compose/Coolify example also uses Cloudflare Secrets Store. Configure its
keyvault account ID and bootstrap `CLOUDFLARE_API_TOKEN`; the planned provider
leases `COOLIFY_APP_KEY`, the recovery `CLOUDFLARE_API_TOKEN`,
`R2_ACCESS_KEY_ID`, and `R2_SECRET_ACCESS_KEY`. The R2 S3 client uses those
leased Cloudflare credentials, not an AWS CLI profile. Neither example requires
an AWS account, AWS CLI, or AWS Secrets Manager. The keyvault account and the
input account owning the replacement tunnel may be different.

Highlight an input or keyvault setting and press Enter to edit it. Enter strings
(including IPs and paths) as plain text, for example `203.0.113.8`; enter
numbers, booleans, arrays, and objects as JSON. Clearing a required value marks
it as missing. Ordinary edits stay in memory until saved through guided setup;
they never overwrite the source plan. Local
checks run on selection and after edits. Press `r` to recheck after changing
local setup, or Esc to return to plans. AWS profile results are reused after
ordinary input edits; changing the profile or pressing `r` runs a fresh AWS
profile check. Ctrl+C exits.

## Guided setup and secure plan storage

Plain `dotnet run` starts the interactive setup flow: choose a plan and follow
the prompts. When a plan is opened through an explicit path, missing local
requirements also start setup automatically. Install/login/publication actions
still require confirmation, but entering setup itself does not.

Press **`s`** on the configuration screen to revisit setup later. The explicit
`setup` command remains an optional shortcut, not a prerequisite:

```powershell
dotnet run --project Drtui.csproj -- setup .\examples\coolify-s3-cloudflare\plan.drp.yaml
```

The wizard prioritizes missing hosting selection, required tools, invalid or
missing configuration, and the bootstrap token. It explains each requirement,
offers configure/skip/back choices, and validates entries before accepting
them. The primary plan checks Cloudflare ID lengths, HTTPS R2 endpoint shape,
exact S3 object URI shape, and SHA-256 syntax. These checks do not prove that
the account, backup, or checksum is correct remotely.

The primary plan **does not use Terraform or AWS**. The hosting chooser includes
Cloudflare Containers/Sandbox (`cf`), Hetzner (`hcloud`), DigitalOcean (`doctl`),
and other Linux/VPS providers. Cloudflare's Containers/Sandbox provides Linux
microVM compute managed through Workers/Durable Objects; it is not the SSH host
interface used by this reference plan. Selecting Cloudflare is valid and saved,
but setup reports a separate workflow-compatibility block and stops asking
irrelevant Linux-host questions. A Cloudflare-compatible recovery workflow and
execution adapter are not implemented. See
[Cloudflare Sandbox](https://developers.cloudflare.com/sandbox/).
For Linux/VPS targets, select a provider-supported region near users/data,
a size sufficient for all services and recovery disk, and an exact image ID.
No provider, region, machine size, or image is silently guessed.

The wizard installer shows **only required tools**, including Node.js when
needed for `cf`. Windows catalogued installs require confirmation; other tools
get vendor-install guidance. Authentication offers confirmed interactive
`gh auth login --web --git-protocol https`, `cf auth login`,
`hcloud context create drtui-recovery`, or `doctl auth init` as applicable.
Login credentials are managed by those vendors' CLIs, outside the plan. No
server, backup object, DNS record, or tunnel is changed by setup.

For the bootstrap `CLOUDFLARE_API_TOKEN`, setup accepts masked input for the
current process only. It never writes the token to disk or GitHub. `cf` browser
login does not satisfy this separate bootstrap requirement. R2 credentials
should be read-only for backup objects; DNS/tunnel and Secrets Store permissions
should be limited to the required account, zone, and secret names. The wizard
provides guidance, not remote permission or secret-existence verification.

Successful setup actions save a non-secret draft under the current user's
local application-data directory:
`drtui/plans/<plan-id>/plan.drp.yaml`. Directories are restricted to the current
Windows user or Unix mode `0700`; Unix draft files use `0600`. The wizard
offers to resume that draft next time. Local drafts are editable preparation
state, separate from the future stateless execution engine.

Choose **Save draft and publish to private GitHub** for the portable copy.
Setup uses `gh` to detect the current repository and list accessible private
repositories, including organization and collaborator repositories. Choose from
the writable private repositories without typing `owner/repo`; a saved selection
is prioritized, followed by the current repository when eligible. Missing `gh`
opens installation guidance, and missing authentication offers browser login.
Public or read-only repositories are excluded; discovery failures are reported
instead of being treated as an empty list. **Create a new private repository**
is available even when no existing repositories are found. Setup discovers your
account and organization memberships through `gh`, lets you choose the owner and
a repository name, and requires confirmation before creation. Organization policy,
SSO authorization, and token permissions can restrict creation; failures are reported
without switching owners or visibility. No local code is pushed.
Setup verifies the new repository's name, privacy, and write access, then
rechecks repository privacy
and requires explicit confirmation before creating/updating
`plans/<plan-id>/plan.drp.yaml`. Known literal credential fields and secret
defaults are rejected; only public configuration and secret references belong
in the plan. Never enter credentials into ordinary input fields.
Updates use the previous blob SHA to reject concurrent edits. Publication
requires write authorization and its own confirmation, separate from repository
creation. Setup never silently falls back to public storage.

To access the plan from another authorized machine, clone the private
repository with `gh repo clone <owner/repo>`, then run `setup` with the saved
plan path. Keep GitHub and Cloudflare recovery access independent of the host
being recovered. GitHub history retains uploaded versions, so review public
configuration for sensitive infrastructure metadata before publishing.
The setup screen lists each missing local configuration item and its reason.
Cloudflare compute selection does not hide Cloudflare/R2, backup, CLI, or
credential settings behind a workflow warning. Inputs referenced exclusively by
an incompatible hosting provider are deferred, while shared settings remain
configurable. Existing drafts need no migration. Deferred inputs remain required
by the current recovery contract; they are not silently given placeholder values.
Implementation blockers are shown separately from configuration, so repeating
setup is not presented as a way to implement a missing recovery adapter.
**Live recovery and hosting-provider execution remain unimplemented.**

## Generic Compose recovery plan

The [primary example](examples/coolify-s3-cloudflare/plan.drp.yaml) now describes:

1. Provision and probe a host through its selected provider's native CLI.
2. Read checksum-pinned Coolify state and a recovery inventory from Cloudflare R2.
3. Fetch application and database Compose definitions from GitHub at exact commits.
4. Restore Coolify configuration without starting workloads or public ingress.
5. Verify remote Docker, Compose v2, and `cloudflared`; restore databases into
   isolated volumes, verify them, then deploy and verify applications.
6. Create a replacement Cloudflare Tunnel, configure ingress, start its connector,
   and verify connectivity before changing hostname routes.
7. Snapshot exact old DNS records, point hostnames at the new tunnel, and verify
   public health. Compensation restores those records, stops the new connector,
   and deletes only the new tunnel; the old tunnel is retained.

This is a **planned provider contract**, not an executable recovery feature.
The example's required account, backup, endpoint, and checksum inputs are
deliberately unset. Its hosting selection and provider digests remain
placeholders. No backup is downloaded, Compose file executed, or tunnel created
by the current application.

The [inventory schema](schemas/recovery-manifest.schema.json) defines a list
of workloads rather than a fixed application or database. Each workload names
its GitHub `owner/repo`, exact 40-character `commit`, relative `composeFiles`,
and Compose `service`. Database entries additionally declare the pinned engine
image, read-only verification adapter/check, and checksummed backup objects.
Each backup must explicitly select `volume-archive` (with a target Compose volume
and stopped/application-consistent provenance) or `database-native` (with a
restore adapter). No backup format is inferred from its bucket or filename.
The future manifest action must also reject duplicate workload IDs, unsafe archive
entries, undefined Compose services/volumes, invalid routes, and incompatible
engine versions; the current loader validates the plan, not remote inventories.

Cloudflare R2's cold-storage option is **Infrequent Access**, not AWS Glacier.
It is accessible through the S3 API without a Glacier-style thaw step; retrieval
fees can apply. Set the account-specific HTTPS `r2_endpoint`, exact backup object
URIs, and trusted SHA-256 values before preparing a run. See
[R2 storage classes](https://developers.cloudflare.com/r2/buckets/storage-classes/).

Providers may declare `requiredClis: [gh, cf]` independently of their registry
name or capability. These are local PATH checks, not commands or install scripts.
An explicit empty list means no provider-specific local CLI is needed; omitted
lists preserve legacy inference. Keyvault authentication still requires its CLI.
Uncatalogued hosting CLIs (for example `hcloud`) appear as required and get manual
vendor-install guidance, never an arbitrary installer command. Docker/Compose
and `cloudflared` in this example are **remote** prerequisites, checked by the
planned runtime action rather than incorrectly requiring local Docker.

## CLI installation

Press `i` on the configuration screen to see the approved CLI list: Cloudflare
(`cf`), AWS (`aws`), Terraform (`terraform`), GitHub (`gh`), Proxmox
(`proxmox-backup-client`), Coolify (`coolify`), and the Tunnel connector
(`cloudflared`). The table shows which tools the selected plan needs
and which are optional; missing optional tools do not block readiness.
AWS and Terraform remain optional catalog entries for unrelated plans, not
primary-plan dependencies. Guided setup hides those unrelated tools.
On Windows, the catalog menu offers confirmed Winget installs for AWS CLI, Terraform, and
GitHub CLI. Cloudflare `cf` installs with `npm install --global cf` (with a
Winget Node.js LTS option if npm is missing); it has no verified Winget package
in this application's catalog. `cf` is currently beta; see the
[official CLI documentation](https://developers.cloudflare.com/cf/).
Wrangler is no longer this application's Cloudflare prerequisite or installer.
`cf` manages API resources; `cloudflared` remains the separate remote Tunnel
connector. Coolify and `cloudflared` have vendor-install guidance only.
Manual guidance opens on a separate screen in a width-aware panel, with URLs
and commands separated from explanatory text. A single static footer accepts
Enter, Esc, or Q to return to settings, without redrawing over the instructions.
The PBS client also has no
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