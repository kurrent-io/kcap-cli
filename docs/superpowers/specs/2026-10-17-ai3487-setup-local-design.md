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
2. **Socket paths.** The UTF-8 byte length of `<root>/run/.s.PGSQL.5432` and of the embedded KurrentDB's
   `<root>/data/kurrentdb/kurrent.sock`, under the root as configured and under the root with its existing ancestors
   resolved through symlinks, must be at most 103 on macOS (107 on Linux). A refusal names each path that does not fit
   with its length, and how many bytes shorter the root must be (`socket_path_too_long`). `LocalSockets` is the one
   place the CLI derives these paths.
3. **Ancestors.** No existing ancestor of the root may be world-writable without the sticky bit, which the server
   refuses at start (`unsafe_root`). Refusing here avoids a ten-minute readiness wait on a server that cannot boot.
4. **Journal and registry.** A `setup` journal present: the hint is **recover**. Recovery runs under the lease before
   checks 5–6 are rerun, so a crashed or half-rolled-back setup never blocks its own recovery. Without one, an
   unreadable registry is `registry_unreadable`, and a registry with an active installation gives the hint **reuse**.
   Otherwise the hint is **install**.
5. **Profiles.** Any profile whose `server_url` is non-blank and does not classify as the active installation:
   `other_server` naming the profile and its server, and saying conversion arrives later. On the reuse path the active
   installation's own profile passes. Skipped on the recover hint, and rerun after recovery.
6. **An empty slot** (install plan only). Each of these refuses, with `leftover_state` naming what was found, and
   nothing is deleted:
   - any of `<root>/bin`, `<root>/data` or `<root>/run`;
   - the server unit's plist, or its label loaded in launchd.

   The label is fixed per user, not per root, so a unit owned by another `KCAP_LOCAL_ROOT` is refused here rather than
   touched (see "The per-user unit"). A registry with no active installation and retained data cannot arise: uninstall
   without purge keeps the installation active, and a purge deletes the data. What an install's rollback deletes is
   therefore only what that attempt created.
7. **Config.** Read strictly: an unreadable `config.json` is `config_unreadable`, never a fresh default. Like check
   5, it is skipped on the recover hint and run under the lease after recovery. Rolling back needs no config, and
   rolling forward reads the journal's `config_dir` strictly at the moment it publishes. An unreadable config in
   the invoking process therefore never strands a recovery.
8. **Version.** The version to install must pass `ReleaseManifest.IsValidVersion`. That holds for the
   `KCAP_LOCAL_BUNDLE_VERSION` override too: it becomes a path segment. A failure is `invalid_arguments`.

The terminal asks its questions from the hint: on **install**, or on **recover**, which can still end in an install. A
plan that changes under the lease is decided there. If it became reuse, nothing more is needed. If it became install
without the answers, it is `answer_required`.

### The install transaction

Under the lease, after recovery, the preflight checks 4–7 are rerun: they are the preconditions the lock exists to
hold. The empty-slot check of the unit (check 6) is made while holding the unit lock (see "The per-user unit"), which
is then held through `Install`. Then:

1. **Port.** 9473, the server's default, when it is free on both `127.0.0.1` and `::1` and not a retired port;
   otherwise an OS-assigned free loopback port that is not retired. Emits `port_chosen`.
2. **Begin.** The journal records `operation: "setup"`, `phase: "pending"`, `server_start: "fenced"`, the new
   installation id (32 lowercase hex characters), and details:
   - `port`, `version`, `server_url`;
   - `config_dir`, as an absolute normalized path captured before `Begin`;
   - `profile` (the active profile's name).

   It is begun before the download, so a crash during the download is cleaned up by recovery like any other pending
   setup.
3. **Download and verify** (see "The bundle"). Emits `downloading`, `download_progress` (throttled), `verified`.
4. **Unpack** into `bin/<version>/`, then point `bin/current` at it with the atomic relative-symlink switch
   `ScriptUpdater.SwitchLink` already uses. The tree is durable first (see "Durability"). Emits `unpacked`.
5. **Install and start the unit.** First `Advance` to `phase: "unit"` (still `fenced`), so recovery knows a unit may
   exist. Then `ILocalServerService.Install(LocalServerUnit.ForInstallation(root, port))`, with the plist written
   durably. Emits `starting`.
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

   An `Advance` that throws is ambiguous. The rename may have landed while the directory sync failed. So the
   in-process rollback eligibility ends when the `committing` write is *attempted*, not when it returns. On any
   exception from that write the process deletes nothing and fails with `commit_failed`. The next recovery decides
   from what is on disk: `pending` or `unit` rolls back, `committing` rolls forward.
2. **Registry.** `PublishRegistry(registry.WithActive(installation))`. It is never skipped, even when the registry
   already names the installation. A visible registry is not proof of a durable one: an earlier attempt may have
   renamed it and crashed before its directory sync. Republishing makes that sync before `allow` can become visible.
3. **Profile.** One durable config mutation through `CommitBoundary.CommitAsync`: the active profile's `server_url`
   set to the canonical URL (`PointProfileAtServer`) and its stamp set to `None` on that URL. No token publication.
   Idempotent.
   - It carries a precondition, `CommitPrecondition.OnlyServer(url)`, checked inside the config lock on a strict read:
     every profile's `server_url` is blank or the installation's.
   - An unreadable config or a conflicting profile fails with `commit_failed` and names it. The journal stays at
     `committing`, so the next run retries once the user has removed or repointed that profile. That is never a
     rollback, and never a silent overwrite.
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

On a failure or cancellation before the point of no return, with a token and budgets of its own (the install's token
is already cancelled):

1. **Unit.** At phase `unit`, under the unit lock, read the owner evidence (see "The per-user unit") for the loaded
   job and for the on-disk unit separately, then decide:
   - **Ours.** Every object that is present names this root. Unload with `ILocalServerService.UninstallAsync`: a
     verified stop, so no restart policy or reboot can start it and no store it owns is alive, and then the plist is
     removed. This runs even when `Install` failed partway; an absent label is fine.
   - **Not ours.** Every object that is present positively names another root, or nothing is present. Leave it
     untouched; nothing that exists can start this root. That happens when this setup crashed before its `Install`
     and another root has installed since. A foreign job is never stopped to obtain proof.
   - **Unknown.** Anything else: unreadable evidence, a loaded job and a plist naming different roots, or a loaded job
     whose root cannot be read. Delete nothing, keep the journal, and fail with `recovery_failed` naming the label
     and the plist. A probe that finds nothing alive is not enough here: a throttled or between-starts job can start
     again a moment later.

   At phase `pending` no unit was attempted, and the preflight proved none existed, so this step is skipped.
2. **Nothing alive.** After step 1 has excluded every restart, `LocalStackProbe.FindLive(root)` must report nothing of
   this root's stack alive.
3. **Delete** the installation: `bin/`, `data/` and `run/` whole. The empty-slot precondition proved this attempt
   created all three. `logs/` is kept, because the failure message names the log to read.
4. **Sync** the root. At phase `unit`, also sync `LaunchAgents` whenever it exists, whether or not this process
   removed the plist: an earlier attempt may have unlinked it and crashed before its sync. An absent `LaunchAgents`
   needs no sync and is never created.
5. **Clear** the journal.

If step 1 or step 2 fails (the unload throws, the label stays loaded, a postmaster survives, or the owner is unknown),
nothing is deleted. The journal stays exactly as it was, `unit`/`fenced`, and `rolled_back` is not emitted. The next
lifecycle command retries, with another verified unload before any deletion. Otherwise it emits `rolling_back`, then
`rolled_back`, and reports the original failure.

### The per-user unit

The launchd label `io.kurrent.kcap.local` and its plist are per user, while the lifecycle lock is per root. Two
setups under different `KCAP_LOCAL_ROOT`s therefore need two more things:

- **A unit lock.** It is a lock file at the fixed `~/.config/kcap/local-unit.lock`, which ignores both
  `KCAP_LOCAL_ROOT` and `KCAP_CONFIG_DIR`, as the daemons directory does. It is taken after the lifecycle lock and
  before any config lock. Every check of the unit, and every `Install`, unload or removal of it, holds it. Two roots
  can then never both pass the empty-slot check and replace each other's unit.
- **Owner evidence.** `ILocalServerService` gains `Owner(unit)`. It returns two readings, one for the loaded job and
  one for the on-disk unit, because launchd keeps the definition it loaded however the plist changes later.
  - On launchd, the loaded job is read from `launchctl print`'s environment, and the plist is parsed with
    `LaunchdUnit.TryReadPlist`. On systemd, `systemctl --user show -p Environment` and the unit file are read.
  - Each reading is `Absent`, `Root(path)` or `Unreadable`.
  - Rollback, recovery and reuse act on the unit only when every present reading names this root.
- **Root identity.** "Names this root" is decided by filesystem identity, not spelling. On macOS, `/tmp/kcap-A`,
  `/private/tmp/kcap-A`, a case variant and a firmlinked `/System/Volumes/Data/...` path all reach one directory.
  - If both paths resolve with `CanonicalPath.TryResolve` to *ordinally* equal paths, they are the same root.
    `PathComparison` is not used: it ignores case on every Mac, including case-sensitive volumes.
  - Any other difference, case-only included, goes to a nonce probe. Holding the lifecycle lock, write `<root>/.identity-<random>`, durability not needed,
    and test whether the same name exists under the other path. If it does, it is the same root. If the other path
    does not exist, it is a distinct root: it cannot alias one that exists. The nonce is deleted afterwards.
  - Any I/O error along the way makes the reading `Unknown`, never "another root".

### Recovery

`LocalSetupRecovery : ILifecycleRecovery` for operation `"setup"`, registered wherever `LocalLifecycle` is built.

Before it changes anything it validates the entry, and on any failure it changes nothing and fails with
`recovery_failed`, naming the field:
- the installation id is 32 lowercase hex characters;
- `version` passes `IsValidVersion`;
- `port` is 1–65535, and `server_url` is exactly `http://localhost:<port>`;
- `config_dir` is fully qualified, and `profile` is a valid profile name;
- the phase and `server_start` pair is one of `pending`/`fenced`, `unit`/`fenced`, `committing`/`fenced`,
  `allowed`/`allow`.

Then, by phase:
- `pending` and `unit` → the rollback above.
- `committing` → registry, profile (using the journal's `config_dir` and `profile`, not the current process's), allow,
  confirm, complete. Before confirming, a missing `bin/current/kcap-server` is `recovery_failed`, and a missing plist
  is reinstalled with `Install`.
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
- Require the lift proof: `GET /auth/config` answers `200` with provider `None` and no pending header. Without it the
  outcome is `not_our_server`, and the profile is not touched. A readiness `200` alone could come from another
  process holding the port.
- Publish the profile (step 3 of the commit, with the same precondition).
- Emit `reused`, with the installation's version. When that differs from this CLI's, say that upgrades arrive with
  `kcap local upgrade`.

A second setup therefore downloads nothing and takes no second port.

### Cancellation

Today `InteractiveLifetime` turns Ctrl+C, SIGTERM, SIGHUP and the parent-death watchdog into `Environment.Exit(130)`,
which runs no `finally`. The install needs its rollback to run, so `InteractiveLifetime` gains a deferral scope.

**Who holds it.** The front end holds the scope, not the core. It spans `InstallAsync` and the writing of the final
event, so the terminal result is rendered and flushed before anything exits.

**While it is held:**
- An interrupt cancels the install's token and returns at once. Signal handlers share one dispatch thread, so a
  handler that waited would block the next signal.
- The watchdog's repeated calls only cancel again, so a dead wizard never cuts a rollback short.
- A second Ctrl+C or SIGTERM exits 130 at once. The journal covers that case.
- When the scope ends after an interrupt, the process exits 130.

**The contract:**
- Before the point of no return, a cancellation rolls back. The last JSON line is then `failed` with code
  `cancelled`, and the exit code is 130.
- After it, a cancellation is ignored until the commit finishes. The last line is `committed` or `failed` as it
  happened, and the exit code is 130 because an interrupt was received. The terminal front end does not continue to
  the later steps.
- The desktop wizard cancels with SIGTERM.

## Durability

The journal's phases describe files, and the files must be on disk when a phase says so. A file is durable when its
data is flushed and its directory entry is synced.

**Before `Advance` to `unit`:**
- every file and directory of the unpacked tree, flushed as it is written;
- the tree moved to `bin/<version>`, and `bin/` synced;
- `bin/current` switched, and `bin/` synced again;
- the root's entry in its parent synced, and each managed directory synced after it is created.
  `LocalRoot.EnsureCreated` gains this.

**The plist:** written through a durable variant of `ServiceFiles.WriteOwnerOnly`, which flushes the file and syncs
`LaunchAgents`. It is written before `Install` returns, so it is on disk before `committing`.

**The profile:** written through a durable `ConfigMutator` path, which flushes the temp file before the rename and
syncs the config directory after it. The commit uses it, so `allowed` is never published ahead of the profile it
depends on. `ConfigMutator` also flushes its temp file before every rename, durable or not. The later step-5 rewrite
of the same file therefore cannot leave an empty `config.json` after a power loss.

**New directories:** every directory setup creates goes through one helper. It creates missing components one at a
time, owner-only where the layout requires it.

Existence is not proof of durability: an interrupted earlier attempt may have created a component and never synced
its parent. So the helper syncs the parent of *every* component between a fixed anchor and the leaf, whether that
component was created now or found existing. The anchor is the user's home for paths under it, else the filesystem
root. The guarantee is therefore idempotent across attempts, at the cost of a few directory syncs. The helper covers:
- the root and its managed directories;
- a new config directory (`ConfigMutator` uses it);
- a new `LaunchAgents` (the durable `ServiceFiles` variant uses it).

Each dependent phase or publication comes after those syncs.

**Rollback:** the removals are synced before the journal is cleared.

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
6. Move the tree to `bin/<version>` and switch `bin/current`. Neither can pre-exist: the empty-slot precondition
   refused any `bin/`.

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
`registry_unreadable`, `config_unreadable`, `other_server`, `leftover_state`, `answer_required`, `invalid_arguments`, `lifecycle_busy`,
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
- **Empty slot:** pre-existing `bin/`, `data/`, `run/`, a plist or a loaded label each refuse an install and survive
  untouched. That includes a unit owned by another root.
- **Failed unload:** the journal stays `unit`/`fenced`, and `rolled_back` is not emitted. A second recovery makes
  another verified unload before any deletion, including when the first attempt unloaded the label but left a
  postmaster alive.
- **Per-user unit:**
  - Setup A records `unit`, then crashes before `Install`. Setup B, under another root, commits. A's recovery then
    leaves B's unit loaded and its plist in place.
  - Two roots set up concurrently: exactly one passes the empty-slot check, and the other is refused.
- **New directories:**
  - Ordered fault tests for a new multi-level config path and an absent `LaunchAgents`. Each new component's parent
    is synced before the publication that depends on it.
  - A two-run fault test: the first run is interrupted after a mkdir and before its parent sync. Recovery against the
    still-visible directory must make the missing parent sync before `allowed`.
- **Owner evidence:** each of these deletes nothing of this root and keeps its journal:
  - a loaded job with no readable plist and no live process at the moment;
  - a loaded job and a plist naming different roots;
  - an unreadable plist with an absent label.
- **Root identity:** recovery through an ancestor alias (`/tmp/x` against `/private/tmp/x`), with the job between
  starts, treats the unit as ours and makes a verified unload before any deletion. A genuinely distinct root is
  treated as another root's. An I/O error in the probe is `Unknown`.
  - Case-only spellings go to the probe. On a case-sensitive filesystem, a case sensitive disk image when the test
    can create one and skipped otherwise, two roots differing only in case are distinct, and recovery leaves the
    foreign unit and plist untouched. On a case-insensitive one, the probe recognises a case variant as the same root.
- **Registry rename then crash:** the first run renames the registry and crashes before its directory sync. Recovery
  republishes it durably before `allow` becomes visible.
- **Plist unlink then crash:** the first run unlinks the plist and crashes before syncing `LaunchAgents`. Recovery
  syncs `LaunchAgents` before it clears the journal.
- **Fresh home:** a pending setup that fails at the manifest, or is cancelled, with no `~/Library/LaunchAgents`
  clears its journal and never creates that directory.
- **Config vs recovery:** a corrupt current config does not block `pending` or `unit` recovery. A `committing`
  recovery with a healthy original config, under an unreadable different current override, publishes to the original.
- **Validation:**
  - An invalid `KCAP_LOCAL_BUNDLE_VERSION` (absolute, `..`, not semver) is refused before `Begin`.
  - A malformed but parseable setup journal (bad id, version, port, URL, relative `config_dir`, or a mismatched
    phase/`server_start` pair) leaves every file and the journal untouched.
- **Ambiguous commit write:**
  - Injection before the `committing` rename, after it, and at its directory sync: none deletes anything in process.
  - Recovery then rolls back from `pending`/`unit`, or forward from `committing`.
- **Durability:** a fault seam on the config writer. A profile flush failure keeps a `committing` journal, and
  `allowed` is never written before the profile is durable.
- **Profile precondition:**
  - A profile repointed to a cloud server, or a corrupted config, between preflight and commit gives `commit_failed`
    with the journal kept at `committing`.
  - The same state before recovery: recovery refuses until it is fixed, then rolls forward.
- **Relative config dir:** setup with a relative `KCAP_CONFIG_DIR` records an absolute `config_dir`. Recovery from
  another working directory, under another config override, publishes to the original.
- **Reuse proof:** readiness `200` followed by a foreign provider, a malformed or redirected `/auth/config`, or a
  pending header gives `not_our_server` with no profile mutation.
- **Cancellation contract:** `InteractiveLifetime` with an injected exit, covering the first interrupt, the watchdog
  repeat, the second interrupt, an exit only after the scope ends, and the rollback's own token.
  - A subprocess test, macOS only and opt-in like the service live test: SIGTERM while the manifest fetch stalls ends
    with a `failed`/`cancelled` line, exit 130, a cleared journal and no `bin/`.
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
