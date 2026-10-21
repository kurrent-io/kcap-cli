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
| Where in the launcher | Inside the Agent chip's panel, above the model rows, shown only when the vendor has two or more accounts on the target machine. No new chip. |
| Default | Per vendor and per daemon: the account whose directory the daemon's own environment points at. Not stored; derived. |
| Memory | Per repository, vendor and machine. No memory means the default, never what another repository used. |
| Managing accounts | A tab in the Settings window, this machine only. Remote machines are read-only in the launcher. |
| Account actions | The app runs `kcap accounts` through its mutation lane; the daemon only watches and publishes. |
| Readings in a session | A strip under the chat composer, always shown for a Claude or Codex session, read-only. |
| Reviewers | Review flows and PR reviews keep the vendor's default account. |
| Contract | One optional account id appended to the launch request on each hop. Null means the default. An unknown id refuses the launch. |
| Vocabulary | "Account" everywhere; "profile" stays a kcap server sign-in. |

## 3. The default account

The daemon resolves its vendor layouts once from its environment (`HarnessRegistry.FromEnvironment`
in `DaemonRunner`). That directory is the vendor's **default account** for this daemon: what a launch
with no account id runs under. It is derived, never written: the daemon registers nothing, because a
registered directory gets wired at the next refresh, and the first spec records no directory nobody
listed. Two daemons on one host with different environments share the registry and each advertises
its own default; the launcher targets a daemon, so "default" always means the targeted daemon's.

The snapshot (Section 5.1) names the default directory per vendor. When it is registered, that
account carries the Default badge. When it is not, which happens when the daemon's environment
differs from the shell `kcap setup` ran in, hosted sessions under it are not recorded, and both the
launcher panel and the Settings tab show it as a row of its own, labelled by directory name, with the
Default badge and a "not recording" hint; adding it is the row's one action. Removing a registered
default turns it into that row.

The registry is otherwise as Part A left it. Labels default to the directory name; Part B's identity
probe replaces them with the email.

## 4. Launching under an account

### 4.1 The request

`account_id` is appended as an optional field, null meaning the default, on each hop:

- App: `LaunchRequest` and `LaunchAgentRequestV2Payload` in `Services/ILaunchClient.cs`, and
  `LaunchDraft`, so the id is captured with the rest of the draft before the upload starts.
- Server: `RemoteLaunchRequest` and `DaemonLaunchCommand`, passed through untouched.
- Daemon: `LaunchAgentCommand` in `Capacitor.Cli.Core/Models.cs`, appended last under the same
  positional wire-compatibility rule as the fields before it.

The server does not validate the id. The daemon does: it reads the registry under no lock
(`accounts.json` is replaced atomically) and refuses the launch with a named reason when the id is
not registered, is registered for another vendor, or names a directory that no longer exists. A
refusal is reported like any other launch failure. The daemon never substitutes the default for an id
it cannot honour: the person chose the account to spend, and a silent swap spends the wrong one.

A launch with no id, including every review-flow and PR-review launch, runs under the default.

### 4.2 Per-launch layouts in the daemon

`ClaudeLauncher` and `CodexLauncher` each hold one `ClaudePaths` / `CodexPaths` taken from the
harness registry, and every write and read they make is keyed on it: workspace trust in `.claude.json`,
the MCP config, the project symlink, tool permissions, the Codex MCP inventory and `config.toml`
reads. Both launchers take the layout per launch instead, built with `AccountLayouts.Claude` /
`AccountLayouts.Codex` from the account's directory. The same applies to the Codex app-server runtime
and to anything else that starts a vendor process for an agent.

The spawned process gets its config directory explicitly, never by inheritance: `CLAUDE_CONFIG_DIR` or
`CODEX_HOME` set to the account's directory. The one exception is the layout's own rule: the default
directory is a null override, so for an account at the vendor's default location the variable is
left unset rather than set to `~/.claude`, which would move `.claude.json` away from where Claude reads
it.

Hooks and the transcript watcher already attribute a session to its account by path (Part A), and
`CLAUDE_CODE_SUBPROCESS_ENV_SCRUB` may strip the variable from hook environments; nothing here relies
on the variable reaching a hook.

### 4.3 The agent remembers its account

`ResumeLaunchDescriptor` in the resume ledger gains the account id. A resume after a daemon restart
relaunches under that account. When the account is no longer registered at resume time, the resume
fails with a reason and the entry is marked interrupted, as any other resume failure is; it does not
resume under the default.

`AgentStatusDto` in `LocalIpc/StatusIpc.cs` gains the account id and label, appended. The daemon's
agent registration and status report to the server carry the same two fields, so the server's agent
row shows the account for a remote session. All optional; an older daemon reports neither and the
app shows nothing for that session.

### 4.4 The Codex model catalog is per account

`CodexHostedAgentRuntimeFactory.ProbeModelsAsync` probes once per daemon with an empty environment
and fingerprints the default home's `models_cache` and `auth.json`. With accounts, it runs once per
registered Codex account, and for an unregistered default, with `CODEX_HOME` set to that directory,
fingerprinting that account's files. Per-machine catalogs already travel on `DaemonConnect.VendorModels`
to the server's `ListedDaemon` and on `DaemonStatusDto.VendorModels` to the local app, and
`HomeViewModel.ModelChoicesFor` prefers the machine's list; a per-account map keyed by account id is
appended beside `VendorModels` on both wires, with the default account's catalog staying in
`VendorModels` for older readers. The launcher reads the selected account's catalog, falling back to
the machine's vendor catalog, then to the curated list, so a launch is never blocked on this.

## 5. Listing a machine's accounts

### 5.1 This machine

Two of the local-socket frames of the first spec, Section 6.3, are built: `AccountsSubscribe` and
`AccountsSnapshot`, appended to `FrameType` and advertised in `LocalControlCapabilities.Current` beside
their handlers. The daemon watches `accounts.json` as that spec's Section 6.1 describes and pushes a
snapshot on change. The snapshot carries the registry revision, the default directory per vendor,
and the public account view: id, vendor, directory, label, and the recording state; Part B appends
identity and the reading. `AccountAction` and `AccountActionResult` are not built; Section 7 says
why.

### 5.2 A remote machine

The daemon publishes the same snapshot, without readings, to a server that advertises the `accounts`
capability: `DaemonConnect.Accounts`, `AccountsChanged` with the 30-second trailing throttle, and the
revision gate of the first spec's Sections 6.1 and 6.2. The server stores it per host and serves it
to the owner only, and the app reads the selected machine's accounts from there. Part B adds
readings to a snapshot that is already flowing. Until the server part lands, the panel shows no
account rows for a remote machine and the launch goes to its default.

## 6. The launcher

### 6.1 Account rows in the Agent panel

`LauncherPaneView.OnAgentChipClick` builds the panel: a vendor rail and searchable model rows. When
the selected vendor has two or more accounts on the selected machine, an **Account** section goes
above the model rows, under the search field, with one row per account: label, directory in faint
text, a neutral Default badge on the default, and the selection check the model rows use. Once Part
B exists each row also carries one bar per window with its percentage and the next reset. Picking a
row sets the account and rebuilds the model rows for it. The search field does not match accounts.

An unregistered default counts as a row (Section 3). With one row the section is absent and the
panel is unchanged.

### 6.2 The chip

The chip label stays "Claude · Opus 4.1" while the chosen account is the default and becomes
"Claude · Opus 4.1 · work" otherwise. The label is identity, so it takes no status colour.

### 6.3 The draft and the start

`HomeViewModel` keeps the selected account per vendor beside `SelectedModel`. Start validates it
against the latest snapshot for the selected machine and vendor; an account that has gone resets to
the default and the launch proceeds under it, with the reset shown in the chip. The request carries
null for the default.

### 6.4 Memory per repository

`AppState` gains `AccountByRepo`, a map keyed by repository path, vendor and machine name to an
account id, beside `HarnessByRepo` and with its reserved scratch key. Picking a row writes it; picking
the default removes the entry, so a later change of default is followed. A launch restores the entry
for the current repository, vendor and machine. An entry whose id is not in that machine's snapshot
is ignored and removed. No entry means the default, never another repository's account. Switching
the vendor in the panel restores that vendor's own entry for the repository.

## 7. Settings: the Accounts tab

A new tab after Profiles in `SettingsWindow.axaml`, named **Accounts**, with one line under the
title saying what it is: Claude and Codex config directories on this machine, each recording its
sessions and pickable when a session starts. The line exists because the Profiles tab beside it lists
kcap sign-ins.

- Rows grouped by vendor: label, directory, Default badge, recording state as Part A reports it
  (Recording, Needs trust with its hint, Broken, Not wired), and a menu with Rename, Re-wire and
  Remove. Part B adds email, plan, the bars with resets, the reading age and Refresh.
- **Add account** offers the candidates `AccountDiscovery.Find` returns, then a directory picker.
  Adding wires the account. Signing in is the person's job in the vendor CLI, as the first spec
  states.
- Actions run the CLI: `kcap accounts add`, `remove`, `rename` and `rewire` through the app-lifetime
  mutation lane's existing executor (`DaemonMutationLane` over `IKcapCli`), one at a time, as daemon
  mutations do. The daemon does no wiring: the CLI already owns `AccountWiring` and its
  `WiringOptions`, and a second wiring path in the daemon would need the plugin and MCP paths moved
  out of the CLI assembly. The CLI gains a machine-readable result for the lane, carrying each wiring
  step's outcome and the registry revision the action committed at. Success is exit 0 with every
  step succeeded and a snapshot at or past that revision; a failed step is a failure even when the
  row already looks right. The lane's request factory admits the account verbs without a server
  profile, since they touch nothing on the server.
- This machine only. A daemon that does not advertise the frames gets the tab in a read-only state
  that says to update kcap on this machine, and the launcher shows no account rows for it.

## 8. In the session: the strip under the composer

Under `ComposerCard` in `ChatTabView.axaml`, inside the composer band, one line for every Claude or
Codex session whose account is known: the vendor glyph and the account label, then one bar per window
with its percentage and the next reset. Before any reading it shows the directory and "no reading
yet". When the account is blocked, the exhausted window's bar is danger-coloured and a danger pill
reads "Limit hit" with the reset; the existing usage-limit notice in the composer is unchanged.

The strip is read-only. It takes the account from `ChatSessionInfo`, filled from `AgentStatusDto` on
the local lane and from the server's agent row on the remote lane, and the reading from the
machine's accounts snapshot. A session from a daemon that reports no account shows no strip, as does
any vendor without accounts.

## 9. Readings

Part B of the first spec is the data source for every bar here and is built as written there, with
three changes:

- The Accounts screen of its Section 6.4 is the Settings tab of Section 7; there is no standalone
  page and no host switcher.
- Its Sections 5.3 and 5.4 attribute app-server readings and limit hits to "the daemon's default
  account until spec 2". With Section 4.3 the agent carries its account, and both attribute to it.
- The bars also appear in the launcher rows (6.1) and the composer strip (8).

## 10. Delivery

Five pull requests, each shipping on its own. The server carries this CLI as a submodule, so the
daemon's contract change lands before the server's.

1. **kcap-cli, daemon and CLI.** Sections 3, 4 and 5: the account id on the launch command,
   per-launch layouts, the resume ledger field, agent status fields, per-account Codex catalogs, the
   registry watcher, the local snapshot frames, publishing to the server behind its capability, and
   the CLI's machine-readable account results.
2. **kcap-server.** Section 4.1's pass-through, the agent row's account, the per-account catalog on
   the listed daemon, and the accounts snapshot store of the first spec's Section 6.2.
3. **kcap-cli, app.** Sections 6, 7 and the strip of Section 8 without bars.
4. **kcap-cli and kcap-server.** Part B of the first spec: the status line wrapper, the Codex
   producers, the readings store, publishing.
5. **kcap-cli, app.** The bars, in the three places.

README and `help-*.txt` change with the PR that changes user-facing behaviour.

## 11. Testing

- **Daemon:** a launch with an account id spawns with the directory set; the default account spawns
  with the variable unset; an unknown, cross-vendor or missing-directory id is refused with its
  reason and nothing is spawned; a resume relaunches under the ledger's account and fails named when
  it is gone; trust, MCP config and project symlink land in the account's directory and not the
  default's; the Codex catalog probe runs per account and advertises per account; the snapshot names
  this daemon's default directory and registers nothing; a second daemon with another environment
  names a different one; a registry change reaches the local snapshot and the server within the
  throttle.
- **CLI:** each account verb prints its step outcomes and committed revision; a failed step reports
  failure with exit 1 even when the registry already holds the requested state.
- **Server:** old daemon with new server and the reverse; the id reaches the daemon unchanged; the
  agent row carries the label; per-account model options alongside per-vendor ones for an older app.
- **App, headless view-model tests:** the account section appears at two accounts and not at one;
  the chip label with and without a non-default account; the request carries null for the default;
  memory restore per repository, vendor and machine; the removed-account fallback and entry removal;
  the default pick removing the entry; vendor switch restoring the other vendor's entry; the
  unregistered default as a row in the panel and the tab; the lane admitting an account verb with no
  server profile and refusing success on a failed step; the tab's read-only state on an old daemon;
  the strip's three states and its absence for other vendors and for an older daemon.
- **App smoke:** the panel opens with the section and keyboard focus reaches the account rows.

## 12. Not doing

- Switching the account of a running session, or choosing one again when a limit hits.
- A reviewer account per vendor.
- An account flag on `kcap agent start`, which launches over the local Spawn frame; it keeps the
  default account.
- Reading, copying or brokering any vendor credential, as before.
- Changing a remote machine's accounts from the app.
- A standalone Accounts page or a host switcher.
