# AI-2840 — Subagents in the desktop chat (design)

GitHub: [#966](https://github.com/kurrent-io/kcap-cli/issues/966). Linear: [AI-2840](https://linear.app/kurrent/issue/AI-2840). The layout was agreed on a mockup during design: a one-line strip above the composer, and a section in the work-context pane.

## Problem

When a session starts a subagent, the desktop app has nothing to show for it beyond a tool-call row that reads "Ran an agent". There is no name, no running count, and no list. The web chat already names subagents and keeps the running ones in view.

Background subagents make it worse. Claude Code answers a background `Agent` call at once, the parent's message ends, and its Stop hook fires — so the daemon reports the session as waiting for input while the subagents are still working. The chat pane loses its "Working for …" note and the rail row shows no sign of activity, though the session is busy.

## Current state (what the change builds on)

- **Transcript shape, read off a live session.** A background launch is a `type:"user"` line whose `tool_result` block carries the spawning `tool_use_id`, with a root `toolUseResult` object `{ isAsync: true, status: "async_launched", agentId, description, … }`. The subagent's finish arrives later as a `type:"user"` line with `origin.kind == "task-notification"` whose text holds `<task-id>`, `<tool-use-id>` (the spawning call's id), `<status>` (`completed` or otherwise) and `<summary>`. Claude Code notes that the same task may notify more than once when the agent is resumed. Only single-result lines were observed carrying `toolUseResult`.
- A background agent killed through `TaskStop` never resolves its `Agent` call and never fires `SubagentStop`. Its only trace is the `TaskStop` tool result, whose `toolUseResult` carries `task_type: "local_agent"`, `task_id` (the agent id) and a message opening "Successfully stopped task". `TaskGet` and `TaskOutput` results carry the same `task_id` for agents that are still running.
- `ClaudeTranscriptEvents` projects both lines already — each `tool_result` block as its own `ToolResultReceived`, the notification as `UserMessageReceived` with `origin_kind` in the `claude_code` extension — but never reads `toolUseResult`. Event ids hash the record id and block index (`TranscriptIds`), never the extension.
- **The remote lane's events come from another producer.** kcap-server normalizes raw transcript lines with its own `ClaudeCodeNormalizer`; no server project references this repo's projection. It emits one `ToolResultReceived` per line — the first `tool_result` block — with `extensions.claude_code.tool_use_result` set to the raw root `toolUseResult`, so the `async_launched` status and the `agentId` are already persisted. It writes no `origin_kind`: the server recognises a task-notification by its text. #775 requires this repo's leaf to reach that same shape (`tool_use_result`, `output_raw`, `is_error`).
- The web chat folds subagents into a `SubagentSection` (agent id, agent type, prompt, start time, running flag) from `SubagentStarted`/`SubagentCompleted` plus the `Agent` call's `subagent_type` and `description`, and closes one on several signals because the stop hook is known to get lost: the completion event, a terminal `Agent` result, the `TaskStop` result above, session end, and a 15-minute quiet reaper. It has no failed state — nothing populates `SubagentCompleted.outcome` — and no endpoint or hub message exposes the sections, so a remote client derives its own.
- `ClaudeChatRules.Filter` drops sidechain and meta records, and rewrites a task-notification into a `SystemNote` built from `<summary>` and `<result>`; the `<tool-use-id>` is discarded. It identifies the notification by `origin_kind` alone, so on the remote lane, whose events carry none, the notification passes through as a user turn.
- `ChatProjectionResult(Envelopes, SubmittedInputs)` is what a feed hands the chat per projected line: the rows, plus a side-band of display facts that are not rows. `TranscriptChat.Project(evt, rules)` builds it for one canonical event. `LocalTranscriptFeed` reaches it through `IChatTranscriptProjection.ProjectWithInputs`, which aggregates every event of one transcript line; the interface's default implementation of that method builds the two-member result from `Project`, and `EnvelopeJournalProjection` relies on that default. `RemoteTranscriptFeed.Project` calls `TranscriptChat.Project` per stream event and drops a result whose `Envelopes` and `SubmittedInputs` are both empty. Claude and Codex are the only `IChatDisplayRules` implementers.
- `ChatTabViewModel.Apply` guards on the feed generation, appends rows per poll, pairs `tool_call` with `tool_result` by id to flip a `ToolCallItem` from running to done, and clears everything on a feed `Reset`. `OnSession` can deliver a session's status before the first asynchronous transcript read lands. `ChatSessionInfo.Ended` is the lane's verdict that nothing more will arrive — it folds in a terminal status, and on the remote lane a row removal, which can recover. `ToolSummary` maps `Agent`/`Task` to `ToolCategory.Agent` for the row's phrasing only; `subagent_type` is never read.
- The strip above the composer is `QueuedMessagesBanner`: a summary line over a capped list, in its own `Auto` row between the activity note and the composer card.
- The right-hand work-context pane is one view model per workspace. Its SESSION section is fed by the agent's `AgentStatusDto`, not by the server read, so it renders whatever phase the pane is in. Sections are a `Button.sectionHeader` with a chevron, an eyebrow and a count, over an `ItemsControl`; a section with no rows is hidden whole. `RemoteSessionView` hosts the chat without this pane.
- **The verdict.** `AgentActivityClock.AwaitingInput` is set by an ACP turn's falling edge or, for a PTY vendor, by the hook relay. For Claude only `Stop` and `PreToolUse` run `kcap hook`, so the relay that fires in an installed plugin is `stop → true` and `pre-tool-use → false`; `ClaudeHookCommand` also maps `user-prompt-submit → false`, but the plugin registers only a title script for that event, so nothing sends it. A delivered input clears the flag daemon-side through `ClearAwaitingInputSince`, fenced by `WaitGeneration`. The relay runs only for the parent (`agent_id` absent), and `ClaudeHookInputWaitRelayTests` pins why: a background subagent working on after the parent asked the user something must not clear the parent's wait. `AgentOrchestrator.LocalIpc` publishes `Status == "Running" && clock.AwaitingInput` on `AgentStatusDto`; the clock's `OnAwaitingInputChanged` pulses the status notifier; nothing else reads the flag. `AgentStatusDto` grows by trailing nullable members whose null means an older daemon — `AwaitingInput` and `TranscriptFormat` arrived that way. The server's agent row carries no verdict, so a remote session's `AwaitingInput` is null and it never shows a working note.
- The rail row pulses its status dot only while starting, shows a "zzz" badge and a "waiting for input" tooltip while `WaitsOnUser`, and raises `NeedsYou` from `NeedsAttention` or a pending permission card. Permission and question cards raise attention on their own lane, whatever the verdict says.
- **Subagent hooks.** `SubagentStart` and `SubagentStop` run `kcap hook --claude` asynchronously with a 5 s timeout; they spawn and drain the per-subagent watcher and post to the server. They never reach the daemon. Both fire for background subagents: a background agent's watcher was spawned at launch and drained when the agent finished, about 5 s before the parent's task-notification line. A subagent's own tool calls run the same hook with `agent_id` set. Being asynchronous, a start can be delivered after its own stop.
- `AgentActivityClock` is created before launch admission, on paths that can fail without ever producing an `AgentInstance`, and `AgentInstance` allocates a default one that the launch clock replaces. It owns no resources today.

## Decisions

### D1 — Scope: Claude sessions; display on both lanes, the live count on the local lane

The display half works wherever Claude's rules run, which is the local and the remote lane alike. The daemon half covers sessions this machine hosts. Other vendors' subagents (Codex collab, Gemini, OpenCode, Cursor) are out: each needs its own launch and finish evidence, and the shapes below are vendor-neutral so they can be added without touching the app.

### D2 — The leaf writes the tool-result shape the server already persists

`ClaudeTranscriptEvents` copies a tool-result line's root `toolUseResult`, when it is an object, into the `claude_code` extension of that line's `ToolResultReceived` as `tool_use_result` (`ClaudeCodeExtension.ToolUseResult`) — the key and the content the server's normalizer writes. Additive, absent when the line has none, and outside the id derivation. This is the `tool_use_result` part of #775, delivered here because this feature is its first reader; `output_raw` stays with #775.

**Only when the line has exactly one `tool_result` block.** The root object describes one result and names none, so on a line with several it cannot be attributed; the leaf then writes it on none of them. A background launch on such a line reads as finished at launch, which is what every launch reads as today. The server writes the object on the first result of such a line; that difference is recorded for #775 to settle, and the rules below tolerate it.

One shape on both lanes means one reader in the rules. A flag of this repo's own would exist on the local lane only, since nothing this repo projects reaches the server's stream.

The tool call's input is not the source: `run_in_background` is optional, and a harness that backgrounds every subagent sends none. The result's status is what Claude Code actually did.

### D3 — Subagent facts ride beside the rows, not on the envelope

`ChatProjectionResult` gains a third member, `Subagents`, a list of `SubagentSignal`:

- `Started(CallId, Name, Description, At)` — a tool call that spawns a subagent.
- `Detached(CallId, AgentId)` — that call's result was only a launch acknowledgement; the agent id it names is the subagent's handle from then on.
- `Finished(CallId?, AgentId?, Outcome, At)` — the subagent ended outside its tool result; `Outcome` is `Done`, `Failed` or `Stopped`, and at least one key is set.

`IChatDisplayRules` gains `Subagents(CanonicalEvent evt, AcpEventEnvelope raw)` with a default of none, so Codex's rules yield none. It is called for every envelope of the event whatever `Filter` decides: a signal can come from a row the chat hides. `ClaudeChatRules` implements it. A sidechain event yields nothing — those are a subagent's own records, and a nested subagent is not the parent's row. The meta flag is not consulted. Otherwise:

- `Started` for an `Agent` or `Task` tool call, with `subagent_type` as the name (`agent` when absent) and `description`.
- `Detached` for a tool result whose `tool_use_result.status` is `async_launched`, with its `agentId`.
- `Finished` for a task-notification, keyed by `<tool-use-id>` and `<task-id>`; `Failed` when `<status>` is anything but `completed`.
- `Finished` with `Stopped` for a tool result whose `tool_use_result` has `task_type: "local_agent"`, a `task_id`, and a message opening "Successfully stopped task", keyed by that agent id. The message gate is what keeps a `TaskGet` or `TaskOutput` probe from ending a running subagent; it is the server's rule, kept word for word.

A task-notification is one whose extension says `origin_kind: "task-notification"` **or** whose text opens with `<task-notification>`. One predicate serves `Filter`, `SubmittedInput` and `Subagents`, so the remote lane — whose events carry no `origin_kind` — gets the `SystemNote` row and the finish signal it lacks today. The notification still becomes its `SystemNote` row, with one exception that follows from the order of `Filter`'s checks, which does not change: a notification marked meta is dropped as a row before its text is looked at, so it yields its `Finished` signal, no row and no submitted input.

Every producer of a `ChatProjectionResult` carries the member: `TranscriptChat.Project`, `TranscriptChatProjection.ProjectWithInputs`, and the default `IChatTranscriptProjection.ProjectWithInputs`, which yields none and so gives the journal projection its behaviour. `RemoteTranscriptFeed.Project` keeps a result that has signals even when it has no rows and no inputs.

`SubagentStarted` and `SubagentCompleted` on the remote stream are not read. They carry an agent type but no description and no call id, the stop hook behind them is the signal known to get lost, and everything they say is derivable from the tool calls on the same stream.

`AcpEventEnvelope` is a wire contract mirrored on the server, with one field group per kind and a per-field wire-compat guard; a display fact that only the desktop reads does not belong on it. The side-band is the existing place for such facts, and keeps "which tool names spawn a subagent" in the vendor's rules instead of an app-side name table.

### D4 — One tracker per workspace: `SessionSubagents`

A plain class in `Capacitor.App/ViewModels`, fed by `ChatTabViewModel.Apply` — after its generation guard — with each `ChatProjectionResult`, and exposing an `IAvaloniaReadOnlyList<SubagentRow>` plus `RunningCount`. Within one result it applies the signals first, then the envelopes:

- `Started` adds a running row keyed by its call id, in arrival order. A call id already tracked is ignored.
- `Detached` applies only to a **running** row with that call id: it marks the row background and binds the agent id to it. An agent id binds to one row at a time and the latest such `Detached` wins, so a second `Agent` call that resumes an ended agent is a second row and the id follows it. A `Detached` for an ended or unknown call — a repeat, or an old one delivered after the second launch — changes nothing and rebinds nothing.
- A `tool_result` envelope for a tracked, running call finishes the row — `Failed` when `ToolIsError`, else `Done` — unless **the same projection result** carries `Detached` for that call id. The exemption covers the launch acknowledgement and nothing after it: a later result for the same call, an error included, finishes the row. Keyed by call id, it reads the same whether a result arrives with its line's other events or alone.
- `Finished` resolves in this order, and an ended row is never reopened or re-ended:
  1. A call id the tracker knows decides alone: it ends that row if it is running, and otherwise does nothing. It never falls through to the agent id — a repeated notification for the first execution must not end a second one that now holds the same agent id.
  2. With no call id, or one the tracker has never seen, the agent id selects the running row it is bound to — provided the signal is not dated before that row started. A completion older than the row belongs to an earlier execution.
  3. Otherwise nothing. The notification of an agent resumed through `SendMessage`, which starts no row, lands here or in 1.
- A feed `Reset` or `SwitchFeed` clears the rows and bindings, so state always rebuilds from the log and cannot leak.

**Session end is a view of the rows, not a transition.** The tracker holds `SessionOver`, which `OnSession` sets from `ChatSessionInfo.Ended` every time it runs. While it is true, a row whose own state is `Running` presents as `Stopped` and `RunningCount` is zero. The rows' own states are untouched: rows projected after the session ended — a first read or a reset that lands late — present as stopped at once; a real `Finished` in the final drain still settles its row as `Done` or `Failed`; and a remote row that reappears turns `SessionOver` false and its running rows read as running again. A row stopped this way shows no duration, since nothing dates its end.

`SubagentRow` is a `ReactiveObject`: `Name`, `Description`, `IsBackground`, `State` (`Running`, `Done`, `Failed`, `Stopped`), `StateText`. Elapsed runs from the spawning event's timestamp to the finishing signal's or result's, and for a running row to `TimeProvider.GetUtcNow()`, refreshed on the chat's existing poll tick.

`WorkspaceViewModel` creates the tracker and hands the same instance to the chat tab and to the work-context pane. `RemoteSessionViewModel` creates one for its chat tab alone.

### D5 — The strip

A new `Auto` row in `ChatTabView` between the activity note and `QueuedMessagesBanner`: a `Border` named `SubagentsBanner` in the queued banner's chrome, holding the pulsing `toolRunning` dot and `SubagentSummary` — "1 subagent running" / "N subagents running". Visible while `RunningCount > 0`. One line however many run; the names are the sidebar's job. On the remote session view, which has no sidebar, the strip is the whole feature.

### D6 — The sidebar section

`WorkContextView` gains a SUBAGENTS section directly above SESSION, below the divider — with the other session-local facts, so it renders in every pane phase. Header count: "N running · M total", or "M total" when none run. Rows: a status dot (pulsing warning while running, success when done, danger when failed, neutral when stopped), the name with a faint "background" tag, the state text right-aligned ("running · 6m 18s", "2m 41s", "failed · 48s" in danger, "stopped · 1m 02s", or a bare "stopped" when the session ended under it), and the description on a second, ellipsised line. The word carries the state as well as the dot does, so a row never leans on colour — the rule the web's agents card follows. Hidden when the session has spawned none. It starts expanded; the toggle is the user's from then on and is not persisted, like the pane's other sections. Rows are not interactive.

### D7 — The daemon reports live subagents beside the verdict, and never instead of it

Hooks cannot tell "I will wait for my agents" from "I asked you something": both are the parent's `Stop`. So `AwaitingInput` keeps its meaning exactly — the parent finished a turn and nothing has been handed to it — and the wait, the "zzz" badge, `NeedsYou` and the tray's attention all behave as they do today. A question the parent asks while its agents work is never hidden.

What the daemon adds is a second fact. `AgentStatusDto` gains a trailing `int? LiveSubagents`: the number of subagents the daemon currently believes are running. **Null means the daemon has heard nothing about subagents for this agent** — an older daemon, a vendor whose hooks report none, or a Claude session that has spawned none yet. From the agent's first report onward it is a number: the clock's count while the agent is `Running`, zero in any other status. The rule is evidence, not a vendor table, so the daemon needs no list of vendors that support it. Like every member of the status payload it is always emitted, as JSON null when absent; an older client ignores it. It rides the existing status frame — no new `FrameType`, and no `LocalControlCapabilities` entry, because nothing new is handled.

The app reads "busy" from the two together, through one predicate, `SessionStatusDots.IsWorking`: `Status == "Running"` and (`AwaitingInput == false` or `LiveSubagents > 0`). Null counts as zero.

- **Chat pane.** `RefreshActivityNote` takes `IsWorking` as its in-turn test, so "Working for …" stays on, and keeps counting, while only subagents run. `ChatSessionInfo` carries the count from `FromLocal`. The pending-card pause stays as it is and applies to background work too: while a permission or question card is up the note hides and its clock pauses, whatever the count. The note says the session is getting on without the user; with a card up, something is blocked on them, and the note cannot know whether the asker is the parent or one of the subagents, whose tool calls raise cards on the same lane. The strip keeps showing what runs.
- **Rail.** `AgentRow` carries the count from `FromLocal`. While it is above zero the row's status dot pulses, its meta line gains "N subagents", and its tooltip gains "N subagents running". Badges are untouched, so a session can read as both waiting on the user and busy — which is what it is.
- **Remote rows** carry null and are unchanged.

### D8 — How the daemon counts

**Relay.** At the top of `ClaudeHookCommand.HandleWithDeps`, beside the input-wait relay and ahead of every gate: a hook with `agent_id` set posts `{ session_id, agent_id: <hosted id>, cwd, subagent_id, live, sent_at }` to `/{token}/claude/subagent`, with `live = command != "subagent-stop"` and `sent_at` the hook's own UTC clock in milliseconds when it builds the message. `SubagentStart` and every subagent tool call therefore report the subagent alive; `SubagentStop` reports it gone. It never posts `input-wait`: a subagent's hook still must not touch the parent's wait. Same 1 s cap, budget clamp and best-effort contract as `DaemonInputWaitRelay`, which becomes one relay with two messages rather than gaining a twin.

**Bridge.** `LocalPermissionBridge` routes the new path like `input-wait`: shared-token check (a reviewer token is refused: the ladder trusts the body's ids), capped body, attribution through `AttributeHandler`, then `SubagentHandler(agentId, subagentId, live, sentAt)` and a 204; a body without a usable `sent_at` is stamped with the bridge's own clock on arrival; `claude` is the only vendor it accepts. An older daemon answers 404 and the relay swallows it; an older hook sends nothing and the count stays null or zero.

**Clock.** `AgentActivityClock` keeps, under its existing gate and per subagent id: whether it is live, the monotonic time it was last reported, and the `sent_at` of the latest report applied to it.

- **A live report says "alive now", so an old one says nothing.** `HandleSubagent` drops one whose `sent_at` is more than **60 seconds** behind the orchestrator's `GetUtcNow()` when it is handled, before it reaches the clock — the clock itself reads no wall time, as its own rule demands, and only compares the stamps it is given. The bridge runs its handlers independently, so a report admitted early can be processed late; this is what keeps a live report held across its own stop dead however long it was held. Stops are facts rather than heartbeats and are not aged out.
- **Within that minute, reports apply in the order the hooks sent them, not the order the bridge ran them.** A report stamped earlier than the latest one already applied to its id is dropped: a live report overtaken by its own stop stays dead, and a stop overtaken by a later live report does not end the resumed run.
- `SubagentSeen(id, sentAt)` adds or refreshes the id — unless the id is stopped and `sentAt` falls within **30 seconds** after that stop's `sent_at`. The stamp is taken when a hook process runs, not when Claude Code fired its event, and the two hooks are asynchronous: a start whose process was scheduled after its own stop carries the later stamp and would otherwise read as a resume. The window is a judgement about how far apart two such processes can be scheduled, not a bound the platform gives. Past it the id counts again, which is a resumed agent reporting under the id it had before.
- `SubagentStopped(id, sentAt)` marks the id stopped. A stop for an id already stopped changes nothing, the stamp the window runs from included.
- All three comparisons read wall clocks, which hooks and daemon share by running on one machine, so a clock step can mislead them. The stamp a report leaves is therefore honoured only for **10 minutes of the daemon's monotonic time** after that report arrived; past that, the next report for the id is taken on its freshness alone. A backward step thus costs at most 10 minutes of rejected reports for a resumed subagent; a forward one drops the live reports sent before it and handled after it, and can leave a stale count that the liveness window removes. Neither outlasts 10 minutes.
- **The count changes only through events, never by the passing of time alone.** `LiveSubagents` is null until the first report of either kind, then the number of ids in the live set — no age filter at read time. An id enters the set through `SubagentSeen`, and leaves it through `SubagentStopped` or through being retired for age; each of the three is reported to the orchestrator, which announces it. A value that moved by itself as time passed could change between a published snapshot and the report that settles the id, and that report would then see nothing to announce: a stop for an id that had aged out unannounced would leave the app showing it forever.
- `TakeSubagentExpiries()` is the only place an id is retired for age. At one instant under the gate it removes every live id not reported for **10 minutes**, drops stop records past their retention, and returns whether it removed any live id together with the time until the oldest remaining one falls due, or none. The verdict on each id and the next deadline come from the same instant, so an id is either reported as expired by exactly one call or counted in the deadline that call returns; none can cross unobserved between two readings of the time.
- `SubagentSeen` and `SubagentStopped` return whether the live set changed — an id added, an id removed — or the first report's null-to-number step happened. Renewing an id that has aged but has not been retired yet changes nothing: it was still counted, and the renewal only moves its deadline. Stopping such an id removes it and reports the change, which is what publishes its end. They raise no callback: the orchestrator has to reschedule between taking a report and announcing it, so it sequences the pulse itself. A heartbeat that only renews an id changes no count, but it does move a deadline, which is why rescheduling follows every report rather than every change.
- The two sides never touch: subagent reports leave `AwaitingInput`, `WaitGeneration`, `ActivitySeq` and `IdleForMs` alone, and no input or turn signal alters the subagent sets.
- The clock stays a passive state machine that owns nothing, so clocks created on launch paths that fail, and the default one an `AgentInstance` allocates, need no cleanup.

**Orchestrator.** The published count follows D7's rule. One timer, owned by the orchestrator and shared by every hosted agent, covers expiry, and one method, `RescheduleSubagentExpiry(announce)`, is the only code that touches the timer and the only code that pulses status for subagents. In one hold of a lock of its own, in this order, it:

1. returns if disposal has begun;
2. calls `TakeSubagentExpiries()` on **every** agent's clock, whatever the agent's status;
3. sets the timer to the earliest deadline returned, or rests it;
4. pulses status if `announce` is set or any clock retired an id in step 2.

`HandleSubagent` applies a report to the attributed agent's clock and then calls it with `announce` set to whether the clock said the value changed. The timer's callback calls it with `announce` unset.

- **Every expiry gets published, whoever consumes it.** The timer is shared, so the call that finds an id expired is not always the callback: a heartbeat from a still-live subagent in one session can reschedule before the callback for another session's expiry has been dispatched. Because retiring an id is an event the clock reports, exactly once, to whichever call meets it, that call announces it — however the deadline fell relative to the call's own steps — and renewing one session can never postpone the news about another.
- **Arm before announcing.** An id retired in step 2 is absent from the snapshot the pulse causes; an id that survived step 2 is inside the deadline step 3 arms, however long the call was delayed. Pulsing first would let a subscriber snapshot a second id as live, and a call delayed past that id's deadline would then find nothing to arm and nothing left to announce it. A callback that fires a moment early retires nothing, announces nothing, and arms the remainder.
- **Every mutation is followed by a call**, and steps 2 and 3 are one step under the lock, so the last call to run has seen the latest state: a call that computed "nothing to wait for" cannot overwrite the deadline of a report that raced it, since that report's own call runs after it. Visiting every agent is what makes a report received while an agent is still `Starting` expire on time once it runs.
- `DaemonStatusNotifier.Pulse` only bumps a generation and releases its waiters asynchronously, so holding the lock across it re-enters nothing.

`DisposeAsync` takes the same lock to mark disposal and dispose the timer. From the moment it releases the lock nothing here publishes or re-arms: a call already entered either finished before disposal got the lock, or acquires it afterwards and returns at step 1.

The orchestrator takes a `TimeProvider`, defaulting to `TimeProvider.System`, uses it for this timer, and hands it to every clock `CreateActivityClock` builds, so a test drives both from one `FakeTimeProvider`.

**Why a window.** A `SubagentStop` is not guaranteed: the hooks are asynchronous on a 5 s timeout, and a background agent killed through `TaskStop` never fires one. Without a bound the session would show activity for the rest of its life. With one, a lost stop costs at most 10 minutes of a stale "N subagents". The window is a judgement, not a measurement: a subagent that reports nothing for longer — one tool call running past it — drops out of the count while it still runs, and reappears with its next tool call.

### D9 — What this deliberately does not do

- No entering a subagent's own session from the app; rows are not links. That is #988.
- No sidechain rows in the parent's chat; the subagent's own turns stay hidden.
- No subagent support for other vendors.
- No change to what `AwaitingInput`, the wait badge or attention mean.
- No change to `AcpEventEnvelope`, `AcpToolKind`, the local-control frames or capabilities.
- No live count for remote sessions; the server row carries none.
- No model of a resumed agent as one continuing execution: a resume through a new `Agent` call is a new row, and one through `SendMessage` is not shown. The daemon counts a resumed agent again once it reports more than 30 seconds after its stop.

## Component changes (implementation map)

**`src/Capacitor.Models.Transcripts/Harness/Claude/`**
- `ClaudeCodeExtension.cs` — the `ToolUseResult` key.
- `ClaudeTranscriptEvents.cs` — carries a single-result line's `toolUseResult` object into the extension.

**`src/Capacitor.Cli.Core/`**
- `SubagentSignal.cs` — the three signals (one closely-related hierarchy, one file); `SubagentOutcome.cs`.
- `ChatProjectionResult.cs` — the `Subagents` member.
- `IChatTranscriptProjection.cs` — the default `ProjectWithInputs` builds the three-member result with no signals.
- `TranscriptChat.cs` — `IChatDisplayRules.Subagents`; `TranscriptChat.Project` and `TranscriptChatProjection.ProjectWithInputs` collect it.
- `Harness/Claude/ClaudeChatRules.cs` — the four derivations; the task-notification predicate; `<tool-use-id>`, `<task-id>` and `<status>` readers.
- `LocalIpc/StatusIpc.cs` — `AgentStatusDto.LiveSubagents`.

**`src/Capacitor.App/`**
- `ViewModels/SessionSubagents.cs`, `ViewModels/SubagentRow.cs`, `ViewModels/SubagentState.cs` — new.
- `ViewModels/ChatTabViewModel.cs` — takes the tracker; feeds it in `Apply`; clears it on reset and feed switch; sets `SessionOver` in `OnSession`; ticks elapsed; `HasRunningSubagents`, `SubagentSummary`; `IsWorking` in `RefreshActivityNote`.
- `ViewModels/RemoteTranscriptFeed.cs` — keeps a signal-only result.
- `Views/ChatTabView.axaml` — the `SubagentsBanner` row.
- `ViewModels/WorkContextViewModel*.cs`, `Views/WorkContextView.axaml` — the section, header text, toggle.
- `ViewModels/WorkspaceViewModel.cs`, `ViewModels/RemoteSessionViewModel.cs` — create and share the tracker.
- `ViewModels/SessionStatusDots.cs` — `IsWorking`. `ViewModels/ChatSessionInfo.cs`, `Services/AgentRow.cs` — carry the count.
- `ViewModels/RailSessionViewModel.cs`, `Views/SessionRailView.axaml` — the pulsing dot, meta and tooltip.

**`src/Capacitor.Cli/Commands/`**
- `DaemonInputWaitRelay.cs` — becomes the daemon hint relay with both messages; Claude's and Codex's input-wait callers move with it.
- `Harness/ClaudeHookCommand.cs` — the subagent relay call.

**`src/Capacitor.Cli.Daemon/Services/`**
- `LocalPermissionBridge.cs` — the `/subagent` route and `SubagentHandler`.
- `AgentActivityClock.cs` — the per-id subagent state, `SubagentSeen`, `SubagentStopped`, `LiveSubagents`, `TakeSubagentExpiries`.
- `AgentOrchestrator.cs`, `AgentOrchestrator.LocalIpc.cs` — the `TimeProvider` parameter, the handler, `RescheduleSubagentExpiry` with its timer and lock, disposal, the published count.

**Docs** — `docs/CHANGES.md` entry; the README's desktop section where it describes the chat pane and the rail; this spec.

## Delivery

Two kcap-cli PRs against #966, each useful alone:

1. **Display** — D2–D6. "Part of #966". Carries this spec.
2. **Live count** — D7–D8. "Closes #966".

No server change is needed. The remote lane reads what kcap-server already persists — `tool_use_result` on tool results, the notification's text — so it works for sessions already ingested as well as new ones.

## Behavior notes and accepted limitations

- Rows name a subagent by `subagent_type`. Two subagents of the same type differ only by description.
- The desktop shows failed and stopped subagents; the web shows only running or finished, because it reads the completion event, whose outcome nothing populates.
- The desktop ends a row on transcript evidence alone. When the transcript carries none — no terminal result, no notification, no `TaskStop` result — the row runs until the session ends, while the web can close the same subagent from the completion event or its 15-minute quiet reaper. The two agree on what is running whenever the transcript records the end, which is the case the evidence supports; the desktop adds no reaper of its own, since one would also end a quiet subagent that is still working.
- A subagent resumed within 30 seconds of its stop is not counted by the daemon until a report stamped after that window. Conversely, a start hook whose process ran more than 30 seconds after its own stop hook's would be counted as a resume, for at most the 10-minute window.
- The strip and the rail answer from different evidence — the transcript and the hooks — and can disagree for a while: a killed background agent leaves the strip at once, from the `TaskStop` result, but stays in the daemon's count until the window drops it, since no hook reports its end.
- A parent that stops with agents still running reads as waiting on the user and busy at once, and raises attention as it does today, whether or not it asked anything.
- A local session hosted by an older daemon gets the strip and the section, and no count.
- On the remote lane the server writes the root `toolUseResult` on the first result of a multi-result line. If that line held two `Agent` results, the first would be read as the detached one. Not observed; accepted.
- After the last background subagent ends and before the parent's notification turn calls its first tool, the session reads as waiting and idle — as it does today from the parent's `Stop` onward. Nothing reports the start of a turn that a system-sourced prompt begins.

## Testing

- **`ClaudeTranscriptEventsTests`** (leaf): a single-result line's `toolUseResult` object rides the extension as `tool_use_result`, unchanged; a line with none, or with a non-object value, adds no key; a multi-result line puts it on no result; the event ids equal the ids without the change.
- **`ClaudeChatRulesTests`**: `Started` for `Agent` and for `Task`, with name and description, and `agent` when `subagent_type` is absent; none for a sidechain call, a sidechain notification or any other tool; `Detached` with the agent id only for an `async_launched` result; `Finished` from a notification keyed by `<tool-use-id>` and `<task-id>`, failed for a non-`completed` status; `Finished`/`Stopped` from a `TaskStop` success result and none from a `TaskOutput` result carrying the same `task_id`. Each notification case runs twice — identified by `origin_kind`, and by text alone as the server's events present it — and both yield the `SystemNote` and no submitted input. A notification marked meta yields its `Finished` signal, no row and no submitted input. `CodexChatRulesTests`: no signals.
- **`TranscriptChatCanonicalTests`**: signals pass through `Project` and `ProjectWithInputs`; a hidden row still yields its signal; rules without an override yield none; the default `IChatTranscriptProjection.ProjectWithInputs`, and through it the journal projection, yields none.
- **`SessionSubagentsTests`** (new): foreground start→result; background start→detached result→notification; failed result; failed notification; a background row stopped by agent id; duplicate `Started`, `Detached` and `Finished`; a signal for an unknown id; the second-launch sequence `Started(c1) → Detached(c1,a) → Finished(c1,a) → Started(c2) → Detached(c2,a) → Finished(c2,a)`, and its variant finished by agent id alone; after that second launch, a repeated `Finished(c1,a)` leaves c2 running, a delayed `Detached(c1,a)` leaves the binding on c2, and an agent-id-only `Finished` dated before c2 started leaves c2 running; a detached launch followed by a later terminal result for the same call finishes the row, and by an error result fails it; two `Agent` results in one projection result, one detached and one not; reset clears; `SessionOver` presents running rows as stopped with no duration, a real `Finished` under it still settles the row, rows added under it present as stopped, and clearing it restores them; `RunningCount` and order; elapsed under a `FakeTimeProvider`.
- **`ChatTabViewModelTests`**: fixture transcripts drive `HasRunningSubagents` and `SubagentSummary` through the real local feed, singular and plural; a feed reset empties the strip; a terminal status delivered before the first read, and a reset after it, leave no running strip; a stale-generation read after a feed switch adds no rows; "Working for …" holds while the parent waits with `LiveSubagents > 0` and clears at zero and at null; with a pending card up, `AwaitingInput` true and a positive count, the note hides, its elapsed time stops, and it resumes from the paused total when the card clears — beside the existing pause test, which stays. **`ChatTabViewSmokeTests`**: `SubagentsBanner` is hidden at zero and shows the summary while one runs, above `QueuedMessagesBanner`.
- **`WorkContextViewModelTests`** / **`WorkContextViewSmokeTests`**: the section is hidden with no rows; header text for running and for all-finished; it renders while the pane's server read is loading or failed; the toggle folds it.
- **`RemoteSessionViewModelTests`** / **`RemoteTranscriptFeedTests`**: canonical events shaped as the server writes them — `tool_use_result` on the result, a notification with no `origin_kind` — drive the strip through a background launch and its finish; a signal-only projection — a notification marked meta, which yields no row — survives the feed and finishes its subagent; a row removal stops the strip and its reappearance restores it.
- **`RailSessionViewModelTests`** / **`SessionStatusDotsTests`** (new): `IsWorking` across the verdict and count combinations, null included; the rail row pulses and names the count while it is above zero, keeps its wait badge and `NeedsYou` beside it, and is unchanged for a remote row.
- **`ClaudeHookSubagentRelayTests`** (new, beside `ClaudeHookInputWaitRelayTests`): a hook with `agent_id` relays `live: true` with its `sent_at`, `subagent-stop` relays `live: false`, a parent hook relays no subagent message and still relays input-wait; no relay without a hosted loopback bridge or with the budget spent. `A_subagents_tool_call_relays_nothing` narrows to what it protects: a subagent's tool call posts no `input-wait`. `CodexHookInputWaitRelayTests` stays green over the merged relay.
- **`LocalPermissionBridgeSubagentTests`** (new, beside `LocalPermissionBridgeInputWaitTests`): the route's 204, 400, 404 (bad token, other vendor) and 413 arms; the handler receives the attributed agent id and the body's `sent_at`, or the arrival time when the body has none; with `BeforeHandlerRunsForTest` holding an admitted live report across its own stop, the subagent stays stopped on both sides of the 10-minute boundary — released after 45 seconds it is dropped as overtaken by its stop, and released after 11 minutes, once the stop's stamp is no longer honoured, it is dropped as stale.
- **`StatusIpcJsonTests`**: the pinned payload gains `"live_subagents"` last in each agent, emitted as `null`, as `0` and as a positive number; a payload without the member deserializes to null; the unknown-member test stays as the guard for older clients.
- **`AgentActivityClockTests`** (fake time): null before any report, a number after the first of either kind; seen, refreshed, stopped; a live report stamped before the latest applied one is dropped, and so is such a stop; a start stamped within 30 seconds after its stop is ignored and one stamped later counts again; a duplicate stop does not move the window; a report with no stamp is ordered by its arrival; after a stop, with the wall clock stepped back by more than 10 minutes, live reports for the resumed id are rejected until 10 monotonic minutes have passed since the stop arrived and are counted from then on; `LiveSubagents` does not move with time alone — an id unreported for more than 10 minutes is still counted until it is retired; `TakeSubagentExpiries` retires each aged id exactly once, reports it, returns the time to the oldest survivor, and a second call reports nothing; a heartbeat renews an id's deadline; a heartbeat for an aged id not yet retired keeps it counted and reports no change; a stop for an aged id not yet retired removes it and reports the change; `SubagentSeen` and `SubagentStopped` report an added or removed id and report none for a plain renewal or a repeated stop; the parent stopping and asking for input while a child keeps reporting leaves `AwaitingInput` true with the child counted; subagent reports leave `WaitGeneration`, `ActivitySeq` and `IdleForMs` alone, and a stale `ClearAwaitingInputSince` leaves the sets alone.
- **`AgentOrchestratorSubagentTests`** (beside `AgentOrchestratorInputWaitTests`; one `FakeTimeProvider` for the orchestrator and its clocks): a relayed report reaches the right agent's clock and pulses status on a changed value, and on an unchanged one only when its call retired an expired id; a live report stamped more than 60 seconds before the orchestrator's clock never reaches the agent's clock, and a stop that old still does; the published count is null before any report, the clock's count while running and zero otherwise; the timer pulses at the expiry and re-arms for the next; with two ids a minute apart and the first callback held past the second deadline, the status it then publishes counts neither, and with the callback not held the second deadline still fires its own pulse; with two hosted sessions and the callback not yet dispatched, an unchanged heartbeat from B publishes A's zero both when A's last subagent passed its deadline before B's call began and when it passes after the call has begun but before the call reaches A's clock, and A's zero stays published however often B renews afterwards; with a count of one published, the expiry callback held and more than 10 minutes gone, the subagent's stop alone publishes zero, and the callback released afterwards publishes nothing more; a report that lands while the callback is rescheduling still has its expiry fire; a report taken while `Starting` expires on time after the agent turns `Running`; a callback held across disposal publishes nothing once disposal has released the lock, and nothing re-arms.

## Out of scope

Entering a subagent session from the app, sidechain rows, other vendors' subagents, and any live count for remote sessions.
