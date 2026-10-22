# Uninstall kcap from the macOS app

## Intent

Dragging kcap.app to the Trash runs nothing, so it leaves kcap's background pieces behind: the
daemon's LaunchAgent retries a binary that no longer exists, the local server keeps running, the
`kcap` link on PATH dangles and every harness hook then fails with "command not found", and the
hooks, skills, MCP entries and `~/.config/kcap` stay. macOS gives an app no uninstall hook, so the
app offers the conventional Mac answer: an **Uninstall kcap…** item in the app menu that removes
everything and then moves the app to the Trash.

"Everything" is meant literally. If a local server is installed, its recorded sessions are deleted
too, and the confirmation says so before anything is removed.

## Goals

- One menu action that leaves no kcap trace on the machine: harness integrations, every daemon
  service, the local server (service, binaries, data, logs), the `kcap` link on PATH, `~/.config/kcap`
  and the app's own support files, then the app itself.
- A clear warning, in the confirmation, when a local server is installed: its sessions are
  permanently deleted.
- `kcap uninstall` and the menu item behave the same, because the menu item runs `kcap uninstall`.
  Fixing the command fixes the CLI-only user too.

## Non-goals

- Windows and Linux menu items. Windows cleans up through its Velopack uninstall hook; Linux is
  #1782.
- Cleaning up after a plain drag to the Trash (a self-retiring daemon). A daemon cannot reliably tell
  "trashed" from "moved" or "being updated", and a false positive is worse than the leftovers.
- Deleting anything on a cloud server. Sessions already sent to a hosted kcap server stay there.

## Anti-goals

- The app moving itself to the Trash while kcap pieces remain. A failure keeps the app installed.
- Deleting local-server data without the warning having been shown and accepted.
- Ending a running hosted agent without the confirmation having said so.

## Current state (facts the design rests on)

- `kcap uninstall` (`UninstallCommand.cs`) already removes every daemon service: its first step,
  `OutgoingRecorders.StopAsync`, uninstalls each id from `ListInstalled()` and then stops daemons and
  watchers. It has no busy check and does not end agents gracefully (no
  `EndAgentsBeforeRemovalAsync`).
- It never touches the local server, yet `LocalRoot` defaults to `~/.config/kcap/local`, so its final
  recursive delete of the config directory removes the server's data while the
  `io.kurrent.kcap.local` LaunchAgent stays installed and running. This is a bug today.
- It never touches the PATH link. `PathShimInstaller` has no remove method. The link may be in
  `/usr/local/bin` (often created with admin rights via osascript), `/opt/homebrew/bin`,
  `~/.local/bin` or `~/bin`. `ApplicationReferences.PrepareAsync` already scans those four folders
  for a `kcap` symlink resolving into the bundle, and `ReplaceLinkAsync` already falls back to
  osascript admin on `UnauthorizedAccessException`.
- `kcap local uninstall --purge` does the full local removal (busy refusal unless `--force`, stop and
  remove the unit, delete `bin/`, `run/`, `data/`, `logs/`, pending recordings and caches, clear
  profiles pointing at it, retire the port). Not installed: exit 0.

## Design

### 1. `kcap uninstall` (all platforms)

- **Busy refusal up front.** Before changing anything, probe live daemons the way
  `kcap local uninstall` does (`OutgoingRecorders.BusyAsync`). A daemon running agents, or one that
  cannot report, refuses with a message naming `--force`, and nothing is changed. New flag
  `--force` ends those agents. When removing daemon services, end agents through the same path
  `daemon service uninstall` uses (`EndAgentsBeforeRemovalAsync`) rather than the bare SIGTERM of
  today.
- **Local server.** When a local installation exists, uninstall removes it before anything else in
  the config directory, with the purge semantics of `kcap local uninstall --purge`, under its
  lifecycle lease. Reuse that code; do not restate it. The interactive confirmation lists it as a
  separate line that says the data is deleted and names the URL. With `--keep-config`, the server is
  removed without purge (data kept, as `kcap local uninstall` does), consistent with "I plan to
  reinstall" — my call. A failure here counts as a failure, so the config directory is kept, as every
  other failed step already does.
  `KCAP_LOCAL_ROOT` pointing outside the config directory is handled the same way, since the purge
  works on the resolved root.
  **`--yes` is not consent to delete local data** (my call): when a local installation exists and
  the run is non-interactive, uninstall refuses unless `--purge-local` is also given (or
  `--keep-config`, which keeps the data). The interactive prompt is consent, since it names the
  deletion. The app passes `--purge-local` only after its warning was accepted.
- **PATH link.** Remove each `kcap` symlink in the four folders whose target resolves to the binary
  running the uninstall (or into the same install). Never remove a regular file or a link pointing at
  another kcap. A link that cannot be removed for permissions is reported with the exact
  `sudo rm <path>` to run, and makes the run end "with errors". The app handles that case itself
  (section 2), so it passes `--keep-path-links`, a hidden flag (not in help or README) that skips
  this step.
- README `### Uninstalling` and the help text gain `--force`, the local server line and the link
  removal, in the same PR.

### 2. The app menu item (macOS only)

- `AppMenu` gains **Uninstall kcap…**, after "Set up kcap…" with a separator, built only on macOS.
  Enabled once the app has finished starting (same pattern as `SetSetupAction`).
- **Confirmation** — a destructive dialog in the existing style (`BuildConfirmForceStopWindow`:
  `kcapChip` Cancel, `kcapDangerFill` accept, closing = cancel). Content:
  - What gets removed, in plain words.
  - When a local server is installed (`LocalEndpoints.Read(localRoot)` has an active install): a
    warning, visually distinct, that every session recorded on the local server is permanently
    deleted.
  - When the daemon reports running agents: how many will be ended. When a daemon is running but
    not answering, say so and that anything it runs will be ended. Every other daemon whose pid is
    live is named too, with the same line, since `--force` stops them all; when the daemons
    directory cannot be read, say that anything a daemon runs will be ended. Either way accepting
    passes `--force` (my call), so Retry never fails the same refusal again.
  - When the profile points at a hosted server: one line that sessions already sent there stay there.
  - **Another kcap install** (decided: remove everything regardless). The hooks, daemon services and
    config are shared, so the uninstall removes them for every install, unlike the Windows hook,
    which scopes itself to the app's own services. When a `kcap` outside this bundle is found (on
    the terminal PATH via the login-shell probe, or in a known install location such as npm's global
    prefix, Homebrew or the script installer), the confirmation names it and says it will stop
    working, with the command that removes it. When the terminal PATH cannot be read in time, one
    line says any kcap installed outside the app would also stop working. The uninstall neither
    removes nor protects it.
  - Accept label: "Uninstall".
- **Run in a fresh copy of the app (my call).** Accepting quits the app through its normal shutdown
  and relaunches it as `--uninstall` (plus `--purge-local` when the warning was shown), the way
  "Set up kcap…" relaunches as `--setup`. The uninstall copy builds nothing of the normal app — no
  daemon client, no mutation lane, no lifecycle controller, no update check, no app-state writes —
  and shows only an uninstall window. That is what keeps the app from restarting or reinstalling what
  is being removed, and from recreating files under `~/.config/kcap`, without adding an admission
  gate to the lane. The window runs the bundled CLI's
  `kcap uninstall --yes --force --keep-path-links [--purge-local]` streamed and shows its lines.
- **After the CLI exits 0:** remove the `kcap` links into this bundle (reuse the
  `ApplicationReferences` scan and its osascript-admin fallback, so a root-owned `/usr/local/bin`
  link costs one password prompt), delete the app's own support files (preferences, caches, logs,
  Velopack's downloads — builder enumerates them), move the bundle to the Trash with the system API
  (it stays restorable), and quit.
- **On failure** (non-zero exit, or a link that could not be removed): the app stays installed and
  shows the CLI's error lines and what is left, with Retry and Close. It never trashes itself
  half-cleaned. Cancelling the admin prompt counts as a failure of that step only. Retry runs the
  uninstall again in the same copy; Close quits, and the next launch starts from whatever is left like
  any other start.
- **When the bundle cannot be moved to the Trash** (e.g. owned by another admin user): reveal the
  bundle in Finder with "drag kcap to the Trash to finish", then quit (my call). Everything else is
  already gone, so this is not a Retry state.

## Left to the builder

- Exact copy, layout of the confirmation and progress views.
- The list of the app's own support paths, and the trash API (`NSFileManager trashItemAtURL` via the
  existing AppKit interop is the expected choice).
- Test split: CLI unit tests for the busy refusal, the local purge running before the config delete,
  `--keep-config` keeping local data, and link removal touching only links that resolve to this
  install; a ViewModel test for what the confirmation shows in each state.
