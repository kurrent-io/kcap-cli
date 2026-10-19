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
- Windows and Linux. The CLI refuses unsupported platforms with a stable code; the app shows it.

## Anti-goals

- The app must not report a failed or half-finished install as success. A step shows done only after
  the CLI's `committed` (or `reused`) line and a zero exit.
- The app must not kill the CLI mid-install in a way that skips its rollback.
- No terminal instruction anywhere on the local path ("run kcap …" text in Done is cloud wording).

## The flow

### Edition

Choosing **For you, locally** advances to Install instead of showing the notice. The card's detail
line says the local edition is for a Mac one person uses, because choosing it answers the CLI's
`--single-user` question (see Install). *My call.*

The Local card stays unavailable, with an explanation in place of today's notice, in two cases. *Both
my calls, from the project's milestones:*

- **Not macOS.** Linux and Windows get the app's local path in their own milestones (AI-3898, AI-3903),
  so on those platforms the card keeps a "not available on this platform yet" line rather than running
  an untested flow. The CLI would accept glibc Linux, so this gate is the app's.
- **The machine already has a cloud profile.** The wizard can reopen for one (an expired token, or
  `--setup`), and the CLI would refuse with `other_server`: converting a cloud machine is the Conversion
  milestone. The card says this Mac is set up for a cloud workspace and that moving it to local comes
  later, instead of letting the user run into a permanent failure.

### Install (new step)

A new `WizardStepId.Install`, placed after Edition. It is shown only for the Local choice, and Sign-in
only for the other two.

On entry it starts `kcap setup --local --json --single-user --yes` through the app's CLI facade.
`--single-user` acknowledges that shared machines are unsafe for now (the Local card says so);
`--yes` confirms the download, whose approximate size the Local card states as static copy (the
CLI reports the real size only after consent). Both are answered by the user's choice of
the Local card, not by a further prompt. *My call; the CLI needs both or fails with `answer_required`.*

The page shows four rows, each with a state, and a progress bar for the download:

| Row | Driven by these lines |
|---|---|
| Downloading | `downloading {url, bytes}`, `download_progress {received, bytes}` |
| Verifying the signature | `verified` |
| Installing | `unpacked` |
| First start | `port_chosen`, `starting`, `ready`, `committing`, `committed` |

- `preflight_passed` names the plan and version; a `recovering`/`recovered` pair shows a line such as
  "Finishing an earlier setup" above the rows.
- `reused {server_url, installed_version}` means a committed installation already exists: all rows show
  done and the step reads "Already installed".
- `rolling_back` / `rolled_back` show as "Undoing the partial install" before the failure state.
- A line whose `event` the app does not know is ignored, not treated as an error, so new CLI steps
  (Ollama) do not break the page. The line carries `v`; a `v` above the one the app knows is still
  read for `event`, `committed` and `failed`.

On `committed` (or `reused`) with exit 0 the step is satisfied, records the `server_url`, and moves on
to Agents by itself. *My call: one primary action per screen, and there is nothing to decide here.*

**After the commit there is no way back to Edition.** The profile now names the local server and the
stack runs; switching to cloud would be a conversion, which v1 does not do. Back from Agents stops at
Agents (Install and Edition are locked), and a re-entered Install shows the static "Installed" state
without re-running the CLI. *My call.* A machine that quits right after the commit reopens into the
main window, as a cloud machine does after Sign-in, because the gate reads Complete.

**Cancel.** While the install runs, the page offers a Cancel link and no Back. Cancel interrupts the
CLI gracefully (SIGINT on macOS), so its own lifetime handling rolls back and writes a last
`failed {code: cancelled}` line before exiting 130. Only if the child has not exited after a grace
period does the app kill the process tree. The process runner's streaming call today always kills the
tree on cancellation; it needs a graceful mode. *Grace period is the builder's call.*

**Quit mid-install.** The app signals the child as for Cancel and does not kill its tree; failing
that, the CLI's parent-liveness watchdog sees the app gone and rolls back. On the next
launch the onboarding gate still reads Incomplete (no committed profile), so the wizard opens again; the
CLI's journal recovery finishes or undoes the earlier attempt on the next run (`recovering` lines).

**Failures.** The `failed {code, message}` line decides what the page offers:

| Group | Codes | The page |
|---|---|---|
| Transient | `download_failed`, `start_failed`, `verification_failed`, `recovery_failed`, `cancelled` | Says what went wrong in plain words, offers **Try again** (re-runs the same command) and Back |
| Another setup running | `lifecycle_busy` | Says another kcap setup or upgrade is running on this Mac, offers Back only |
| Permanent | every other code (`unsupported_platform`, `musl_unsupported`, `socket_path_too_long`, `unsafe_root`, `registry_unreadable`, `config_unreadable`, `other_server`, `leftover_state`, `bundle_not_published`, `not_our_server`, `commit_failed`, `answer_required`, `invalid_arguments`) | Shows the CLI's message, offers Back only |

A non-zero exit with no `failed` line is treated as transient with a generic message. *My call:*
`verification_failed` sits with the transient group because a retry re-downloads; a second failure in a
row reads the same and stays retryable.

`lifecycle_busy` can occur although the app never runs two setups: a terminal `kcap setup --local` or
`kcap local uninstall`, a `kcap local upgrade` (AI-3494), or a previous app instance's child that has
not exited yet.

### Agents

Today's Harnesses step, unchanged in what it offers: it lists the agents found on the Mac and the user
picks. On the local path the default-visibility control is hidden, because there is no team to share
with. *My call.* It already writes to the resolved profile, which the install committed.

### History

Today's History step, unchanged: import or skip. It reaches the server through the CLI with the
committed profile, which needs no bearer.

### Daemon service

Not a step, as today. `DaemonEnabler` runs as soon as the install commits, the same way it runs today
when Sign-in is satisfied, and asks nothing in the normal case. Its existing behaviour for a name in
use or an old service stays.

### Closing frame

Today's Done step, with local wording on the local path: the server runs on this Mac, which agents are
recorded, whether the import ran, the daemon's state. Two actions:

- **Continue**, the primary: closes the wizard; the app opens its main window connected to the local server.
- **Open web dashboard**: opens the local server's URL in the browser (what `kcap local open` opens).
  It does not open by itself. *My guess; the user asked for the action, not for an automatic open.*

No "run kcap …" text on this path.

## What has to change, and what already works

Already works for a committed local installation (checked in the code):

- `kcap setup --local --json` installs, starts and commits, and points the active profile at the local
  server with `AuthProvider.None` (`LocalSetupCommit.PublishProfileAsync`).
- `OnboardingGate` treats that profile as complete, so the wizard does not reopen afterwards.
- Profile resolution sends no bearer to a local endpoint, and the app's server reads accept
  `NoAuthRequired`, so Harnesses, History, the daemon's held-name lookup and the main window work.

Has to change:

- **Composition.** `Applicable` is read before the user chooses, so the per-edition steps use
  `PassThrough`: Install passes through unless the choice is Local; Sign-in passes through when it is.
  Back must skip pass-through steps too, or Back from Agents lands on a skipped Sign-in.
- **The "connected" gate.** The daemon identity, the `EnsureAsync` trigger and Done's dashboard URL are
  keyed on `signIn.Satisfied`. They key on "Sign-in satisfied or the install committed" instead.
- **CLI facade.** A streaming call for `setup --local --json` that parses each stdout line as JSON and
  hands typed events to the step, with the graceful cancel described above. Stderr is kept for
  diagnostics, not shown.
- **Done.** Local wording and the dashboard action.
- **Main window.** For a local server its sign-in request path offers no sign-in dialog. When the local
  server is not reachable (after a reboot before the service is back, or `kcap local stop`), the window
  says the local server isn't running and offers **Start**, which runs the bundled CLI's
  `kcap local start` and reconnects when it reports ready. A failed start shows the CLI's message.
  *Decided with the user.*

## Telemetry

New setup-funnel events, emitted by the app (its CLI children run with telemetry off):
`cli_setup_local_install_started`, `cli_setup_local_install_committed` (plan: fresh, reused),
`cli_setup_local_install_failed` (code), `cli_setup_local_install_retried`. The existing edition events
stay. *My call on the names; they follow the existing `cli_setup_*` pattern and never change once
shipped.*

## Server follow-up (kcap-server, separate issue)

The local dashboard must not prompt the user to import history when there are no sessions and nothing
was imported, because the app's setup already offered the import. The app already reports setup to
`/api/users/me/cli-setup`, which clears the dashboard's "run kcap setup" welcome; the follow-up decides
how the dashboard learns the import was offered (for example a field on that report). Out of this
change.

## Left to the builder

- Layout and copy of the Install page and the failure states, within the Kcap theme rules in
  `CLAUDE.md` (status colours for status only, Lucide icons).
- The graceful-cancel grace period and how the runner signals the child.
- How download progress is smoothed and labelled.
- Whether the closing frame is a variant of `DoneStepViewModel` or a sibling step type.
