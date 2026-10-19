# Local edition setup in the desktop app

Linear AI-3499, milestone "4. macOS launch" of the project "Capacitor local edition".

## Intent

The desktop app is the primary install path. A new user downloads it, picks "For you, locally" on the
edition screen, and ends with a recorded session on a server running on their Mac, without opening a
terminal. The edition screen shipped in PR #1564; its Local choice currently stops on a "not available
yet" notice. This work makes that choice go all the way.

**Done when:** a fresh install of the app on a Mac with no kcap setup goes from the edition screen to
a running local stack, recording agents, the daemon service running, history optionally imported, and
the app's main window connected to the local server.

## Goals

- The local path runs end to end in the existing wizard: Edition, Install, Agents, History, closing frame.
- The app installs the stack by running the bundled CLI's `kcap setup --local --json` and rendering its
  progress lines. It does not reimplement the install.
- Agents (harnesses) are offered for the user to pick, as today.
- The daemon service is installed and started without asking.
- The import is offered; the user can import or skip.
- A closing frame says the user is set up, with **Continue** (into the app) and **Open web dashboard**.
- When the local server is down, the main window offers **Start**.

## Non-goals

- The cloud path. Its sign-up rework is AI-3906; nothing here changes the Cloud or "Already have a
  workspace?" branches beyond what sharing the shell requires.
- The skeleton's ledger shell and overlapped waits (the "First Run Skeleton" kcap page). The user chose
  to keep today's step-by-step wizard and to wait for the install before moving on.
- Mapping unmatched history folders to repositories in the app.
- Upgrading the stack after the app updates itself. `kcap local upgrade` (AI-3494) is the coordinator;
  wiring the app to it is a follow-up change.
- The Ollama embeddings step (AI-3908, in progress in parallel). It adds lines to the same `--json`
  stream; this work must not break on event names it does not know (see Progress).
- Converting between local and cloud in either direction (the Conversion milestone).
- Windows and Linux (their own milestones, AI-3903 and AI-3898).

## Anti-goals

- The app must never report a failed or half-finished install as success, and never offer a route to
  cloud once a local profile has been published, whatever the exit code said.
- The app must not kill the CLI mid-install in a way that skips its rollback, and must not signal it
  twice (a second interrupt makes the CLI exit at once, skipping cleanup).
- No terminal instruction anywhere on the local path ("run kcap …" text in Done is cloud wording).

## Which edition a wizard run is on

The edition is not only the button pressed in this run. A run is **on the local path** when either the
user chose Local in this run, or there is pending local setup, or the resolved profile's server
classifies as `LocalEndpointClass.Local` (the registered active installation, not merely a loopback
or no-auth server). Both of the last two are defined under Durable local state. Everything below that says "local path" uses this one predicate, so a forced `--setup` on a
committed local machine gets local presentation and no route to cloud, and a cloud machine's forced
setup behaves as today.

## Durable local state, and the gate

`kcap setup --local` writes its lifecycle journal before it commits anything, and moves through
`committing`, registry publication, profile publication and the fence lift before clearing it. A run
can stop at any of those points, including before the profile exists (`commit_failed` straight after
the `committing` write). So the app decides whether a local install is in progress from the journal,
not from the profile.

**Pending local setup** means the local root's journal (`LocalStateFiles.ReadJournal`) either holds a
`setup` entry, in any phase, or is `Unreadable` (malformed, inaccessible or a newer schema: the app
cannot prove setup finished). **Committed local** means no pending setup and the resolved profile
classifies as `LocalEndpointClass.Local`.

The gate gains one rule, ahead of its profile checks: pending local setup is **Incomplete**, with a
reason saying a local install is unfinished, whatever the profile says. The wizard then opens on the
local path at Install, which re-runs `kcap setup --local --json`; the CLI's recovery rolls the entry
forward or back, or refuses with its own code (an unreadable journal or registry surfaces as the CLI's
`registry_unreadable` or similar, and the Install page shows that code's affordance). A journal entry
for another operation (an upgrade, an uninstall) leaves the gate as it is; an install started meanwhile
reports `lifecycle_busy`.

While pending local setup stands, the app never builds the normal main graph and never enables the
daemon (see Closing the wizard).

## The flow

### Edition

Choosing **For you, locally** advances to Install instead of showing the notice. The card's detail
line says the local edition is for a Mac one person uses, and roughly how large the download is,
because choosing it answers the CLI's `--single-user` and `--yes` questions (see Install). *My call.*

The Local card stays unavailable, with an explanation in place of today's notice, in two cases. *Both
my calls, from the project's milestones:*

- **Not macOS.** On other platforms the card keeps a "not available on this platform yet" line rather
  than running an untested flow. The CLI would accept glibc Linux, so this gate is the app's.
- **The machine already has a cloud profile**: any configured profile, active or not, has a nonblank
  server that does not classify as `Local`, which is the CLI's own preflight rule
  (`LocalPreflight.CheckProfiles`). The wizard can reopen for one (an expired token, or `--setup`),
  and the CLI would refuse with `other_server`. The card says this Mac is set up for a cloud workspace and that moving it to local
  comes later.

### Install (new step)

A new `WizardStepId.Install`, after Edition. It is shown only on the local path and Sign-in only off it.

**Before starting** it checks that the profile the app resolves is the one the installer will write:
the installer writes the config's literal active profile, while the app honours `KCAP_PROFILE`. If
`KCAP_PROFILE` names a different profile, the step stops with a message naming both and does not run
the install. *My call: refusing is smaller and safer than teaching the installer a second resolution
rule.*

It then starts `kcap setup --local --json --single-user --yes` through the app's CLI facade. Entry
returns as soon as the child is started; the run is owned by an install controller that outlives the
step's page (see Closing the wizard).

The page shows four rows, each with a state, and a progress bar for the download:

| Row | Driven by these lines |
|---|---|
| Downloading | `downloading {url, bytes}`, `download_progress {received, bytes}` |
| Verifying the signature | `verified` |
| Installing | `unpacked` |
| First start | `port_chosen`, `starting`, `ready`, `committing`, `committed` |

- `preflight_passed` names the plan and version; a `recovering`/`recovered` pair shows a line such as
  "Finishing an earlier setup" above the rows.
- `reused {server_url, installed_version}` shows all rows done and "Already installed". A reuse may
  still be followed by `committed`; the outcome is counted once.
- `rolling_back` / `rolled_back` show as "Undoing the partial install".
- A line whose `event` the app does not know is ignored, so new CLI steps (Ollama) do not break the
  page. A line whose `v` is above the one the app knows is still read for `event`, `committed`,
  `reused` and `failed`.

**The settled result.** Progress lines only drive the rows. The step's outcome is decided once, after
the child has exited, on two separate axes.

*Success:* the install is **Installed** when a `committed` or `reused` line was seen, whatever the exit
code (a cancel that arrives after the commit boundary exits 130 after `committed`). It then moves to
Agents by itself. Otherwise it failed, and the two axes below decide the page.

*Navigation lock:* after a failure the app re-reads durable local state. If there is pending local
setup or committed local, the run is **locked to local**: no Back to Edition, and the page says the
install did not finish. Otherwise Back is offered.

*Affordance, from the failure code alone:*

| Group | Codes | The page |
|---|---|---|
| Retryable | `download_failed`, `start_failed`, `verification_failed`, `recovery_failed`, `commit_failed`, `cancelled`, and a non-zero exit with no `failed` line | Says what went wrong in plain words and offers **Try again**, which re-runs the command (the CLI recovers a pending entry first) |
| Another setup running | `lifecycle_busy` | Says another kcap setup, upgrade or uninstall is running on this Mac. No Try again |
| Permanent | every other code (`unsupported_platform`, `musl_unsupported`, `socket_path_too_long`, `unsafe_root`, `registry_unreadable`, `config_unreadable`, `other_server`, `leftover_state`, `bundle_not_published`, `not_our_server`, `answer_required`, `invalid_arguments`) | Shows the CLI's message. No Try again |

So a locked run that hits `lifecycle_busy` or a permanent refusal shows that message with neither
Try again nor Back; the user can only close the app, and the next launch re-evaluates the gate.
*My calls:* `verification_failed` is retryable because a retry re-downloads; `commit_failed` is
retryable because its own message tells the user to run setup again, which is what recovery needs.

`lifecycle_busy` can occur although the app never runs two setups at once: a terminal
`kcap setup --local` or `kcap local uninstall`, a `kcap local upgrade`, or a previous app instance's
child that has not exited yet.

**Advancing.** The shell drops an `AdvanceRequested` raised while a navigation is in flight. The
shell changes so that an advance requested during navigation is queued and applied once when that
navigation finishes; an install that settles immediately (a reuse, a fake in tests) and one that
settles minutes later both advance exactly once without a click.

**After Installed there is no way back to Edition or Install.** Back from Agents stops at Agents, and
Back skips pass-through steps in general. *My call.*

**Cancel.** While the install runs, the page offers a Cancel link and no Back. Cancel interrupts the
CLI once (SIGINT on macOS), then waits for it to exit; its own lifetime handling rolls back before the
commit boundary and writes `failed {code: cancelled}`. Past the boundary the CLI finishes the commit,
and the settled result is Installed. Only if the child has not exited after a grace period does the
app kill the process tree. The process runner's streaming call today always kills the tree on
cancellation; it gains a graceful mode. *Grace period is the builder's call.*

### Closing the wizard during an install

The wizard closes three ways: app **Quit**, the titlebar close, and **Finish later**. Today the last
two hand over to the main graph, which is built even when the re-evaluated gate is Incomplete. On the
local path every close goes through the install controller first:

1. **Settle.** If an install is running, the controller sends its one interrupt (a second request,
   such as Cancel then Quit, reuses the interrupt already sent), waits for the child to exit and
   settles the result.
2. **Quit** then exits the app. No graph is built.
3. **A non-quit close** re-reads durable local state, whether or not a child was running (an install
   that already failed counts too). If there is pending local setup, the normal graph is not built:
   the wizard window stays open on Install showing the settled state, so the user can retry, read the
   message, or quit. Otherwise the close proceeds as today.

Finish later is disabled on the local path until the install has settled Installed. *My call.*

If the app dies outright, the CLI's parent-liveness watchdog rolls back, and the next launch's gate
sees either no pending setup or the outstanding journal entry.

### Agents

Today's Harnesses step: it lists the agents found on the Mac and the user picks. On the local path the
default-visibility control is hidden and the step's visibility is preset to `private` (only this user;
there is no team), so continuing, skipping and "no agents detected" all pass validation and persist
`private` to the profile. *My call.*

### History

Today's History step, unchanged: import or skip. It reaches the server through the CLI with the
committed profile, which needs no bearer.

### Daemon service

Not a step, as today. `DaemonEnabler` runs once the install has settled Installed (never on an early
progress line), the same way it runs today when Sign-in is satisfied, and asks nothing in the normal
case. On a forced setup of a committed local machine it runs on entry, as it does for a satisfied
Sign-in. Its existing behaviour for a name in use or an old service stays.

### Closing frame

Today's Done step, with local wording on the local path: the server runs on this Mac, which agents are
recorded, whether the import ran, the daemon's state. Two actions:

- **Continue**, the primary: closes the wizard; the app opens its main window connected to the local server.
- **Open web dashboard**: opens the local server's URL in the browser (what `kcap local open` opens).
  It does not open by itself. *My guess; the user asked for the action, not for an automatic open.*

No "run kcap …" text on this path.

## Main window: the local server is not running

Decided with the user: when the local server is down, the main window says so and offers **Start**.

- **Applies only** when the resolved server classifies as `LocalEndpointClass.Local`. An unreachable
  cloud server never offers it, and the sign-in dialog is never offered for a `Local` server.
- **Evidence** is the server lane's own: the server connection failing to connect or its health
  request failing. The daemon socket's attach state is a separate signal, so a stopped server with a
  healthy daemon still shows Start.
- **Start** runs the bundled CLI's `kcap local start`, one at a time (a second press while one runs
  does nothing), with a timeout above the CLI's ten-minute readiness budget rather than the facade's
  usual sixty seconds. `kcap local start` writes human text and takes no `--json`; success is exit 0.
- On exit 0 the window reconnects the server connection explicitly instead of waiting for its retry.
  On failure, including `lifecycle_busy` contention, it shows the CLI's output and offers Start again.

## What has to change, and what already works

Already works for a committed local installation (checked in the code):

- `kcap setup --local --json` installs, starts and commits, and points the config's active profile at
  the local server with `AuthProvider.None` (`LocalSetupCommit.PublishProfileAsync`).
- Profile resolution sends no bearer to a local endpoint, and the app's server reads accept
  `NoAuthRequired`, so Harnesses, History, the daemon's held-name lookup and the main window work.

Has to change:

- **Gate.** The pending-local-setup rule above.
- **Startup handoff.** A non-quit wizard close with pending local setup keeps the wizard instead of
  building the main graph.
- **Composition.** `Applicable` is read before the user chooses, so the per-edition steps use
  `PassThrough` keyed on the local-path predicate. Back skips pass-through steps. A forced setup on a
  committed local machine starts at Agents on the local path, with Edition and Install locked.
- **Shell.** Queued advance during navigation; close paths routed through the install controller.
- **The "connected" gate.** The daemon identity, the `EnsureAsync` trigger and Done's dashboard URL are
  keyed on `signIn.Satisfied`. They key on "Sign-in satisfied, or the install settled Installed, or
  the run is on the local path with a committed profile".
- **CLI facade.** A streaming call for `setup --local --json` that parses each stdout line as JSON and
  hands typed events to the controller, with graceful single-interrupt cancel. A call for
  `local start` with its own long timeout. Stderr is kept for diagnostics.
- **Agents.** Preset `private` and hidden visibility on the local path.
- **Done.** Local wording and the dashboard action.
- **Main window.** The local Start state.

## Telemetry

New setup-funnel events, emitted by the app (its CLI children run with telemetry off):
`cli_setup_local_install_started`, `cli_setup_local_install_committed` (plan: fresh, reused, recovered),
`cli_setup_local_install_failed` (code, and whether the run was locked to local), `cli_setup_local_install_retried`. The
existing edition events stay. *My call on the names; they follow the existing `cli_setup_*` pattern
and never change once shipped.*

## Acceptance

Beyond the happy path, tests cover:

- A failure injected at each commit point (right after the `committing` write, after the registry,
  after the profile, before the journal clears): relaunch reopens the wizard at Install on the local
  path with no route to cloud, Try again finishes it, and nothing reads as set up before.
- An unreadable journal beside a published local profile: the gate reads Incomplete and Install shows
  the CLI's refusal.
- A locked run that fails with `lifecycle_busy`, and one with a permanent recovery refusal: the CLI's
  message, no Try again, no Back.
- A non-quit close that settles with pending setup builds no main graph and keeps Install on screen;
  Quit builds none and exits; a close after a settled Installed builds exactly one graph.
- An inactive profile naming a cloud server makes the Local card unavailable.
- Cancel before the commit boundary (rolls back, Try again and Back offered), during a fresh commit and
  during recovery (settles Installed), and `reused` followed by `committed` counted once.
- Titlebar close and Quit during the install and during rollback; Cancel then Quit sends one interrupt.
- An install that settles immediately and one that settles late each advance to Agents exactly once.
- `KCAP_PROFILE` naming a profile other than the active one: the install refuses before starting.
- Agents on the local path: continue with agents, skip, and none detected, with visibility hidden.
- Forced `--setup` on a committed local machine: starts at Agents, local presentation, no route to
  cloud. A cloud machine's forced setup is unchanged.
- Main window: a stopped server with a connected daemon offers Start; a start taking over sixty
  seconds succeeds and reconnects; a failed and a contended start show the CLI's message; an
  unreachable cloud server never offers Start.

## Server follow-up (kcap-server, separate issue)

The local dashboard must not prompt the user to import history when there are no sessions and nothing
was imported, because the app's setup already offered the import. The app already reports setup to
`/api/users/me/cli-setup`, which clears the dashboard's "run kcap setup" welcome; the follow-up decides
how the dashboard learns the import was offered (for example a field on that report). Out of this
change.

## Left to the builder

- Layout and copy of the Install page, its failure states and the main window's Start state, within
  the Kcap theme rules in `CLAUDE.md` (status colours for status only, Lucide icons).
- The graceful-cancel grace period and how the runner signals the child.
- How download progress is smoothed and labelled.
- Whether the closing frame is a variant of `DoneStepViewModel` or a sibling step type, and where the
  install controller lives.
