# Desktop session rename

Linear: AI-4667

## Intent

The desktop app has no way to rename a session. Today a title only changes when the agent runs
`kcap set-title`, the harness renames (Claude's `/rename`), or the server generates one. The owner
wants to rename a session directly from the session's header in the desktop app, and see the new
name everywhere the app shows it.

## Goals

- A pencil beside the session title in the session header lets the user rename the session in
  place.
- A desktop rename behaves exactly like a harness rename on the server: rename tier on the title
  ladder, timestamped, newest rename wins. Agent, vendor and generated titles never overwrite it;
  only a later rename (desktop, harness, or any other rename source) does, within the known
  limitations below.
- On success, the header title and the session rail row update together, immediately — no wait for
  the daemon's 60 s title tick, and no flicker back to the old title.
- Works for every session the user has permission to rename: sessions on this machine's daemon and
  remote sessions (other machines, hosted agents) alike.
- Remote rail rows show the session's real server title rather than a cut of the launch prompt, so a
  rename is visible on them.

## Non-goals

- Renaming from the Home tab cards or the rail itself (they only reflect the new title).
- A web-UI rename.
- Pushing the new name into the vendor harness's own session name (Claude's own `/rename` state).

## Anti-goals

- Reporting a rename as successful when the server's ladder refused it or the write failed.
- Losing the user's typed text on a failed save.
- Showing the pencil on a session the server would refuse (403) to rename.

## Interaction (decided by the owner)

- Pencil icon beside the session title in the header (title · activity status · agent/repo line).
- Click the pencil: the title becomes an editable text field; the pencil is replaced by a ✓ / ✗
  pair.
- Enter or ✓ saves. Escape or ✗ cancels and restores the previous title.
- On a successful save the field returns to read-only, the new title shows, and the pencil returns.
- No success banner or toast: the title changing in place is the confirmation.
- On failure, the error is shown through the existing toast (`IAppNotifier` →
  `WindowNotificationManager`, top-right), and the field stays in edit mode with the user's text so
  they can retry or cancel.

Builder's calls (not owner decisions):

- Saving empty/whitespace text or unchanged text behaves as cancel.
- The field caps input at 120 characters, matching `kcap set-title` and the server's normalisation.
- While a save is in flight, ✓ / ✗ and the field are disabled; the pencil does not return until the
  server answers.
- The pencil is hidden while the header has no session id yet (a pending launch).

## Where it lives

Two headers show a session title and both get the pencil:

- `src/Capacitor.App/Views/WorkspaceView.axaml` — local workspace header (`WorkspaceTitle`), title
  from `WorkspaceViewModel.Title` (daemon `AgentStatusDto.Title`, or the pending row's title).
- `src/Capacitor.App/Views/RemoteSessionView.axaml` — remote session header (`RemoteTitle`).

The rail (`SessionRailView.axaml`, `RailSessionViewModel.Primary`) reads `AgentRow.Title` from the
merged directory.

## Server contract

The rename write needs no server change. The app writes the rename through the existing owner-checked
`POST /hooks/agent-title` with `rename: true`, authenticated as the signed-in user. That route:

- checks ownership (404 not visible, 403 visible-not-owned),
- records an `agent-rename` title with a server-side `EffectiveAt`, which is rename tier on
  `SessionTitleLadder` (newest timed rename wins), and
- answers `AgentTitleResult(applied, shown)`, so the client can tell an applied rename from one the
  ladder refused.

`applied == false` is a failure for the UI (toast + stay in edit mode), never a success.

Choosing this route over a new `user-rename` source is the builder's call: it already has the exact
semantics required and needs no server deploy. The cost is that a desktop rename is labelled
`agent-rename` in `sessions.title_source`.

### Known limitations (accepted by the owner)

The ladder orders renames by `EffectiveAt`, and the clocks behind it differ: a desktop rename is
stamped with server receipt time, a harness rename with the agent machine's clock or transcript time,
and some harness renames carry none. The owner accepted these gaps for this work:

- An untimed harness rename made after a desktop rename never shows (`ApplyRename`: an untimed
  rename never overrides a timed one).
- If the agent machine's clock runs ahead of the server's, a later desktop rename is refused
  (`applied == false`) and the UI reports it as a failure.
- Re-using an earlier harness name after a desktop rename (`/rename X` → desktop `Y` →
  `/rename X`) is dropped as a re-send; `Y` stays.

Fixing them needs a server-side ladder change and is out of scope here.

## Making both places update immediately

Local sessions: the header and rail both derive from the daemon's `AgentStatusDto.Title`
(`AgentOrchestrator` `ResolvedTitle`, driven by `TitleResolveLoop`, which treats the server title as
authority on a 60 s tick). A UI-only override would be clobbered by the next dto emission until the
tick. So a local rename goes through the daemon:

- A new local-control IPC frame asks the daemon to rename an agent's session. Per the IPC
  invariant it gets a new, never-reused `FrameType` value and is advertised in
  `LocalControlCapabilities.Current` beside its handler; the app only offers the pencil for local
  sessions when the daemon advertises the capability.
- The daemon posts the rename to `/hooks/agent-title` (`rename: true`), and on `applied` sets the
  agent's resolved title and seeds `TitleResolveLoop`'s server-title state with it, then publishes
  the updated dto. Header and rail then update from the same dto.
- The reply carries `applied` / `shown` / failure reason back to the app.
- `TitleResolveLoop` discards a server title that matches the prompt's first line (`IsPromptEcho`)
  or one of its own push attempts. A rename the daemon applied must never be discarded that way, or
  renaming to such text reverts on the next tick. Builder's call: the loop records an applied rename
  as authority that only a different server title replaces.
- `TitleResolveLoop.TickAsync` holds per-agent state across awaits (server read, generation, push)
  and applies its result afterwards. A tick that read the old title before the rename must not
  apply it after the rename: each agent's title state carries a revision that an applied rename
  bumps, a tick captures the revision when it starts, and it discards its apply and its push when
  the revision moved. Required test: pause a tick's server read (and separately its push) across an
  applied rename, release it, and assert the header title never returns to the old value.

Remote sessions: the app writes the rename to the server directly as the signed-in user. Remote rows
are rebuilt from `RemoteAgentsService`'s cache on every `AgentDirectory.Recompute`, and that cache is
replaced by each `/api/agent-instances` fetch, so an ad hoc row edit would not survive. Instead
`RemoteAgentsService` owns a title overlay:

- On `applied`, it records `session id → shown title` stamped with its fetch sequence number at
  that moment, and republishes the affected instance so the directory recomputes with the new
  title.
- A fetch that started before the rename never clears the overlay.
- A fetch that started after it clears the overlay entry once the payload carries a title for that
  session; the server's title then wins, whether it is the rename or a newer one.
- Against an older server whose payload has no title field, the overlay entry lasts for the app's
  lifetime, so a refresh cannot restore the prompt cut.

## Remote rail titles

`AgentRow.FromRemote` sets `Title` from `TitleFromPrompt(dto.Prompt)`; `AgentInstanceDto` carries no
title. Remote rows must show the session's server title instead, falling back to the prompt cut only
while the server has none.

The owner chose **live**: a title change made anywhere (a rename on another machine, that machine's
daemon pushing a native or generated title, server generation) reaches remote rail rows and the
remote header within seconds.

The client's live path already exists: the hub's `AgentInstancesChanged` is a payload-free
notification, and `RemoteAgentsService` throttles it and refetches `GET /api/agent-instances`.
"Live" therefore means notification plus refetch, not a title-bearing hub event. That needs a
`kcap-server` change:

- `GET /api/agent-instances` carries each instance's current session title (a new nullable field on
  `AgentInstanceDto`; older servers omit it and the client falls back to the prompt cut).
- A session-title change on a session with a registered agent instance raises
  `AgentInstancesChanged` for the clients that see that instance.

The server change ships before the client depends on it.

## Who sees the pencil

Every session the user may rename:

- Local daemon rows: shown only when all hold —
  - the daemon advertises the rename capability,
  - the daemon reports the app's own server (`IAgentDirectory.LocalDaemonOnAppServer`),
  - the daemon is signed in as the app's signed-in user (the daemon reports its authenticated user
    id alongside the capability; the app compares it with its own subject), and
  - the agent is not private. A private agent's contract is "no per-agent server calls"
    (`SnapshotAgentsForTitles` drops its session id), so it cannot be renamed.
- Remote rows: shown when `AgentInstanceDto.OwnerUserId` matches the signed-in user.
- Hidden for read-only participants and sessions without a session id.

Required acceptance cases: daemon signed in as a different user, daemon pointed at a different
server, a private agent, and an older daemon — each shows no pencil.

The server re-checks ownership on every write; the client-side gate only avoids offering a rename
that would 403.

## Constraints

- NativeAOT: no reflection-based JSON; new DTOs go through the source-generated contexts.
- Local control IPC is append-only and capability-gated (see `CLAUDE.md` invariants).
- An older daemon without the new capability must leave the app working: no pencil on local
  sessions, nothing else changes.
- Tests per repo conventions (TUnit, Helpers' `TempDir`, etc.).

## Left to the builder

- Icon assets and exact layout/spacing of the pencil and ✓ / ✗ in both headers.
- The IPC frame's shape and the daemon-side plumbing.
- Error copy for each failure (not owned, not connected, ladder refused, server error).
