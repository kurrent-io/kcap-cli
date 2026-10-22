# Vendor accounts: choosing one when a session starts

The second spec on vendor accounts. The first, `2026-10-07-vendor-accounts-and-limits-design.md`,
defined an account as one vendor plus one config directory on one host, put the registry beside the
daemons directory, and wired every registered account so its sessions record (Part A, shipped). Its
Part B, the usage readings and the screen that shows them, is designed there and not repeated here.
This spec adds what the first one left out: picking an account when a hosted session starts, a
per-agent config home in the daemon, remembering the pick per repository, managing accounts from the
desktop app's Settings window, and where the readings show once Part B exists.

Mockups: https://kurrent.kcap.ai/pages/87dd7f8b111e40ec9df4ce7f78980acb

## 1. Problem

A person with two Claude or Codex subscriptions on one machine can record sessions from both, but
every hosted session the daemon starts runs under the one directory the daemon inherited from its
environment. There is no way to say "this repository runs on the work account", and no way to see,
before starting, which account has room.

## 2. Decisions

| Decision | Choice |
|---|---|
| When | An account is chosen only when a session starts. Switching a running session is a follow-up. |
| Where in the launcher | Inside the Agent chip's panel, above the model rows, shown only when the vendor has two or more rows on the target machine. No new chip. |
| Default | Per vendor and per daemon: the directory the daemon's own environment points at. Derived, never written to the registry; published by each daemon as its own fact, apart from the host's registry. |
| Resolved account | The daemon resolves every launch to a directory before the process starts and keeps that, not the request, on the agent. |
| Contract | One optional account id appended to the launch request on each hop, and to the local Spawn frame for `kcap agent start --account`. Null means the default. An explicit id is honoured or refused at every hop; no hop and no screen substitutes the default for it. |
| Support gate | A daemon advertises account launches; the server reports that per listed daemon and refuses an explicit id for a daemon without it; the app offers, restores and sends an explicit selection only for a target the server reports as supporting them, and holds Start while that support or the target's snapshot is unknown. |
| Memory | Per repository, vendor and host, read only from a complete snapshot. No memory means the default, never what another repository used. |
| Managing accounts | A tab in the Settings window, this machine only. Remote machines are read-only in the launcher. |
| Account actions | The app runs `kcap accounts` through its mutation lane; the daemon only watches and publishes. Every verb commits a registry revision after its wiring attempt. |
| Readings in a session | A strip under the chat composer, always shown for a Claude or Codex session whose account is known, read-only. |
| Reviewers | Review flows and PR reviews keep the vendor's default account. |
| Vocabulary | "Account" everywhere; "profile" stays a kcap server sign-in. |

## 3. The default account

The daemon resolves its vendor layouts once from its environment (`HarnessRegistry.FromEnvironment`
in `DaemonRunner`). That directory is the vendor's **default account** for this daemon: what a launch
with no account id resolves to. It is derived, never written: the daemon registers nothing, because
a registered directory gets wired at the next refresh (`plugin --if-installed` wires every
registered account), and the first spec records no directory nobody listed. Two daemons on one host
with different environments share the registry and each advertises its own default; the launcher
targets a daemon, so "default" always means the targeted daemon's, and the default travels as the
daemon's own fact, never inside the host's registry snapshot (Section 5).

When the default directory is registered, that account carries the Default badge. When it is not,
which happens when the daemon's environment differs from the shell `kcap setup` ran in, both the
launcher panel and the Settings tab show it as a row of its own, labelled by directory name, with
the Default badge, the recording state **Not wired**, and Add as its one action. What a launch
under it does follows from the wiring that exists, not from registry membership:

- Codex: both transports keep their preflights. The launcher's `Prepare` refuses to start when
  neither the account directory nor the repository carries the kcap hooks, with the existing error,
  whose remedy names adding the account, and the app-server runtime additionally validates the
  hooks it lists after spawning. With repository hooks both transports start and record: the PTY
  session through the hooks, the app-server session through the runtime's envelope transcript once
  it has passed those checks.
- Claude: the launch starts; the session records only if the repository wires the kcap hooks.

Adding the row registers and wires the directory, and the row becomes an ordinary account.
Removing a registered default turns it back into this row. The registry is otherwise as Part A left
it. Labels default to the directory name; Part B's identity probe replaces them with the email.

## 4. Launching under an account

### 4.1 The request and its gates

`account_id` is appended as an optional field, null meaning the default, on each hop:

- App: `LaunchRequest` and `LaunchAgentRequestV2Payload` in `Services/ILaunchClient.cs`, and
  `LaunchDraft`, so the id is captured with the rest of the draft before the upload starts.
- Server: `RemoteLaunchRequest` and `DaemonLaunchCommand`, passed through untouched.
- Daemon: `LaunchAgentCommand` in `Capacitor.Cli.Core/Models.cs`, appended last under the same
  positional wire-compatibility rule as the fields before it.

An older hop drops the field silently: an old server never forwards it, and an old daemon ignores a
trailing field. Both would turn an explicit choice into a default launch, so support is gated at
every hop rather than inferred from merge order:

- The daemon advertises `accounts/1` on `DaemonConnect`, beside its other advertised capabilities,
  and on the local socket's capability list.
- The server keeps it on the listed daemon (`DaemonInfo`), and its launch capability policy, the one
  that already refuses a Codex posture a daemon did not advertise, refuses an explicit `account_id`
  for a daemon without `accounts/1`, with a named reason. It never forwards the id for the daemon to
  ignore.
- The app offers account rows for a machine, local or remote, only when the listed daemon the
  server reports carries `accounts/1`. An old server reports no such flag, so the rows stay absent
  and every launch goes to the default by the person's own, visible, choice. The local socket's flag
  is not enough on its own: local launches also travel through the server.

The server does not validate the id beyond that gate. The daemon does: it reads the registry under
no lock (`accounts.json` is replaced atomically) and refuses the launch with a named reason,
`account_unavailable`, when the id is not registered, is registered for another vendor, or names a
directory that no longer exists. A refusal is reported like any other launch failure and is never
collapsed into a generic rejection. The daemon never substitutes the default for an id it cannot
honour: the person chose the account to spend, and a silent swap spends the wrong one.

A launch with no id, including every review-flow and PR-review launch, resolves to the default.

`kcap agent start` takes `--account <id-prefix | directory>`, resolved the way `kcap accounts`
resolves its arguments: a unique id prefix or a registered directory. A directory that is not
registered is refused by the CLI with the `kcap accounts add` remedy; nothing registers it. The
resolved id is appended to the local Spawn frame's payload after the title, under that frame's
trailing-bytes rule, and the daemon applies the same resolution and refusal as for a server launch.
An older daemon would ignore the trailing bytes and spend the default, so the CLI sends the flag
only to a daemon whose local capability list carries `accounts/1` and refuses otherwise. The flag
combines with `--private`; the resolved account is kept on the agent as in 4.2 either way.

### 4.2 Per-launch layouts in the daemon

Before the process starts, the daemon resolves the launch to a **resolved account**: the directory,
the registry id when the directory is registered, and the label (the registered label, else the
directory name). Everything below keys on that, never on the request's id.

`ClaudeLauncher` and `CodexLauncher` each hold one `ClaudePaths` / `CodexPaths` taken from the
harness registry, and every write and read they make is keyed on it: workspace trust in `.claude.json`,
the MCP config, the project symlink, tool permissions, the Codex MCP inventory and `config.toml`
reads. Both launchers take the layout per launch instead, built with `AccountLayouts.Claude` /
`AccountLayouts.Codex` from the resolved directory. The same applies to the Codex app-server runtime
and to anything else that starts a vendor process for an agent.

The child environment is an operation on the inherited one, not an overlay: the PTY and app-server
environment builders (`UnixPtyProcess.BuildChildEnv`, `ConPtyProcess.BuildEnvironmentBlock`, the
Codex factory's child-environment step) inherit the daemon's variables and then **remove**
`CLAUDE_CONFIG_DIR` / `CODEX_HOME` when the resolved directory is the vendor's built-in location
(`~/.claude`, `~/.codex`), and **set** it to the resolved directory in every other case, the daemon's
own custom default included. The daemon's process environment is never changed. Removing, rather than
leaving unset, is what keeps a daemon started with `CLAUDE_CONFIG_DIR=~/.claude-work` from handing
that directory to a launch resolved to `~/.claude`; the built-in location stays unset because naming
it as the override moves `.claude.json` away from where Claude reads it, the rule `AccountLayouts`
already encodes as a null override.

Hooks and the transcript watcher already attribute a session to its account by path (Part A), and
`CLAUDE_CODE_SUBPROCESS_ENV_SCRUB` may strip the variable from hook environments; nothing here relies
on the variable reaching a hook.

### 4.3 The agent keeps its resolved account

`ResumeLaunchDescriptor` in the resume ledger gains the resolved account: directory, registry id or
null, label. A resume after a daemon restart relaunches under that directory, not under whatever the
restarted daemon's default is now:

- A registered id that is no longer registered, or whose directory is gone, fails the resume with
  `account_unavailable`; the entry is marked interrupted. The reason survives the native-resume
  path: it is not collapsed into `launch_rejected`, and the continuation fallback that follows a
  failed native resume is not attempted for it, since a continuation would spend another account.
- An unregistered default (null id) resumes under its stored directory while that directory exists,
  and fails the same way when it does not. It is never registered by the resume.
- A ledger entry written before this field exists resumes under the current default, as today.

`AgentStatusDto` in `LocalIpc/StatusIpc.cs` gains the resolved account's directory, id and label,
appended. The daemon's agent registration and status report to the server carry the same three
fields, so the server's agent row shows the account for a remote session and the composer strip can
name a directory even for an unregistered default. All optional; an older daemon reports none and
the app shows nothing for that session.

### 4.4 The Codex model catalog is per account

`CodexHostedAgentRuntimeFactory.ProbeModelsAsync` probes once per daemon with an empty environment
and fingerprints the default home's `models_cache` and `auth.json`. With accounts, it runs once per
registered Codex account, and once for an unregistered default, with `CODEX_HOME` set to that
directory and that account's files in the fingerprint set. Per-machine catalogs already travel on
`DaemonConnect.VendorModels` to the server's listed daemon and on `DaemonStatusDto.VendorModels` to
the local app, and `HomeViewModel.ModelChoicesFor` prefers the machine's list; a per-account map
keyed by registry id is appended beside `VendorModels` on both wires, with the default's catalog
staying in `VendorModels` for older readers. The launcher reads the selected account's catalog,
falling back to the machine's vendor catalog, then to the curated list, so a launch is never blocked
on this.

The catalog follows the registry while the daemon runs. The registry watcher of Section 5.1 drives
it: an added account is probed and its files join the watched set; a removed account's catalog is
dropped and a probe still in flight for it is discarded on completion, matched by id; an
unregistered default that becomes registered moves from the default catalog to a map entry while
`VendorModels` stays as it was. A change to the account map alone, with `VendorModels` unchanged, is
published to the server and to the local app; the re-advertisement check compares both.

## 5. Listing a machine's accounts

### 5.1 This machine

Two of the local-socket frames of the first spec, Section 6.3, are built: `AccountsSubscribe` and
`AccountsSnapshot`, appended to `FrameType` and advertised in `LocalControlCapabilities.Current` beside
their handlers. The daemon watches `accounts.json` as that spec's Section 6.1 describes and pushes a
snapshot on change. The snapshot has two parts:

- The **host registry**: host id (from `host.json`), registry revision, and the public account view
  per account: id, vendor, directory, label, recording state. Part B appends identity and the
  reading.
- The **daemon's defaults**: the default directory per vendor, with its **directory key**, and
  nothing else. This is the daemon's own fact, carries no revision, and changes only when the daemon
  starts with another environment. Whether that directory is registered, and under which id, is
  derived by every reader from the accepted host registry, never carried in this part: an id here
  would outlive a removal or re-add that the registry part records under a revision the reader may
  not have accepted yet.

A **directory key** is computed only on the owning host, by the publishing daemon, for every
registry entry and every default: the directory as `AccountDirectory.Normalize` resolves it there
(symlinks followed, the host's physical path), case-folded when that host compares paths
case-insensitively and left as is on Linux. Readers, the local app included, match a default to an
account by ordinal equality of keys and never resolve or case-fold a remote path themselves:
`AccountDirectory.Same` resolves against the reader's own filesystem with the reader's own case
rule, so a macOS app would merge two distinct Linux directories differing only in case, and no
reader can follow a symlink on another host. Two daemons on one host compute equal keys for one
directory because they share its filesystem.

A snapshot is **complete** when it carries both parts; the app acts on nothing less.
`AccountAction` and `AccountActionResult` are not built; Section 7 says why.

### 5.2 A remote machine

The daemon publishes the same two parts, without readings, to a server that advertises the
`accounts` capability: `DaemonConnect.Accounts`, `AccountsChanged` with the 30-second trailing
throttle, and the revision gate of the first spec's Sections 6.1 and 6.2 for the host registry. The
server stores the registry per host behind that gate, and the defaults per daemon with no gate: the
daemon's latest publication wins, and every connect republishes them, so a daemon restarted with
another environment updates its defaults without touching the host's revision. Two daemons on one
host publishing equal revisions in either order leave one registry and two sets of defaults. The
server serves both to the host's owner only, and a reader matches the targeted daemon's default
key against its host's accepted registry to find the default's row and its catalog; a rejected
older registry part leaves that daemon's defaults accepted, which is safe because they name only
directories. Part B adds readings to a snapshot that is already flowing. Until the
server part lands, the panel shows no account rows for a remote machine and the launch goes to its
default.

## 6. The launcher

### 6.1 Account rows in the Agent panel

`LauncherPaneView.OnAgentChipClick` builds the panel: a vendor rail and searchable model rows. When
the selected vendor has two or more rows on the selected machine, an **Account** section goes above
the model rows, under the search field, with one row per registered account of that vendor and, when
the targeted daemon's default is unregistered, the row of Section 3: label, directory in faint text,
a neutral Default badge on the targeted daemon's default, the recording state where it is not
Recording, and the selection check the model rows use. Once Part B exists each row also carries one
bar per window with its percentage and the next reset. Picking a row sets the account and rebuilds
the model rows for it. The search field does not match accounts.

With one row the section is absent and the panel is unchanged, except while the selection is
invalid (6.3): then the section stays, showing the invalid choice marked as removed beside the rows
that exist, so the Default row is always reachable. The account Default row is distinct from the
model list's "Default — Claude chooses" row, which picks a model, not an account. For an
unsupported target (6.3) the section is absent; while the selection is pending it shows one line,
"Loading accounts…", in place of rows, with a Retry action once a fetch has failed.

### 6.2 The chip

The chip label stays "Claude · Opus 4.1" while the chosen account is the default and becomes
"Claude · Opus 4.1 · work" otherwise. The label is identity, so it takes no status colour.

### 6.3 Selection, the draft and Start

**Support knowledge** for a target is one of three values, held per target and per server
connection: *unknown* until the current server's daemon listing has answered for that daemon,
*unsupported* when an authoritative listing shows the daemon without `accounts/1`, and *supported*
when it shows the flag. Unknown is the state before the first listing, after a failed fetch, and
after any reconnect or profile switch until the listing answers again; it is never read as
unsupported. The listing service refreshes only on connection changes and daemon-change
notifications today, so a transient failure on a healthy connection would leave support unknown for
good: while support is unknown for the selected target, the app retries the listing fetch with a
bounded backoff, starting at a few seconds and capped at half a minute, and the pending line offers
**Retry**, which fetches at once. Retries and their results are scoped to the current server
connection and target; a result from a superseded connection is rejected. A failure never becomes
unsupported and never selects the default. The flag is read from the server's listing for local and remote targets alike: the
local daemon's readiness and its socket snapshot never stand in for it, since the launch travels
through the server, and the daemon list is fetched after the connection and apart from local
readiness. A target is **account-capable** when it is supported and a complete snapshot (5.1) for
its host is at hand.

`HomeViewModel` keeps the selected account per vendor beside `SelectedModel`, in one of four
states: *default* (no explicit choice), *explicit, valid* (the chosen id is in the latest complete
snapshot and the target is supported), *explicit, invalid* (chosen, but absent from the snapshot or
the target is not currently supported), and *pending* (no choice made on this target and no restore
has resolved yet). It is set from two sources only: a row the person picks, and the restore of
Section 6.4.

**Entering a target**, when the repository, vendor or machine changes: unknown support, or supported
without a complete snapshot, gives pending; supported with a complete snapshot runs the restore and
gives default or explicit; unsupported gives default, with memory entries kept but not restored.
Pending ends only when support and the snapshot resolve and the restore has run. There is no
timeout that falls through to the default, because that would be the substitution this spec
forbids, and a daemon that advertises `accounts/1` publishes its snapshot on every connect.

**While on a target**, a choice the person made stays selected through everything: support
withdrawn, support unknown again after a reconnect, the snapshot lost or the account gone all turn
it explicit-invalid, never default, and it returns to valid when the evidence returns and lists the
account. The chip shows the invalid choice marked as removed or unavailable. Only the person's own
pick of the Default row, or the Use default action below, makes it default. A default selection
stays default through the same events.

**Start** is enabled only for a resolved selection: explicit-valid, or default established by a
resolved state (a restore that found no entry, an authoritative unsupported listing, or the
person's own pick). Pending disables Start with its reason, and no draft is captured. An
explicit-invalid selection refuses with a named message in the launcher and sends nothing; the
message offers **Use default**, which sets the selection to the default without launching, and the
person presses Start again. Start captures the resolved selection into the draft, null for the
default; a snapshot or listing that arrives while that launch is in flight changes nothing about
it. A captured explicit draft is refused the same way, the draft kept and never cleared to null,
when the attachment upload completes or a reconnect precedes the send and, at that moment, the
account is absent from the latest complete snapshot or the target is no longer supported. A
restore from memory may fall back to the default silently (6.4); an account the person chose may
not.

### 6.4 Memory per repository

`AppState` gains `AccountByRepo`, a map keyed by repository path, vendor and **host id** to a
registry id, beside `HarnessByRepo` and with its reserved scratch key. The host id comes from the
complete snapshot of the targeted machine, so two daemons on one host share one memory, a daemon
rename changes nothing, and two servers listing daemons of the same name never collide, since
`AppState` is shared across server profiles.

- Picking a row writes the entry; picking the Default row removes it, so a later change of the
  daemon's default is followed.
- The restore runs only for an account-capable target (6.3): it looks up the entry for the
  repository, vendor and the snapshot's host id, and selects that account when the snapshot lists
  it.
- An entry whose id is absent from a complete snapshot of its own host is dropped and the default
  is restored. An entry is never dropped because the snapshot is missing, stale, from another host,
  or because the machine is disconnected: unknown is not removed.
- No entry means the default, never another repository's account. Switching the vendor in the
  panel restores that vendor's own entry for the repository.

## 7. Settings: the Accounts tab

A new tab after Profiles in `SettingsWindow.axaml`, named **Accounts**, with one line under the
title saying what it is: Claude and Codex config directories on this machine, each recording its
sessions and pickable when a session starts. The line exists because the Profiles tab beside it lists
kcap sign-ins.

- Rows grouped by vendor: label, directory, Default badge, recording state as Part A reports it
  (Recording, Needs trust with its hint, Broken, Not wired), the unregistered-default row of
  Section 3 where it applies, and a menu with Rename, Re-wire and Remove. Part B adds email, plan,
  the bars with resets, the reading age and Refresh.
- **Add account** offers the candidates `AccountDiscovery.Find` returns, then a directory picker.
  Adding wires the account. Signing in is the person's job in the vendor CLI, as the first spec
  states.
- Actions run the CLI: `kcap accounts add`, `remove`, `rename` and `rewire` through the app-lifetime
  mutation lane's existing executor (`DaemonMutationLane` over `IKcapCli`), one at a time, as daemon
  mutations do. The daemon does no wiring: the CLI already owns `AccountWiring` and its
  `WiringOptions`, and a second wiring path in the daemon would need the plugin and MCP paths moved
  out of the CLI assembly.
- **Every verb commits a revision after its wiring attempt**, under the registry lock, recording the
  wiring outcome on the entry: an add that wired, an add whose wiring failed (still registered, as
  today, and reported as a failure), a rewire that changed nothing, a rename, a removal. The
  revision in the result is this post-wiring one, so a snapshot cached before the wiring cannot
  satisfy it, and a rewire produces a snapshot the watcher can see.
- The CLI gains a machine-readable result for the lane carrying each wiring step's outcome and that
  revision. Success is exit 0 with every step succeeded and a snapshot at or past the revision; a
  failed step is a failure even when the row already looks right. A missing or malformed result, a
  timeout, or a snapshot subscription lost before the revision arrives is **unconfirmed**, never
  success; the next complete snapshot shows the true state.
- Each action carries its own request id, so the lane never coalesces two actions, equal or not,
  the first spec's rule. The lane's request factory admits the account verbs without a server
  profile, since they touch nothing on the server.
- This machine only. A daemon that does not advertise the frames gets the tab in a read-only state
  that says to update kcap on this machine, and the launcher shows no account rows for it.

## 8. In the session: the strip under the composer

Under `ComposerCard` in `ChatTabView.axaml`, inside the composer band, one line for every Claude or
Codex session whose resolved account is known: the vendor glyph and the account label, then one bar
per window with its percentage and the next reset. Before any reading it shows the directory and
"no reading yet". When the account is blocked, the exhausted window's bar is danger-coloured and a
danger pill reads "Limit hit" with the reset; the existing usage-limit notice in the composer is
unchanged.

The strip is read-only. It takes the resolved account from `ChatSessionInfo`, filled from
`AgentStatusDto` on the local lane and from the server's agent row on the remote lane, and the
reading, when the account is registered, from the host's registry snapshot. A session from a daemon
that reports no account shows no strip, as does any vendor without accounts.

## 9. Readings

Part B of the first spec is the data source for every bar here and is built as written there, with
three changes:

- The Accounts screen of its Section 6.4 is the Settings tab of Section 7; there is no standalone
  page and no host switcher.
- Its Sections 5.3 and 5.4 attribute app-server readings and limit hits to "the daemon's default
  account until spec 2". With Section 4.3 the agent carries its resolved account, and both attribute
  to it when it is registered; an unregistered default has no reading to attribute to.
- The bars also appear in the launcher rows (6.1) and the composer strip (8).

## 10. Delivery

Five pull requests, each shipping on its own. The server carries this CLI as a submodule, so the
daemon's contract change lands before the server's, and the server's account-launch support is
enabled only by the submodule update that carries it. Nothing relies on merge order for
correctness: the gates of Section 4.1 hold for every mixed pairing of app, server and daemon.

1. **kcap-cli, daemon and CLI.** Sections 3, 4 and 5: the account id on the launch command, the
   resolved account, per-launch layouts and the child-environment rule, the resume ledger field
   and resume policy, agent status fields, per-account Codex catalogs with their lifecycle, the
   registry watcher, the local snapshot frames, the `accounts/1` advertisement, publishing to the
   server behind its capability, the CLI's post-wiring revisions and machine-readable results, and
   `kcap agent start --account` with its help text and README lines.
2. **kcap-server.** Section 4.1's pass-through and refusal, `accounts/1` on the listed daemon, the
   agent row's account, the per-account catalog on the listed daemon, and the accounts store of
   Section 5.2 with its split between host registry and daemon defaults.
3. **kcap-cli, app.** Sections 6, 7 and the strip of Section 8 without bars.
4. **kcap-cli and kcap-server.** Part B of the first spec: the status line wrapper, the Codex
   producers, the readings store, publishing.
5. **kcap-cli, app.** The bars, in the three places.

README and `help-*.txt` change with the PR that changes user-facing behaviour.

## 11. Testing

- **Daemon, launch:** an explicit id resolves to its directory; null resolves to the daemon's
  default; an unknown, cross-vendor or missing-directory id is refused with `account_unavailable`
  and nothing is spawned; the child environment, for a daemon started with a custom inherited home,
  removes the variable for the built-in location, sets it for the daemon's own default and for
  another custom account, on the PTY and app-server paths alike, with `.claude.json` landing where
  Claude reads it in each case; trust, MCP config and project symlink land in the resolved
  directory and not the default's; the daemon's process environment is unchanged after a launch.
- **Daemon, resume:** a resume relaunches under the ledger's directory after the daemon restarts
  with a different environment; a removed registered account fails with `account_unavailable`, not
  `launch_rejected`, and no continuation is attempted; an unregistered default resumes under its
  directory and fails named when the directory is gone; a legacy entry resumes under the current
  default.
- **Daemon, snapshot and catalog:** the snapshot carries the host id, the revision and the daemon's
  defaults, and registers nothing; a second daemon with another environment publishes different
  defaults under the same revision, in both orders, and a restart of one with a new default
  republishes defaults without a revision change; a removed and re-added default, followed by the
  other daemon's lower-revision publication, resolves to the accepted registry's row and catalog;
  an unregistered Codex default with no hooks is refused on the PTY and app-server transports and
  starts and records on both with repository hooks; directory keys are computed on the host, so two
  Linux directories differing only in case stay distinct rows for a macOS reader with the Default
  badge on the right one, and a default spelled through a symlink in the daemon's environment
  matches its registered row, the request carrying null for it and the explicit id for the other;
  a registry change reaches the local snapshot and
  the server within the throttle; adding, removing and re-adding a Codex account without a restart
  probes, drops and re-probes its catalog, a late probe for a removed account is discarded, and an
  auth or cache change on a non-default account republishes the map alone.
- **CLI:** each verb commits a post-wiring revision, including an unchanged rewire and an add whose
  wiring failed; the result names each step's outcome and that revision; a failed step reports
  failure with exit 1 even when the registry already holds the requested state; `kcap agent start
  --account` resolves an id prefix and a registered directory, refuses an unregistered directory and
  a daemon without `accounts/1`, and the daemon spawns under the resolved directory.
- **Server:** old daemon with new server and the reverse; the id reaches a daemon with `accounts/1`
  unchanged and is refused, named, for one without; the agent row carries the resolved account; the
  per-account catalog rides beside the per-vendor one for an older app; registry snapshots with
  equal revisions from two daemons keep both daemons' defaults.
- **App, headless view-model tests:** the section appears at two rows and not at one, counting an
  unregistered default; it is absent without a complete snapshot and for a daemon the server lists
  without `accounts/1`; the chip label with and without a non-default account; the request carries
  null for the default; restore per repository, vendor and host id runs on a repository, vendor or
  machine change once the snapshot is complete, and a late snapshot restores unless the person has
  picked since; an entry absent from its host's complete snapshot is dropped, and one is kept
  through a missing snapshot, a disconnect and a switch to another host; the Default pick removes
  the entry; a vendor switch restores the other vendor's entry; a remembered local account with a
  new daemon behind an old server restores nothing and sends null, but only once that server's
  listing has answered; a ready local daemon with a delayed or failed first listing and a saved
  non-default account captures no draft and sends nothing until the listing and the snapshot
  resolve, and with the connection kept up and no daemon-change notification the retry, or Retry,
  fetches again, after which the remembered account restores and Start is available without a
  restart or a reconnect; a result from a superseded connection is ignored; a reconnect returns
  support to unknown and Start waits; a choice made on a supported
  target survives support withdrawal while idle as explicit-invalid, Start refuses named and sends
  nothing; Start before the first complete snapshot of a supported target is disabled and captures
  no draft, and a snapshot arriving during an in-flight upload changes nothing; an explicit
  selection removed before Start, one
  removed during an attachment upload, and one whose target lost `accounts/1` before sending, fail
  named with no request sent and the draft kept; removing the only non-default account keeps the
  section with the Default row reachable, and Use default in the error lets the next Start succeed
  without changing repository, vendor or machine; the lane
  admits an account verb with no server profile, refuses success on a failed step, treats a lost
  result or subscription as unconfirmed, and never coalesces two queued actions; the tab's read-only
  state on an old daemon; the strip's three states, its directory for an unregistered default, and
  its absence for other vendors and for an older daemon.
- **App smoke:** the panel opens with the section and keyboard focus reaches the account rows.

## 12. Not doing

- Switching the account of a running session, or choosing one again when a limit hits.
- A reviewer account per vendor.
- Reading, copying or brokering any vendor credential, as before.
- Changing a remote machine's accounts from the app.
- A standalone Accounts page or a host switcher.
