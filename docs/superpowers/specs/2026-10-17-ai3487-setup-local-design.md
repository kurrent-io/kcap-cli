# `kcap setup --local` on a fresh machine (AI-3487)

Umbrella: the Linear document *Local edition design*, sections "Setup", "Lifecycle transactions", "One server per
machine", "Reaching the server" and "Credentials". Server contract: kcap-server
`docs/superpowers/specs/2026-10-17-ai3488-pending-installation-fence-design.md`, "What AI-3487 must keep".

## Why

A user on a Mac with no cloud setup runs one command and ends with a running local stack, a recorded session and the
historical import offered. Everything after the server step (harness hooks, daemon service, import) already works
against an `http://localhost` server with provider `None`. What is missing is the install itself, done so that a
cancelled or failed setup leaves either a complete installation or none.

**Done when:** on a clean Mac, `kcap setup --local` ends with a running stack, a recorded session and the historical
import offered.

## Goals, non-goals, anti-goals

- **Goal:** one install core, shared in-process by two front ends: `kcap setup --local` for a terminal and
  `kcap setup --local --json` for the desktop wizard (AI-3499). The core reports typed events and never prompts.
- **Goal:** all-or-nothing. Before the point of no return a failure or cancellation rolls back; after it, anything
  that stops the commit is rolled forward by the next lifecycle command.
- **Goal:** keep the fence contract exactly: before the commit setup calls only `GET /healthz/ready` and
  `GET /auth/config`, and the journal is cleared only after the lift is proven.
- **Non-goals** (each has its own issue): converting a cloud machine (AI-3505), the client floor (AI-3495), recording
  namespaces and destination enforcement (AI-3502, AI-3503), `kcap local` commands (AI-3489), uninstall and purge
  (AI-3490), upgrade (AI-3494), Linux (AI-3898), Windows (AI-3903), Ollama (AI-3908), the wizard itself (AI-3499).
- **Anti-goals:** a prompt in `--json` mode; an unsigned bundle path; clearing the journal on anything weaker than a
  proven lift; deleting data setup did not create.

## Decisions taken with the user

- **Bundle source.** The CLI installs the bundle of its own version (`CapacitorVersion.CurrentDisplay()`), from
  `https://www.kurrent.io/download/local/<rid>/`. Two environment overrides exist for dev and pre-release builds:
  `KCAP_LOCAL_BUNDLE_BASE_URL` (replaces `https://www.kurrent.io/download/local`) and `KCAP_LOCAL_BUNDLE_VERSION`.
  The manifest must still verify against the embedded key. There is no unsigned path.
- **Flags.** `--single-user` is the shared-machine acknowledgement, `--yes` confirms the install. In `--json` mode, and
  with `--no-prompt`, a missing one is an error, never a prompt.
- **One PR**, closing AI-3487, with `README.md` and `help-setup.txt` in it.

## Front ends

### Arguments

`--local` selects the arm. It refuses `--server-url`, a positional tenant, `--discover`, `--org`, `--slug`, `--tier`,
`--invite-code`, `--device`, `--github` and `--muse-assistant`, each with an error naming the conflict. `KCAP_URL` set
in the environment is refused too: it would make every later step talk to another server.

`--json` is accepted only with `--local` (besides the existing `--discover --json`). It implies `--no-prompt` and runs
the server and login steps only: the commit writes the URL and the `None` stamp, and nothing else. The wizard owns
visibility, harnesses, the daemon and the import. Under `--local --json` only `--single-user`, `--yes` and
`--no-update-check` are accepted. The arm returns before the welcome rule, so nothing but event lines reaches stdout;
any narration goes to stderr.

### Terminal (`kcap setup --local`)

Step 1/6 "Server" becomes the local arm:

1. Run the preflight. A refusal prints its message and exits 1.
2. Print the single-user notice: the first version is for a machine with one user, and on a shared machine other OS
   users can open the web UI and act as its admin. Ask "Continue?" unless `--single-user`.
3. Ask "Install Capacitor local <version> in <root>?" unless `--yes`. When the preflight found an installation to
   reuse, this question is skipped.
4. Run the install core, rendering its events as status lines.
5. On `Committed`: `serverUrl` is the installation's canonical URL, `provider` is `None`, and login is complete. Step 2
   prints "No sign-in needed for a local installation." rather than an identity.

Steps 3–6 (visibility, harnesses, daemon service, import) run unchanged. The browser leg already skips itself for
`None`, and the import gate already passes.

### JSON lines (`kcap setup --local --json`)

One JSON object per line on stdout, snake_case, through a source-generated context. Every line carries `"v": 1` and
`"event"`. Human narration, if any, goes to stderr. The last line is `committed` (exit 0) or `failed` (exit 1).
`failed` carries a stable `code` and a `message`. The event list is under "Events".

## The install core

`LocalInstaller` in `src/Capacitor.Cli/LocalEdition/`. Two calls:

- `Preflight()`: no lock, no network, no writes. Returns either a refusal or a plan hint, which tells the front end
  which questions to ask.
- `InstallAsync(answers, IProgress<LocalInstallEvent>, ct)`: runs under the lifecycle lease through
  `LocalLifecycle.RunAsync`, so an interrupted earlier setup is recovered first. Then it decides the plan for real,
  from the state recovery left. Returns `Committed(installation)` or throws `LocalInstallFailure(code, message)`.
  The answers (`SingleUser`, `Confirmed`) are required only when the decided plan is **install**; a false one is
  `answer_required`. Reuse needs neither, so a front end that passes both flags works whichever plan it gets.

`LocalLifecycle` has no composition site yet. The setup command builds the first one, with `LocalSetupRecovery`
registered.

### Preflight

Unlocked checks, in order, stopping at the first refusal (checks 4–6 are rerun under the lease, after recovery):

1. **Platform.** macOS on arm64 passes. macOS on x64: "there is no Intel Mac build" (`unsupported_platform`). Linux on
   musl: "the musl builds of kcap do not support a local installation" (`musl_unsupported`), checked before the
   generic Linux refusal so it is the message a musl user sees. Linux otherwise, and Windows: "the local edition runs
   on macOS for now" (`unsupported_platform`). Facts come through a seam so tests cover the matrix.
2. **Socket paths.** The UTF-8 byte length of `<root>/run/.s.PGSQL.5432` and of `<root>/run/kurrentdb.sock`, with the
   root's existing ancestors resolved through symlinks, must be at most 103 on macOS (107 on Linux). A refusal names
   both paths and lengths (`socket_path_too_long`). The second socket is checked although the embedded node does not
   use it yet: the design names both, and a root that fits one byte less costs nothing.
3. **Ancestors.** No existing ancestor of the root may be world-writable without the sticky bit, which the server
   refuses at start (`unsafe_root`). Refusing here avoids a ten-minute readiness wait on a server that cannot boot.
4. **Journal and registry.** A `setup` journal present: the hint is **recover**. Recovery runs under the lease before
   checks 5–6 are rerun, so a crashed or half-rolled-back setup never blocks its own recovery. Without one, an
   unreadable registry is `registry_unreadable`, and a registry with an active installation gives the hint **reuse**.
   Otherwise the hint is **install**.
5. **Profiles.** Any profile whose `server_url` is non-blank and does not classify as the active installation:
   `other_server` naming the profile and its server, and saying conversion arrives later. On the reuse path the active
   installation's own profile passes. Skipped on the recover hint, and rerun after recovery.
6. **Leftover data** (install plan only). `<root>/data` existing is `unknown_data`, naming it, whatever the registry
   says. Nothing is deleted. A registry with no active installation and retained data cannot arise: uninstall without
   purge keeps the installation active, and a purge deletes the data. So an install never sits on data it did not
   create, and its rollback never deletes such data.

The terminal asks its questions from the hint: on **install**, or on **recover**, which can still end in an install. A
plan that changes under the lease is decided there. If it became reuse, nothing more is needed. If it became install
without the answers, it is `answer_required`.

### The install transaction

Under the lease, after recovery, the preflight checks 4–6 are rerun: they are the preconditions the lock exists to
hold. Then:

1. **Port.** 9473, the server's default, when it is free on both `127.0.0.1` and `::1` and not a retired port;
   otherwise an OS-assigned free loopback port that is not retired. Emits `port_chosen`.
2. **Begin.** The journal records `operation: "setup"`, `phase: "pending"`, `server_start: "fenced"`, the new
   installation id (32 hex characters), and details: `port`, `version`, `server_url`, `config_dir`, `profile` (the
   active profile's name). It is begun before the download so that a crash during the download is cleaned up by
   recovery like any other pending setup.
3. **Download and verify** (see "The bundle"). Emits `downloading`, `download_progress` (throttled), `verified`.
4. **Unpack** into `bin/<version>/`, then point `bin/current` at it with the atomic relative-symlink switch
   `ScriptUpdater.SwitchLink` already uses. Emits `unpacked`.
5. **Install and start the unit** with `ILocalServerService.Install(LocalServerUnit.ForInstallation(root, port))`.
   Emits `starting`.
6. **Readiness.** `LocalServerReadiness.WaitAsync` on `/healthz/ready`, with the existing ten-minute budget. Emits
   `ready`.
7. **Identity check.** `GET /auth/config` must answer `200` with provider `None` **and** `Kcap-Installation: pending`.
   Anything else means the port is answered by something other than this pending installation: `not_our_server`,
   rolled back.
8. **Commit** (see "The commit"). Emits `committing`, then `committed`.

Every HTTP call to the local server goes through one named client with no credential, no proxy, no redirects and no
observation headers, always on the canonical `localhost` authority.

### The commit

The order is the server contract's:

1. **Point of no return.** `Advance` the journal to `phase: "committing"`, `server_start` still `fenced`. From here
   nothing deletes the installation, cancellation is ignored, and every failure leaves the journal for recovery to roll
   forward.
2. **Registry.** `PublishRegistry(registry.WithActive(installation))`. Skipped when the registry already names it.
3. **Profile.** One config mutation through `CommitBoundary.CommitAsync`: the active profile's `server_url` set to the
   canonical URL (`PointProfileAtServer`) and its stamp set to `None` on that URL. No token publication. Idempotent.
4. **Allow.** `Advance` to `phase: "allowed"`, `server_start: "allow"`.
5. **Confirm the lift.** Poll `GET /auth/config` for up to ten seconds (the server polls every 250 ms). A `200` with
   provider `None` and no pending header proves it. Anything else (a 4xx, a redirect, a malformed body, a connection
   error, a 200 still carrying the header) proves nothing. When polling proves nothing, stop and start the unit, wait
   for readiness, then require the same proof again. Without it the step fails and the journal stays at `allowed`.
6. **Complete.** Clear the journal.

The restart fallback requires the proof again, rather than counting the restart itself as proof. That is stricter
than the server contract, and it makes "a loopback 4xx or a 200 still carrying the header never leads to the journal
being cleared" hold without exception.

### Rollback

On a failure or cancellation before the point of no return:

1. Unload the unit with `ILocalServerService.UninstallAsync`: a verified stop, so no restart policy or reboot can start
   it and no store it owns is alive, then the plist removed. This runs even when `Install` failed partway; an absent
   label is fine.
2. Delete the installation: `bin/<version>`, `bin/current`, any `bin/current.new-*` left by an interrupted link switch,
   any `bin/.staging-*`, `data/` and `run/`. `logs/` is kept,
   because the failure message names the log to read.
3. Clear the journal.

If step 1 fails, nothing is deleted and the journal stays at `pending`: the next lifecycle command retries the
rollback. Emits `rolling_back`, then `rolled_back`, and the original failure is reported.

### Recovery

`LocalSetupRecovery : ILifecycleRecovery` for operation `"setup"`, registered wherever `LocalLifecycle` is built:

- `pending` → the rollback above.
- `committing` → registry, profile (using the journal's `config_dir` and `profile`, not the current process's), allow,
  confirm, complete.
- `allowed` → confirm, complete.
- Any other phase is unreadable to this build: it throws the "needs a newer kcap" error and changes nothing.

A crash during the `allow` replacement leaves either the `committing` or the `allowed` journal, because the replace is
atomic, and both roll forward. Recovery emits `recovering` and `recovered` (with the operation and phase).

Until AI-3489 adds `kcap local`, the only lifecycle command is setup, so an interrupted setup is finished by the next
`kcap setup --local`. Meanwhile a crashed pending setup can leave a fenced server running. The fence keeps it harmless:
it acknowledges no write and calls nothing outside the machine.

### Reuse

When the registry already has an active installation, the core skips the transaction:

- Install the unit if it is missing, otherwise start it.
- Wait for readiness.
- Publish the profile (step 3 of the commit).
- Emit `reused`, with the installation's version. When that differs from this CLI's, say that upgrades arrive with
  `kcap local upgrade`.

A second setup therefore downloads nothing and takes no second port.

### Cancellation

Today `InteractiveLifetime` turns Ctrl+C, SIGTERM, SIGHUP and the parent-death watchdog into `Environment.Exit(130)`,
which runs no `finally`. The install core needs its rollback to run, so `InteractiveLifetime` gains a deferral scope.
While the core holds one:

- An interrupt cancels the core's token and returns at once. Signal handlers share one dispatch thread, so a handler
  that waited would block the next signal.
- The process exits 130 when the scope ends.
- The watchdog's repeated calls only cancel again, so a dead wizard never cuts a rollback short.
- A second Ctrl+C or SIGTERM exits at once. The journal covers that case.

Before the point of no return, cancellation means rollback. After it, the commit runs to completion. The desktop
wizard cancels with SIGTERM.

## The bundle

`LocalBundleSource`, a typed `HttpClient` registered like `KcapReleaseClient`: no credential, no observation headers,
redirects followed (the files sit behind a CDN).

URLs: `<base>/<rid>/kcap-local-<version>-<rid>.json`, `.json.sig` and `.tar.gz`.

1. Fetch the manifest bytes and the signature.
   - A 404 on the manifest is `bundle_not_published`, naming the version and the two overrides.
   - Any other failure is `download_failed`.
2. Verify with `LocalBundleManifestKey.Verify` over the bytes exactly as downloaded. A failure is
   `verification_failed`.
3. Parse the manifest, which must have `schema: 1`. Then check, each failure `verification_failed`:
   - `key_id` equals the embedded key's;
   - `version` and `rid` equal what was asked for;
   - `archive` equals `kcap-local-<version>-<rid>.tar.gz`;
   - `sha256` is 64 lowercase hex characters;
   - `size` is positive.
4. Stream the archive into `bin/.staging-<installation id>/`, with a ten-minute deadline that covers the body.
   - The transfer stops if it exceeds `size`.
   - The size and the SHA-256 are checked before anything is unpacked.
5. Unpack with a `TarReader` loop of our own, not `TarFile.ExtractToDirectory`. Each entry must be:
   - a regular file, a directory, a symlink whose target resolves inside the tree, or a hard link to an earlier file
     entry in the tree, which is written as a copy (bsdtar emits hard links for multiply-linked files);
   - free of absolute paths and `..` components (a device or FIFO is refused).

   Names may carry a leading `./`, and the root entry `.` is accepted.

   File modes are taken from the entry, masked to the owner. Then `kcap-server` must exist as an executable regular
   file at the tree's root, else `verification_failed`.
6. Move the tree to `bin/<version>`, replacing a leftover one, and switch `bin/current`.

## Journal: the third `server_start` value

`LifecycleJournalEntry.ServerStartAllowed` (bool) becomes `ServerStart`, an enum `Allow | Fenced | Deny`. The wire maps
it to `"allow" | "fenced" | "deny"`. Any other value is unreadable, as today. `LifecycleLease.Begin` and `Advance` take
the enum. Recovery dispatch is unchanged: a `fenced` journal is now readable and goes to its operation's recovery.

## Events

The `event` names for JSON, and the typed records the terminal renders:

| `event` | Fields |
| -- | -- |
| `preflight_passed` | `plan` (`install` or `reuse`), `version`, `root` |
| `recovering` | `operation`, `phase` |
| `recovered` | `operation`, `phase` |
| `port_chosen` | `port` |
| `downloading` | `url`, `bytes` |
| `download_progress` | `received`, `bytes` |
| `verified` | `sha256` |
| `unpacked` | `path` |
| `starting` | `unit` |
| `ready` | `server_url` |
| `committing` | — |
| `reused` | `server_url`, `installed_version` |
| `rolling_back` | `reason` |
| `rolled_back` | — |
| `committed` | `server_url`, `installation_id`, `port`, `version` |
| `failed` | `code`, `message` |

Failure codes: `unsupported_platform`, `musl_unsupported`, `socket_path_too_long`, `unsafe_root`,
`registry_unreadable`, `other_server`, `unknown_data`, `answer_required`, `invalid_arguments`, `lifecycle_busy`,
`bundle_not_published`, `download_failed`, `verification_failed`, `start_failed`, `not_our_server`, `commit_failed`,
`recovery_failed`, `cancelled`.

## Docs

- `README.md`: `kcap setup --local` in "Initial setup" and the command table, the two flags, the `--json` mode, and the
  single-user statement.
- `help-setup.txt`: `--local`, `--single-user`, `--yes` and `--json`.

## Tests

- **Journal:** `fenced` round-trips; an unknown value is unreadable; `Begin` and `Advance` write each value.
- **Preflight:** the platform matrix with musl's own message; a socket path one byte over and exactly at the limit; a
  world-writable ancestor with and without the sticky bit; a profile for another server; reuse; leftover data.
- **Bundle:** a good manifest against a throwaway key. Each refused: a bad signature, a foreign `key_id`, the wrong
  version, rid or archive, a size or digest mismatch, an oversize body, and a 404 giving `bundle_not_published`. The
  tar: `..`, an absolute path, a symlink out of the tree, a hard link out of the tree, a device entry, a missing
  `kcap-server`. A `./`-prefixed tree with a hard link inside it unpacks.
- **Preflight vs recovery:** a pending journal with `data/` and no registry is recovered, not refused.
- **Cancellation:** while a deferral scope is held, an interrupt cancels and returns; repeated watchdog calls do not
  exit; the exit follows the scope's end.
- **Reuse:** no answers are required.
- **Transaction** (fake service that records calls into a shared log, fake local-server handler, temp root and
  config):
  - The happy path. The order holds: unit installed, `/healthz/ready`, `/auth/config` with the header, journal
    `committing`, registry, profile, `allowed`, lift proof, journal cleared.
  - No other request reaches the local server before the commit.
- **Acceptance cases from the server contract.** A seam throws at named points, then recovery runs and must end
  committed, with the fence lifted and the journal cleared, for a crash:
  - after the `committing` advance;
  - between the registry and the profile;
  - after both;
  - during the `allow` replacement, both sides of it.
- **Rollback cases:**
  - A cancel before the point of no return rolls back with the unit unloaded before anything is deleted and before
    the journal is cleared.
  - A failed unload leaves the journal and deletes nothing.
- **Lift proof:**
  - A loopback 4xx with no header, and a 200 still carrying the header, never clear the journal, including after the
    restart fallback.
  - A 200 without the header after the restart does clear it.
- **Reuse:** no download and no second port.
- **Setup command:**
  - Each conflicting flag is refused.
  - `--json` and `--no-prompt` without `--single-user` or `--yes` give `answer_required`, as a `failed` line in JSON.
  - The JSON lines for a fake core are pinned.
  - The terminal arm hands the installation's URL and `None` to the later steps.
- **Live:** an opt-in end-to-end on macOS with a real published bundle, run by hand for "done when".

## Calls I made

- The journal is begun before the download, not after: a crash mid-download is then cleaned up by recovery.
- Port 9473 when free, otherwise an OS-assigned port; never a retired one.
- The identity check before the commit requires the pending header, so a stranger on the port is never committed.
- The lift fallback restarts the unit and then requires the same proof again.
- Rollback keeps `logs/`.
- Reuse never upgrades; it says so when the versions differ.
- The preflight also refuses a world-writable ancestor, so a root the server would refuse never waits ten minutes.
- `--local` refuses `KCAP_URL` and the cloud-only flags; `--json` runs the server and login steps only, and the wizard
  owns visibility.
- Hard links in the bundle are unpacked as copies, not refused.
- The answers are required only for an install, never for a reuse.
- Any existing `data/` refuses an install, whatever the registry says.
- The profile commit maps `AuthResult.Failed` to `commit_failed`, and recovery builds its `ConfigRoot` from the
  journal's `config_dir`.
- Cancellation is a deferral scope in `InteractiveLifetime`; a second interrupt exits at once.
- The tar is unpacked by our own loop with containment checks, not `TarFile.ExtractToDirectory`.
- The install core and the two renderers live in `Capacitor.Cli` (beside the service adapter), not in Core.
