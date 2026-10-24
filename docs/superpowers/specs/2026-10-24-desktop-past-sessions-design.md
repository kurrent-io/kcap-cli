# Past and unhosted sessions in the desktop app (design)

GitHub: [#1910](https://github.com/kurrent-io/kcap-cli/issues/1910). Linear: [AI-4195](https://linear.app/kurrent/issue/AI-4195/bring-session-list-and-details-closer-to-the-web-app) (items 2, 3, 10, 11 and the open question on where recent sessions come from). Agreed with the owner on mockups on 2026-10-24: [Desktop sessions: past and unhosted](https://kurrent.kcap.ai/pages/ccf13776d76d49898feda4b3c2e7220e) (org-visible kcap page, version 2)

## Problem

The desktop app is a control room for agents a daemon hosts. The Sessions rail lists the local daemon's agents, the server registry's hosted agents on other daemons, and launches in flight; a session drops off the moment it ends, and a Claude Code or Codex run started in a plain terminal never appears at all, although the hooks record it and the server streams it.

What people want from the app first thing in the morning is "what have I, or my teammate, been doing lately", without starting an agent and spending tokens. Claude Desktop and the Codex app keep history in front of the user, and some users stay on them for that reason alone. The web app has the list and the overview; the desktop app should carry both, so it is the place a user opens first

## Goals

- The rail lists every session the server lets the signed-in user see: theirs and their teammates', live or ended, hosted by a daemon or started in a terminal
- A user filters by scope and state, chooses how rows are grouped and sorted, and the app remembers those choices for that user
- An ended session opens on an overview a reader can take in without reading the transcript: outcome, figures, pull request, changes, evaluation, turns
- A live session from a terminal opens read-only, as a session on another machine does today
- ⌘K finds any visible session by title or transcript text
- A turn of an ended or live session can be forked into a new hosted agent from the overview, as the web allows

## Non-goals

- Sending input to, or answering prompts from, a session the app has no daemon for. A terminal session is read-only; answering its permission and question prompts through the server is a separate piece of work
- Cost figures. The price table lives behind the web app; no public read carries it. The figure is omitted, not shown as zero
- The full evaluation report, the raw events timeline, fork comparison, delete and close. They stay on the web, as AI-4195 lists
- Browsing organisation-wide by project or by owner other than "me or everyone I can see"
- Syncing the rail preferences across machines. They are per user on this machine

## Anti-goals

- A rail that grows with history. Live rows are few and always all present; the first twenty ended rows load with the scope and refresh when a session ends, and anything older arrives only on request
- A second list. The rail is the one place sessions live; a History view beside it was considered and rejected on the mockups
- Success green or warning orange on identity. Status dots and PR marks keep the status palette; titles, vendor marks, owner initials and keys use text and muted tokens
- A hosted agent's row doubled by its own session. One session, one row

## Current state (what the change builds on)

- **Rows.** `IAgentDirectory.Rows` is a `SourceCache<AgentRow, string>` keyed `local:{id}`, `remote:{id}` or `pending:{id}`, merged in `AgentDirectory` from the local daemon's status snapshots, the server registry (`api/agent-instances` plus `AgentInstancesChanged`) and pending launches. Suppression between the local and remote lanes is pairwise by agent id for a proven twin daemon, and only while `LocalDaemonOnAppServer` holds. The remote lane already drops Completed and Failed registry rows; the local lane keeps a row until the daemon unpublishes it at process exit, and keeps Interrupted rows for resume. `SessionRailViewModel` groups rows by `RepoGroupKey` with nested DynamicData groups, `SortAndBind` at each level and a per-repo collapsed set kept in memory only
- **Identity.** The app knows the signed-in profile (name and server URL) at startup and the token's subject only once the lane connects; the subject's spelling differs from the server's canonical owner id for legacy GitHub tokens, and one user may own sessions under several alias ids. Nothing in the app compares owner ids today
- **Workspaces.** `WorkspaceViewModel` is the local PTY workspace. `RemoteSessionViewModel` is the read-only workspace for a row the app has no socket to: `RemoteTab { Chat, Terminal, PullRequest, Artefacts }`, the chat over the server stream through `SessionAccessService` (access watch plus chat group, with retry), Stop and Open in web, a `WorkContextViewModel` pane, and `SessionDetailReader` for the transcript. It keeps an ended session's transcript on screen while the lease's last verdict stands. It is constructed from an `AgentRow`
- **Server reads the app already makes.** `ServerSessionHttp` wraps `api/sessions/{id}/detail` (mapping four fields of `SessionDetail`: id, ended at, last event number, events), `api/sessions/{id}/agents/{agentId}/events` and the permission response. `AuthenticatedServerReads` and the `IPullRequestSource`, `IPlanSource`, `IPlanArtifactSource` and `IWorkContextSource` implementations read PRs, plans, plan artifacts and the work context per session
- **Server reads the app does not make yet.** `GET api/sessions/listing?state&owner&limit&offset` returns `RepoSession` rows: title, vendor, status, a `stale` flag, started, ended and last-activity times, repository, branch and cwd, and the owner with username, display name and avatar; `owner=me` is resolved on the server. Without a time window the rows are ordered by last activity with running ones first; `limit` is at most 100 and `offset` at most 500. The rows carry no pull request, model or eval score. `GET api/sessions/active` returns every active session the caller may see as `ActivitySession` (repo, branch, PR, stats, owner id only, no owner filter); `GET api/sessions/ended?page&pageSize&owner` pages `ActivitySession` rows in start order, with the eval score on the row; `GET api/sessions/owners` lists the visible owners with `IsMe` and their alias ids. Per session: `GET api/sessions/{id}/summary` (one `ActivitySession`); `/turns` and `/turns/{index}`; `/eval-progress` (the completed eval with score, categories, judge summary); `/pull-requests`. `GET api/sessions/search?q&author&repo&limit` searches title, turn prose and transcript. `SessionDetail` also carries `WhatsDoneContent` and `PlanContent`. Below full access the server blanks branch, cwd and prompt on a row, shapes `detail` rather than refusing it, and serves `turns` and `eval-progress` at the activity floor
- **Hub pushes the app already receives.** A UI connection joins the organisation group on connect, and `ServerConnectionService` binds `ActiveSessionAdded`, `ActiveSessionChanged`, `ActiveSessionRemoved`, `SessionTitleChanged`, `SessionDeleted`, `SessionEvalCompleted` and `SessionWhatsDoneGenerated` as no-op handlers today; `SessionAccessChanged` is already consumed. `SubscribeToChat` admits an ended session at the full floor
- **App state.** `AppStateStore` persists `AppState` as `app-state.json` under the app's config root: a flat record of UX preferences, none keyed by identity
- **Artefacts tab.** `ArtefactsTabViewModel` lists the documents a session declared or wrote, from plan artifacts, with a reader beside the list. The web's nav calls the module **Pages**; `/artefacts` is the older route form shared links still carry
- **Forks.** `SessionForkService` in kcap-server's `Capacitor.Api.Web` forks a turn of a session the viewer holds full access on into an ordinary hosted agent on one of the viewer's daemons: it resolves access, lists checkouts from daemon discovery, resolves base commit options, builds a prompt from the carried turns (summaries or full turns), launches with a `baseRef`, and records the fork for lineage. It is registered under `Features:SessionFork` and reached only by the Blazor pages; the public API has no fork route, and the app's `LaunchAgentRequestV2Payload` carries no base ref

## Decisions

### D1 — Scope: the server's view of sessions, merged with the daemons' view of agents

The rail gains a third lane, keyed `server:{sessionId}`, fed by `api/sessions/listing`: `state=active` for the live rows and pages of `state=ended` for the rest, both with `owner=me` under the Mine scope and no owner under Team. The listing is the read because it alone gives what the rail needs on a row: the server resolves "me", orders by last activity, names the owner (the initials come from the display name or username) and carries the stale flag. It lacks the pull request, the model and the eval score; those three are an additive change to `RepoSession` in kcap-server, and until a server carries them a server row shows no PR mark and no score. Live rows are read whole: every page of the active listing, re-read on `ActiveSessionAdded`, `ActiveSessionChanged` and `ActiveSessionRemoved`, with a slow poll as a fallback while the lane is connected. Ended rows are paged (D2). The lane goes stale with the server lane, as remote rows do today

One session, one row, decided by liveness. A daemon row that is live (Starting, Running, Idle, waiting, or Interrupted and resumable) suppresses the server row for its session, because it carries what the server cannot: the PTY, Stop, the composer, the resume. A daemon row that has finished (Completed) is suppressed by the server's row for the same session, so a finished hosted agent on this machine opens on the Overview like any other ended session rather than on a dead local workspace. A Failed daemon row stays and suppresses the server row: its failure reason is the needs-you state the rail exists to show. Suppression against the local lane is gated on `LocalDaemonOnAppServer`, as every local-to-server match is. When a daemon row disappears before the server has recorded the session's end, the server's live row for that session keeps the hosted presentation, no terminal chip, for a thirty-second grace after the daemon row went, so the hand-over never flashes as a terminal session

A row with no repository groups under "No repository", as remote rows without one do now

### D2 — The rail: one list; scope, state, group and sort are the user's

Under the search field (D6) sit two rows of controls:

- **Scope** segment: Mine · Team. Mine is `owner=me`; Team is no owner filter, so it means everyone the server lets this user see, not a named team
- **State** segment: All · Live · Ended
- **Group** picker: None · Repository · Date · Status
- **Sort** picker: Last updated · Created · Title, with a direction toggle

Grouping:

- **Repository**: the grouping the rail has today, by `RepoGroupKey`; "No repository" last. A repository appears whether or not this machine has a checkout, because the server names it
- **Date**: by the viewer's local day of last activity, newest day first; Today and Yesterday by name, then `ddd d MMM`
- **Status**: Waiting on you · Running · Failed · Ended, in that fixed order, an empty group omitted. Waiting on you and Failed come from the daemon lanes (awaiting input, pending permission, Failed or Interrupted); a server-only live row is Running, stale or not
- **None**: one flat list

Sorting applies inside each group: last updated is last activity (a live hosted agent's latest status revision counts), created is started-at, title is case-insensitive ordinal. Direction flips the comparison. Within a group a row never moves on a state flip alone; it moves when its sort key changes

Paging: the ended listing fetches twenty rows at a time, newest first by last activity, for the current scope. "Show 20 more" at the foot of the rail appends the next page; the footer says how many of the scope's ended sessions are loaded ("12 of 212"). The listing's offset stops at 500, so after twenty-five pages the foot reads "Search or narrow the scope for older sessions" instead of offering more. Grouping and sorting rearrange what is loaded; they never ask the server for a different order. A scope change discards the loaded pages and fetches the first again; a state change does not refetch, it filters. When a session ends (`ActiveSessionRemoved`, or a live row flipping to ended), the first page is re-read and merged by session id over the loaded set, so the session that just ended is on the rail without a click; later pages are kept as they were and may overlap by a row, which the merge absorbs. Collapsed groups are remembered by group key, as repository groups are today, and the set is part of the remembered preferences (D3)

Row anatomy, in the rail's existing row: the status dot (purple running, orange waiting, muted stale, outlined ended), the title, then one meta line: vendor mark; the owner's initials when the owner is not the signed-in user; the worktree label for a hosted agent on this machine, the machine badge for a remote daemon's agent, a **terminal** chip for a live session no daemon hosts, and the repository name when the grouping is not by repository; the relative time (age while live, ended-ago and duration once ended); the PR mark at the right. A row's duration and age tick on the rail's existing minute timer

### D3 — Preferences are remembered per user

Scope, state, group, sort, direction and the collapsed-group set are stored in `AppState` keyed by signed-in identity: the profile's server URL and profile name, both known at startup, so the rail opens in the remembered shape rather than rearranging itself when the lane connects. A profile is one account on one server, so a different user on the same machine, or the same user on a different server, gets their own set. The default for a key never seen is Mine · All · Repository · Last updated · descending, nothing collapsed. The web's own preference store is not used; syncing across machines is a non-goal

### D4 — An ended session opens on its Overview

Selecting an ended row opens the read-only workspace with a new **Overview** tab, the default when the session has ended; Chat stays the default while a session is live. The surface switch reads Overview · Chat · Pull Request · Pages, the last two shown only when relevant as today. Header: title; vendor mark, vendor and model; owner initials and name; repository and branch; start and end times; "ended". Open in web stays; Stop is absent

The work-context pane is hidden while Overview is active and shown on the other tabs. Overview is one stacked report, a card hidden rather than left empty when its input is missing:

1. **Work item chip**: the primary work item the session is attached to, from the work-context read, opening the pane's work item as today
2. **Figures line**: Evaluation (score and band word, Failed, or Not scored), Duration, Turns, Tools (count and errors), Changes (lines added and removed, files), Context (peak occupancy as a percentage when the window is known, else the peak token count), Plan (done of total) when the session declared one. Cost is omitted (non-goal). Sources: `summary` for stats and eval flags, `turns` for the count, `plans` for the ledger
3. **Outcome**: the server's generated "what was done" (`WhatsDoneContent` from `detail`) rendered as markdown with a copy action; a muted line when none was generated. `SessionWhatsDoneGenerated` refreshes it. The detail read is the one the Chat tab performs; the builder shares one read between the tabs rather than fetching the event list twice
4. **Pull request** and **Changes** side by side: the pull request card the live workspace uses, with "Open in reader" switching to the Pull Request tab; the busiest files by lines touched from `summary` stats, a created-then-edited path folded into one row, with the total and "N more"
5. **Evaluation** and **Turns** side by side: score, judge summary, per-category scores and the weakest question from `eval-progress`, with "Open report in web" to the web's evaluation tab, refreshed on `SessionEvalCompleted`; the Turns card lists each turn's prompt first line and time from `turns`, click lands in the Chat tab at that turn, and each row offers **Fork from here** (D7)
6. **Plan**: the declared task list when one exists, through the plan section the pane already renders
7. **Actions**: Read the transcript (Chat tab), Copy session link

Chat is the existing read-only chat with the composer replaced by an "ended" line naming the end time. Pull Request is the existing reader. Pages is the Artefacts tab renamed (D9)

Opening a session no daemon knows needs a workspace built from a session rather than an agent. The builder generalises `AgentRow` or gives the read-only workspace a session-only identity; either way `RemoteSessionViewModel`'s Stop, Terminal and composer are absent when the row has no agent, and its access lease, chat join and detail read are reused as they are

### D5 — A live session from a terminal is read-only

A live server row no daemon hosts opens the read-only workspace on Chat, with a banner "Running in a terminal · read-only here: answer it where it runs". The server does not name the machine a terminal session runs on, so the banner names none. No Stop, no Terminal tab, no composer; the permission and question cards the chat renders for hosted agents are not rendered for such a row, since the app cannot answer them (non-goal). The rail row carries the terminal chip where the machine badge sits

### D6 — ⌘K searches the server

A search field at the top of the rail, focused by ⌘K, queries `api/sessions/search` with the typed text (title, turn prose and transcript lanes, as the MCP tool does), debounced, and lists hits beneath the field: title, owner, repository, when, and the matching snippet. Under the Mine scope the query carries the signed-in user as `author`, taken from the `IsMe` row of `api/sessions/owners`, read once per connect. Enter or click opens the hit as a rail selection would. A hit whose session is not among the loaded rows is added to the loaded set so the rail shows it selected; it leaves the set with the next scope change. Escape clears the search and restores the list. The rail footer's existing "search" control, which configures the local server's embeddings, is unrelated and unchanged

### D7 — Fork from a turn, through a public fork API

The web's fork is a server service with no public route. Forking from the desktop therefore needs, in kcap-server, public routes that wrap `ISessionForkService` as the Blazor pages call it: prepare (turn summary, base options, checkouts with their vendors, token estimates for each context mode), start (the `ForkRequestView` shape: base choice, context mode, instruction, daemon and repo path, vendor, model, effort, tools, visibility, grants), and the lineage reads (forks of a session, source of a fork). Availability is the service resolving, as on the web; an absent feature hides every fork surface in the app

In the app, **Fork from here** on a Turns row opens a dialog mirroring the web's: the turn's summary line; base (the commit recorded at the turn, the branch head when the daemon can resolve it, with uncommitted files listed); checkout (daemon and repository path) among the user's own daemons; vendor and model as the launcher offers them; context (summaries or full turns, each with its token estimate); and the instruction, "same ask" by default. Start requests the fork and the new agent appears as a pending row and opens as the launcher's own launches do. The forked session's overview header reads "Forked from <title> at turn N"; the source's Turns rows show a fork count. "Continue in a new session" is this, from the last turn, and is not a separate action

Layering in kcap-server: `ISessionForkService` and its views live in `Capacitor.Api.Web.Abstractions`, which `Capacitor.Api.Public` does not reference, and the implementation resolves its viewer from Blazor's authentication state. The public routes therefore get their own frozen request and response records in `Capacitor.Api.Public.Abstractions`, mapped from the web views, and the service they call takes the viewer id as the concrete `SessionForkService` already does; moving the service below the web layer is the server PR's call. This is the decision that crosses repositories: kcap-server first, then the app. Until the server side ships, the Turns card has no fork action

### D8 — Live updates come from the hub, polling is the fallback

The server lane already receives `ActiveSessionAdded`, `ActiveSessionChanged`, `ActiveSessionRemoved`, `SessionTitleChanged` and `SessionDeleted` and discards them; the session lane takes them over and applies each to its rows: an added or changed active session re-reads the live listing, a removed one re-reads the live listing and the first ended page (D2), a title change renames the row in place, a deletion drops it. `SessionEvalCompleted` and `SessionWhatsDoneGenerated` refresh the open overview's cards. A slow poll of the live listing covers a missed push while connected; nothing polls while the lane is down

### D9 — The tab is Pages

The Artefacts tab is renamed Pages in the surface switch and in every label that names it, matching the web's nav. Its contents are unchanged by this design: the declared documents stay in it, and the published pages group remains the open slice it already is

## Slices, in delivery order

1. **Server sessions in the rail.** The server lane over the listing, suppression against the daemon lanes, the four controls, grouping and sorting, paging and the end-of-session refresh, remembered per-user preferences, the terminal chip and owner initials, read-only opening of a server row on the existing Chat (live or ended), the Pages rename. kcap-cli, plus one additive kcap-server change: pull request number, title and URL, model and eval score on `RepoSession`, which the app reads when present
2. **The Overview.** The Overview tab for ended sessions with its figures, outcome, PR and changes, evaluation and turns, plan and actions; the shared detail read; the hub refreshes. kcap-cli only
3. **Search.** The ⌘K field over the server's session search. kcap-cli only
4. **Fork from a turn.** Public fork routes in kcap-server, then the fork dialog, lineage header and fork counts in the app

Each slice is a PR on its own; the spec rides the first, as the team convention has it

## Constraints

- **Status tokens are status only.** Owner initials, vendor marks, repository names and the terminal chip use text, muted and surface tokens; only the dot, the PR mark and the evaluation figure use success and warning
- **Rows never clobber across lanes.** The server lane's keys are `server:{sessionId}`; suppression of a server row is by session id against the other lanes' rows, computed in `AgentDirectory`'s single recompute, never in a view model
- **One ObserveOn at the top of the rail pipeline** stays. Grouping and sorting are DynamicData operators over the same source; regrouping on a picker change rebuilds the group operator, not the source
- **No request on a hot path.** Pages, summaries, turns and eval progress are fetched when a tab is shown and on a push, through `SingleFlightReader`, never in a recompute loop
- **A read the user is not entitled to hides its card.** Below full access the server blanks branch, cwd and prompt, shapes the detail and lets the chat lease land Denied; turns and eval progress still answer at the activity floor. The overview shows what it has and no error, and does not hang off the chat pane's own gate
- **Preferences are UX state**, stored in `app-state.json` beside the window size, and a failed write keeps them for the run
- **The 401 rule holds**: a sign-in lapse marks the lane stale and keeps the rows; nothing is dropped
- **Comments and commit messages** follow the repository's rules; the spec itself names no review artefacts

## Left to the builder

- The shape of the session-only identity for the read-only workspace (an `AgentRow` origin, or a sibling type), and how `MainWindowViewModel.OpenSession` addresses it
- The exact DTOs added to `Capacitor.Remote.Models` for the listing, owners, turns and eval progress, and whether `SessionDetailDto` grows the outcome and plan fields or a second mapping is used
- How a fork started on a remote daemon is surfaced, given that a launch on another daemon gets no placeholder row or auto-open today
- The control strip's Avalonia composition, reusing `kcapChip`, `kcapSegmentItem` and the launcher's `kcapPanel` pickers
- The debounce, hit count and empty state of the search field
- Which relative-time wording the ended row uses, matching `RelativeTime`
- Test placement, per the repository's test conventions: directory behaviour in the app unit suite, the dedup and paging rules as view model tests, no XAML-only smoke tests
