# drtui implementation plan

The supported interactive CLI is now the .NET 10 Spectre.Console application
described in [README.md](../README.md). The provider host and live recovery
execution remain unimplemented. Paths below describe planned .NET components,
not existing directories unless explicitly identified as implemented.

## Outcome

Deliver a generic stateless CLI that can validate, package, inspect, and execute
provider-configured recovery plans using digest-pinned, out-of-process
providers. Every component must be testable without live cloud credentials, and
the complete workflow must be rehearsable against a disposable Linux server and
a non-production DNS zone.

The primary example restores GitHub-managed Docker Compose applications and
databases, restores Coolify configuration and database backups from Cloudflare
R2's S3-compatible storage, then creates a replacement Cloudflare Tunnel and
moves hostname routes only after verification. Hosting, storage endpoints,
keyvaults, backup formats, and workload lists are replaceable plan data, not
hardcoded application behavior.

### Current implementation slice

- Implemented: provider `requiredClis` schema and local PATH checks, generic
  manual-install guidance for uncatalogued hosting CLIs, and confirmed installers
  for catalogued tools. Omitted declarations retain legacy inference.
- Implemented: Cloudflare CLI prerequisite/installer now uses `cf`
  (`npm install --global cf`, currently beta), with `cloudflared` separately
  identified as the Tunnel connector. Neither is automatically installed.
- Implemented: the reference plan and tested recovery inventory JSON Schema model
  exact GitHub Compose revisions, explicit per-database backup formats, and
  replacement-tunnel cutover with exact prior-record compensation.
- Implemented: plain `dotnet run` opens interactive plan selection and enters
  guided setup directly, without a separate command, special key, or setup
  opt-in. Explicit plan paths also enter setup automatically when incomplete.
  `setup <plan>` and `s` in configuration are optional shortcuts. Missing
  requirements are prioritized and explained, with validated input prompts,
  required-only installer options, and confirmed vendor-CLI login actions.
- Implemented: explicit native-CLI hosting selection, including Cloudflare
  Containers/Sandbox with `cf`, initially unselected and blocked. Cloudflare
  selections are saved with a container target kind; the SSH-based reference
  workflow gets a separate compatibility block and does not continue prompting
  for Linux-host configuration. A Cloudflare-compatible workflow and adapter
  remain unimplemented. Terraform is not used by the primary plan.
  Suggested provider options are selection/configuration only,
  not implemented provisioning adapters.
- Implemented: atomic non-secret local drafts with restrictive user permissions,
  resume prompts, and explicitly confirmed publication to a private
  GitHub repository. Setup discovers writable private repositories through `gh`,
  including organization/collaborator access, rather than asking for `owner/repo`.
  Saved/current repositories are prioritized, and missing CLI/authentication
  offers installation guidance/browser login. Credentials remain outside plans; bootstrap token entry is
  masked and process-only. Publication verifies privacy, concurrency SHA, and
  the returned path/digest, and reports failures without claiming success.
- Implemented: the GitHub repository picker also offers creation of an empty
  private repository, including when no existing repositories are found. `gh`
  discovers the authenticated account and organization memberships; setup prompts
  for the owner/name, requires explicit creation confirmation, and verifies the
  resulting repository's name, privacy, and write access. Organization policy or
  permission failures are explicit, with no owner/visibility fallback. Creation
  does not push local code; plan publication requires a separate confirmation.
- Not implemented: provider actions, fetching/semantic validation of remote
  inventories, remote prerequisite installation/probes, or live recovery.
  Example required inputs remain unset and provider digests remain placeholders.

The work should land as small vertical changes. A phase is complete only when
its tests and command-level acceptance criteria pass; later phases must not be
used to compensate for an untestable earlier layer.

## Decisions to freeze first

1. Provider plugins are separate executables speaking versioned gRPC, not
  in-process assemblies.
2. A provider `digest` is the OCI manifest digest. A lockfile additionally pins
   the platform-specific binary blob digest and embedded plugin manifest digest.
3. Local development may use `file://` plugins, but their SHA-256 is still
   required and verified before launch.
4. The CLI owns plan compilation, scheduling, retries, journals, secret leases,
   package verification, and built-in filesystem cleanup. Providers own only
   their external-system behavior.
5. Tests use three destination tiers:
   - An in-process SSH server for default, network-local tests.
   - A Docker Linux SSH server for transport and remote-operation integration.
   - A disposable Ubuntu host from the selected hosting provider for the full Coolify rehearsal.
6. The production example remains signature-required. Test plans live under
   `testdata` and refer to locally built plugins with generated real digests.
7. No test changes a production DNS record or reads production backup objects.

The reference plan uses Cloudflare R2 for backup objects and Cloudflare Secrets
Store for recovery secrets. It requires no AWS account, AWS CLI, or AWS profile.
R2's S3-compatible API uses leased Cloudflare R2 credentials, not AWS credentials.
The hosting destination and its region are selected through the infrastructure
provider's native CLI; no AWS or Terraform destination is assumed. Other plans
may explicitly opt into those tools. Saved plan preparation is distinct from
the future stateless execution engine.

## Intended repository layout

The current code lives in the root .NET project; see
[architecture](architecture.md#repository-layout). Future components can be
added as .NET class libraries and executable provider projects: plan
compilation, expression resolution, scheduler, journal, package verifier,
plugin host, secret broker, remote transport, provider SDK, and conformance
tests. Keep the versioned protocol in `api/proto/provider/v1/`, with generated
C# types checked into a dedicated project when introduced.

## Test modes and isolation

| Level | Command | Network | Purpose |
| --- | --- | --- | --- |
| Unit | `dotnet test tests/Drtui.Tests.csproj` | No external network | Current plan, readiness, and PBS tests; expand for engine and redaction |
| Integration | Planned .NET test project | Loopback/Docker | Plugin processes, gRPC, LocalStack/MinIO, fake Cloudflare API, Linux SSH target |
| System | Planned .NET test project | Selected host/test DNS zone | Provision VM, install Coolify, restore synthetic data, cut over test DNS, rollback |
| Compatibility | Planned .NET conformance suite | Plugin-defined | Contract suite external plugin authors can import |

Default tests must pass when Docker, cloud credentials, Terraform, and `protoc`
are absent. Integration tests detect an unavailable Docker daemon and skip with
one actionable reason. CI runs integration tests on Linux where Docker is
guaranteed. System tests run only through a manual or scheduled workflow.

### Isolated plan execution

Implement these commands before live providers:

```text
drtui plan validate <plan-or-package>
drtui plan graph <plan-or-package> --format text|json|dot
drtui run <package> --dry-run
drtui run <package> --to <step-id>
drtui test step <plan> --step <step-id> --context <fixture.json>
drtui target probe --connection <connection.json> --identity-file <path>
```

`run --to` executes the selected step's complete dependency closure and then
stops. It is the supported way to rehearse a real prefix such as provisioning
through SSH verification or downloads through checksum validation.

`test step` is deliberately separate from `run`. It invokes exactly one action
with schema-validated fixture inputs and synthetic predecessor outputs. It may
use only `file://` plugins, rejects production-signature policy, marks every
journal event as test-only, and cannot package its result as a resumable run.
This prevents fixture context from bypassing dependencies in production.

Each provider action gets a contract fixture with:

- A valid request and expected typed response.
- Invalid configuration and invalid request cases.
- Cancellation and deadline behavior.
- A retryable failure followed by success using the same idempotency key.
- A repeated successful invocation proving idempotency.
- Secret-input and log-redaction assertions.
- A rollback or cleanup assertion for every mutating action.

## Destination server strategy

### Tier 1: in-process SSH fixture

Create an SSH server in `internal/testkit/sshserver` using `x/crypto/ssh`. It
generates an Ed25519 host key and client key per test, binds to `127.0.0.1:0`,
and exposes the exact SHA-256 host-key fingerprint. It supports only the minimal
exec and file-transfer operations needed by the probe tests.

This fixture validates authentication, host-key pinning, context cancellation,
timeouts, output limits, and secret cleanup on every platform. It is not used to
claim Linux or systemd compatibility.

### Tier 2: Docker Linux destination

Build `test/fixtures/sshd/Dockerfile` from a pinned Ubuntu 24.04 digest. The
fixture creates a non-root `drtui` user, OpenSSH, a writable staging path,
and deterministic test commands. Testcontainers launches it on a random local
port and generates credentials at runtime; no fixed private key is committed.

The connectivity probe must perform, in order:

1. Validate host, port, user, credential reference, and pinned fingerprint.
2. Resolve the host and open TCP within the connect deadline.
3. Complete an SSH handshake and reject any fingerprint mismatch.
4. Authenticate with the short-lived Ed25519 key.
5. Execute a no-shell probe that returns OS, architecture, free disk, and UTC.
6. Upload, hash, download, compare, and delete a random file through SFTP.
7. Optionally verify declared egress endpoints without sending credentials.
8. Return a typed `ConnectivityReport`; redact usernames and paths only where
   policy marks them sensitive, and always redact credentials.

Required negative tests cover wrong host key, wrong private key, unreachable
port, command timeout, oversized output, interrupted upload, and cleanup after
cancellation. The current workstation has the Docker CLI but its daemon is not
reachable, so enabling Docker Desktop is a prerequisite for this tier locally.

### Tier 3: disposable hosting-provider destination

Create a test-only native-CLI adapter for the selected hosting provider that
provisions one Ubuntu 24.04 instance, an encrypted root volume, and a firewall restricted to the
runner's current public IP. Tag every resource with `drtui-test`, run ID,
owner, and an expiry timestamp. A scheduled janitor deletes expired resources.

Generate a client key in memory, register only its public key, and publish the
SSH host-key fingerprint through the hosting provider's authenticated control plane
before direct SSH begins. Do not use `StrictHostKeyChecking=no`, trust-on-first-
use, embedded keys, or a private key persisted in provider state.

The first cloud system test executes only through `verify_target` and proves:

- Native-CLI JSON output decodes into `ProvisionResult`.
- The returned public IP is reachable on the restricted SSH port.
- The host fingerprint obtained through the selected provider matches the SSH handshake.
- Authentication, command execution, SFTP round trip, disk check, and HTTPS
  egress succeed.
- Rollback destroys the instance, volume, key registration, and firewall.

The full system test later reuses this destination for Coolify. It must run in a
dedicated sandbox project/account with a budget alarm and no production network
peering. Its hosting-provider CLI is required only when that provider declares it.

## Phased implementation

### Phase 0: protocol and build foundation

Deliverables:

- Pin Buf and protobuf C# generators; add `buf lint`, generation, and breaking
  change checks. Check generated C# into a dedicated protocol project.
- Extend the protocol with structured error details and a host-side secret lease
  broker. Plugins receive opaque, action-scoped handles and resolve them over
  authenticated inherited IPC; raw secrets never enter JSON action payloads.
- Define `plugins.lock.json`, package manifest, platform tuple, and OCI artifact
  schemas. Include protocol version and schema digest in every lock entry.
- Add canonical JSON and SHA-256 helpers with cross-platform golden vectors.
- Build a deterministic test plugin implementing every capability and scripted
  success, delay, crash, malformed-output, and retryable-error modes.

Tests and gate:

- Protocol lint and generated-code cleanliness pass in CI.
- A .NET host and an external fixture can complete handshake and one extension action.
- Lockfile and artifact digest golden tests pass on Windows and Linux.
- Secret handles reject the wrong process, action, invocation, and expired lease.

### Phase 1: plan loader and compiler

Deliverables:

- Strict YAML 1.2 loader with aliases disabled, size/depth limits, duplicate-key
  rejection, canonical JSON conversion, and JSON Schema validation.
- Provider metadata loading followed by provider config/action schema checks.
- Typed expression parser and resolver; no regex-based evaluation.
- Semantic checks for unique IDs, transaction references, transitive output
  dependencies, secret-flow restrictions, durations, and `forEach` item types.
- Stable DAG expansion, declaration-order tie breaking, and plan digest.
- `plan validate`, `plan graph`, and `run --dry-run` commands.

Tests and gate:

- Table tests and fuzzers cover YAML, expressions, dependency edges, and types.
- Golden output proves identical graph and digest across repeated runs.
- Every expression and action in the reference plan compiles against fixture
  provider manifests without invoking a provider action.

### Phase 2: execution engine and journal

Deliverables:

- Bounded scheduler, dependency blocking, conditions, deterministic `forEach`,
  context cancellation, fixed exponential retry, and per-step deadlines.
- Immutable state publication with runtime validation of advertised outputs.
- Compensating transactions with rollback-input snapshots and reverse
  topological rollback.
- Built-in `engine/removePath` constrained to the run work directory.
- Append-only JSONL journal with sequence numbers, redaction provenance, output
  hashes, attempt metadata, and a terminal run summary.
- `run --to` and the isolated `test step` harness.

Tests and gate:

- A fake clock makes retries and deadlines fast and deterministic.
- Fault-injection tests cover every state transition and rollback interruption.
- Concurrency and cancellation tests pass on Windows and Linux.
- Re-running fixture actions produces the same idempotency keys and journal
  ordering, excluding explicitly documented timestamps.

### Phase 3: plugin host and provider conformance kit

Deliverables:

- Verify the binary digest before execution, create an authenticated loopback or
  inherited-pipe channel, perform handshake, and negotiate protocol version.
- Implement lifecycle and capability-specific gRPC adapters plus extension
  action dispatch.
- Enforce process deadlines, output limits, graceful close, forced termination,
  and temporary-directory cleanup.
- Map gRPC statuses and provider details to `ProviderError` without leaking raw
  process output.
- Publish a .NET provider SDK and conformance suite for third-party providers.

Tests and gate:

- The scripted test plugin passes the full conformance suite as a child process.
- Digest mismatch prevents process launch.
- Crash, hang, bad handshake, incompatible protocol, malformed schema, invalid
  output, and secret-log attempts fail with stable redacted errors.

### Phase 4: remote transport and destination probe

Deliverables:

- Implement host-key-pinned SSH and SFTP transport with deadlines, bounded
  output, argument validation, and no ambient SSH agent fallback.
- Add `ConnectivityReport` and `drtui target probe`.
- Add Tier 1 and Tier 2 destination fixtures and all negative cases above.
- Add a built-in `engine/probeSSH` action and place it immediately after
  `provision_target` in the reference plan. Keep provider-specific prerequisite
  verification as a separate step.

Tests and gate:

- Unit tests pass on Windows without Docker.
- Docker integration proves a real Linux SSH and SFTP round trip.
- A wrong host-key fingerprint is always fatal and never retryable.

### Phase 5: providers, one vertical slice at a time

#### Cloudflare Secrets Store

The primary plan uses this keyvault, initialized through an explicitly named
bootstrap token environment variable and a configured Cloudflare account ID.
The local prerequisite is `cf`. Provider `initialize` verifies remote account
access; `fetchPlanSecrets` leases the Coolify encryption key, recovery Cloudflare
API token, and R2 S3 access-key ID/secret. Never expose them in process arguments,
plan values, or journals. Readiness currently checks only local token presence
and account-ID syntax, not remote authentication.

Advertised actions: `initialize`, `fetchPlanSecrets`.

Tests use a fake Cloudflare API and cover account scope, denied access, missing
secrets, lease expiry, cancellation, and redaction.

#### AWS Secrets Manager (optional provider for other plans)

Use the AWS SDK for .NET. `initialize` loads the selected credential chain and calls
STS `GetCallerIdentity`. `fetchPlanSecrets` reads one configured secret document
at `drtui/plans/<plan-id>`, requires a JSON string map, and returns leased
mutable values.

Advertised actions: `initialize`, `fetchPlanSecrets`.

Isolated tests use fake STS/Secrets Manager clients; integration uses LocalStack.
Acceptance includes missing keys, malformed JSON, denied access, expiry,
cancellation, and zero secret bytes in logs or journals.

#### S3-compatible storage

Use an S3-compatible .NET client with optional endpoint, region, path-style, and
TLS policy for S3, R2, Garage, and MinIO. The primary R2 plan supplies leased
Cloudflare access-key credentials; no AWS CLI, profile, or account is required.
Parse storage URIs structurally. Download to a
same-directory temporary file, stream with limits, verify a required checksum,
`fsync`, atomically rename, and remove partial files on every failure.

Advertised action: `downloadBlob`.

Add `readRecoveryManifest`: verify the supplied SHA-256, validate against
`schemas/recovery-manifest.schema.json`, and reject duplicate workload IDs,
invalid hostnames, unsafe paths, and unresolved workload/volume references.
Return typed workloads and tunnel routes. Object keys must identify exact
backups; never silently select `latest`. R2 Infrequent Access does not require
an AWS Glacier thaw workflow; account for retrieval fees separately.

Unit tests use a fake client; integration uses MinIO or LocalStack. Acceptance
includes ranged/retried downloads, checksum mismatch, cancellation, overwrite
policy, and work-directory escape rejection.

#### Native-CLI hosting infrastructure

Use the explicitly selected provider's CLI, not Terraform. Cloudflare compute
is selectable through `cf` but needs a Containers/Sandbox workflow rather than
the reference plan's SSH-based host recovery. Setup currently records the provider identifier and executable,
adds its PATH requirement, and prompts for provider-supported region, size,
and immutable image. Unknown/unsupported execution adapters must fail closed;
selection does not imply provisioning support.

Implement adapters with fixed argument planning, structured JSON output, pinned
CLI compatibility, scoped credentials, cancellation, bounded/redacted output,
and run-owned resources. The provider must return an SSH connection and host-key
fingerprint through an authenticated control plane. Destruction may affect only
resources created by that run. Use fake executables for unit tests, then a
dedicated disposable hosting project for system tests.

Advertised actions: `provision`, `verify`, `destroy`. These remain unimplemented.

#### Terraform infrastructure (optional for unrelated plans)

Wrap a pinned Terraform CLI through `ProcessStartInfo.ArgumentList` with cancellation; never scrape human
output. Use `terraform show/output -json`, a constrained module directory, an
explicit backend, and a per-run data directory. Normalize diagnostics into
structured errors. Preserve transient state through rollback in the same run;
cloud rehearsals use a dedicated encrypted remote backend so cleanup can recover
after runner failure.

Advertised actions: `provision`, `verify`, `destroy`.

Unit tests use a scripted fake executable. Integration uses a local-only test
module. The first hosting-provider system slice provisions the Tier 3 destination, runs the
probe, injects a later failure, and proves destroy removes every resource.

#### Proxmox Backup Server

Wrap the official `proxmox-backup-client` CLI behind `IBackupProvider`. Keep PBS
separate from Proxmox VE: PBS owns repository reachability, snapshot inventory,
archive inventory, and archive restore; it does not allocate or start guests.

Advertised actions: `probeRepository`, `listSnapshots`, `listSnapshotFiles`,
`restoreArchive`.

Always use component flags (`--server`, `--port`, `--datastore`, `--auth-id`),
JSON output, `PBS_PASSWORD_FD`, and a pinned `PBS_FINGERPRINT`. Reject restore
paths outside the engine work directory and never automatically retry a missing
or invalid archive signature with `--ignore-missing-signature`.

The standalone .NET implementation lives in [PbsClient.cs](../PbsClient.cs)
with an injected command runner. Unit tests use scripted JSON and failures, so
they run without a PBS installation. A Linux integration test remains planned:
it should install a pinned client and connect to a disposable PBS test datastore
containing host, VM, and container snapshots.
Acceptance covers token authentication, wrong TLS fingerprint, namespaces,
snapshot parsing, archive inventory, cancellation, bounded output, partial
restore cleanup, encrypted-archive fixtures, and no secret material in process
arguments, environment, errors, or journals.

#### Proxmox VE infrastructure

Implement Proxmox VE as a separate `IInfrastructureProvider`, using its HTTPS
API for normal lifecycle operations and narrowly scoped CLI execution only where
the API cannot express a restore. Extend `ProvisionRequest.Spec` with typed
guest modes: create from template, clone, restore VM from PBS, and restore
container from PBS. A PBS restore request carries the exact snapshot ID,
namespace, target storage, new VMID, and overwrite policy; selecting `latest`
implicitly is forbidden in production plans.

Advertised core actions: `provision`, `verify`, `destroy`. Advertised extension
actions: `restoreVM`, `restoreContainer`, `startGuest`, `stopGuest`.

Unit tests use fake PVE API and CLI clients. System tests require a dedicated
nested-virtualization Proxmox lab, restore a tiny synthetic guest from PBS,
obtain its guest-agent or DHCP address, run the destination SSH probe, inject a
failure, and prove guest plus temporary storage configuration are removed.
These tests are manual/scheduled and never target a production cluster.

#### Coolify application

Split SSH transport, Coolify API, backup parsing, and command planning behind
small interfaces. Pin the supported Coolify release and installer artifact by
checksum; do not pipe a remote script into a shell. Implement and test these
advertised actions independently:

```text
bootstrapRuntime              destroyRuntime
injectEnvironment             removeEnvironmentOverride
deployService                 removeService
pingDatabase
extractNetworkTopology
extractServiceDefinitions
verifyRoutes
```

Pure tests use synthetic backup metadata and golden service/network maps.
Transport tests use the Docker destination with a fake systemd/Coolify command
adapter. The disposable-host system test uses real systemd, Docker, Coolify, PostgreSQL, and
a tiny synthetic application. Every mutating action must have an idempotency and
compensation test before it enters the full plan.

#### GitHub Compose definitions and database volumes

The GitHub provider fetches Compose files through `gh` at exact commits, never
floating branches or tags. Validate checkout paths and include/extends paths;
do not run arbitrary repository hooks during retrieval. Its planned action is
`fetchCompose`, returning definitions for every declared workload.

A separate Compose application provider verifies remote Docker, daemon access,
Compose v2, and `cloudflared`. Do not require local Docker for remote execution.
Restore databases before applications, using run-owned isolated volumes. The
inventory declares `volume-archive` versus `database-native`, exact S3-compatible
objects and checksums, target volume mappings, database engine/image digests,
consistency provenance, and read-only verification adapters. Refuse unknown
formats, incompatible engine images, live-volume overwrites, unsafe extraction,
and implicit native-restore commands. Providers must validate Compose image
pins, secrets, dependent services, and volume ownership before mutation.
Archive extraction requires stopped volumes; native imports may start an
isolated database on a new run-owned volume before loading the dump. Neither
mode may overwrite a pre-existing workload's data.

Planned actions: `verifyRuntime`, `restoreDatabases`,
`removeRecoveredDatabases`, `verifyDatabases`, `deployApplications`,
`removeApplications`, `verifyRoutes`, `startConnector`, `stopConnector`.
Coolify bootstrap restores the control-plane backup and encryption key but
must suppress automatic workload and existing-tunnel startup. Install only
version/checksum-pinned remote prerequisites before `verifyRuntime`.

Tests use synthetic GitHub responses, stopped-volume archives, native dumps,
and a disposable Docker destination. Cover multiple database engines,
UID/GID/permissions, version mismatch, malicious archive paths, checksum
failure, incomplete backups, application-consistent provenance, database
verification failures blocking application deployment, and owned-resource-only
compensation.

#### Cloudflare Tunnel and DNS

Inject an HTTP client and API base URL. Resolve zones exactly, list managed
record types, perform idempotent batch upserts, snapshot prior records, and
verify through both the Cloudflare API and authoritative DNS. Never infer a zone
from an unvalidated public suffix.

Use `cf` as the local Cloudflare CLI prerequisite; the remote connector is
`cloudflared`, not Wrangler or `cf`. Create a replacement tunnel only after
database/application verification. Configure explicit hostname/service ingress
and a terminal deny rule, then start a connector using an action-scoped secret
lease, never token process arguments or journal payloads. The example selects
HTTP/2 over outbound TCP 7844; transport selection and egress probes must agree.
Verify the connector before changing public hostname routes.

Planned tunnel actions: `createTunnel`, `deleteTunnel`, `configureTunnel`,
`verifyTunnel`, `snapshotRoutes`, `syncTunnelRoutes`, `restoreRoutes`,
`verifyTunnelRoutes`. Non-tunnel plans may still use `syncRecords` and
`verifyRecords`. Snapshot exact old DNS records including absence, verify they
have not changed before cutover, and route to the replacement tunnel's CNAME,
not the recovered host's public IP. Rollback restores exact prior records only
if this run still owns the change, then stops the connector and deletes only
the new tunnel. Keep the previous tunnel intact. Providers must compensate
partial mutations before returning a failed action.

Unit tests use an injected fake HTTP handler. The live system test uses only a delegated
ephemeral subdomain in a dedicated test zone and always restores or deletes its
records. Test partial batch failure, rate limiting, proxy state, TTL, rollback,
and eventual DNS convergence with a bounded deadline.

### Phase 6: package, lock, signatures, and real digests

Deliverables:

- Implement safe `.drp` ZIP loading and creation, manifest verification, archive
  limits, path normalization, and immutable extraction.
- Implement OCI pull by digest and a content-addressed local plugin cache.
- Add `plugin build`, `plugin inspect`, `plugin lock`, and `plugin verify`.
- Produce reproducible binaries for Windows and Linux on amd64 and arm64.
- Package each plugin manifest plus platform blobs as an OCI artifact, publish to
  GHCR, and sign the manifest digest with Sigstore/Cosign identity policy.
- Generate the example lockfile and replace placeholder plan digests from the
  published OCI manifests. Never hand-edit these values.

Tests and gate:

- Archive traversal, symlink, duplicate-name, decompression-bomb, tamper, wrong
  platform, wrong digest, and invalid-signature tests all fail closed.
- Two clean builds of the same source and toolchain produce identical blobs.
- The packaged reference plan validates and dry-runs with network access removed
  after its plugins are cached.

### Phase 7: complete recovery rehearsal

Create synthetic fixtures: GitHub Compose definitions pinned to commits, a
database volume archive and a database-native dump with known migrations, a
Coolify configuration backup, two test hostnames, and immutable R2/S3-compatible
objects with checksums. Store credentials in sandbox Cloudflare Secrets Store
and use a delegated Cloudflare test subdomain.

Run the plan in three checkpoints:

1. `--to verify_target`: provision the selected host, pin and test SSH, then roll it back.
2. `--to verify_applications`: restore Coolify and Compose databases, verify schema and
   local routes, then roll everything back without touching DNS.
3. Full run: create a replacement test tunnel, verify its connector, update
   test hostname routes, verify public health, inject a post-DNS failure,
   prove compensation restores exact DNS records and removes the replacement
   connector/tunnel and host without deleting the old tunnel, then run
   once without injection and clean up explicitly.

The rehearsal passes only when the journal contains no secret material, every
resource is deleted, the test DNS zone is restored, and a second identical run
demonstrates provider idempotency.

## CI and release gates

Pull requests run formatting, vet, unit tests, race tests on Linux, schema tests,
Buf lint/breaking checks, secret scanning, and Docker integration. Windows runs
unit tests and the in-process plugin/SSH suites to protect cross-platform support.

Nightly or manually approved workflows use narrowly scoped Cloudflare and
selected-hosting-provider credentials, with short-lived federation where the
provider supports it. They enforce concurrency limits, cost ceilings, a hard
timeout, unconditional cleanup, and an independent expiry janitor. Provider OCI
artifacts publish only after unit, conformance, integration, vulnerability, and
provenance checks pass.

## Definition of done

- Every action use in the reference plan resolves to a built-in or signed,
  digest-pinned provider action with validated input/output schemas.
- The default test suite requires no external service and tests every engine
  state transition and provider action.
- A real Linux destination can be probed independently with mandatory host-key
  verification before any runtime mutation.
- Prefix runs and isolated step tests work without weakening production DAG or
  signature rules.
- The disposable-cloud drill restores synthetic data, verifies the database and
  application, changes only test DNS, proves compensation, and leaves no cloud
  resources behind.
- The committed production example contains generated real OCI digests and a
  lockfile whose signatures verify from a clean machine.

## Recommended implementation order

Start with Phases 0 through 4 in order. After the conformance kit and destination
probe are stable, Cloudflare Secrets Store and R2/S3 can proceed in parallel;
the selected infrastructure provider must complete before the disposable-host
destination test; Coolify follows the real SSH
probe; Cloudflare can proceed independently against its fake API. Package and
digest publishing starts once one provider passes conformance, then becomes the
release path for all remaining providers.