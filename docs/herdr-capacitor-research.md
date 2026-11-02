# HerdR and Warp research for Capacitor

Research checked 2026-09-18 against official [HerdR documentation](https://herdr.dev/docs/) and local source: HerdR `7df919d00e5bf8f6ed43a1781e81340cc0bbce8d`, Capacitor `fdc3bcbfab94aa1ee306503440cec2979fcd50fb`. The recommendations below are inferences from those implementations, not claims that HerdR is uniformly ahead. Source links identify the inspected files and lines; public documentation can advance independently of the checkout. These are proposals for discussion, not an approved implementation specification. HerdR behavior was inspected in source; Warp's installed control CLI was exercised through read-only commands. No live attachment, agent input, or handoff experiment was performed.

The useful transferable ideas concern owning terminals and making their agent state actionable. Capacitor already has daemon-hosted runtimes, terminal attachment, structured protocol runtimes, permission handling, activity supervision, remote session attention, and capability negotiation. A plugin marketplace is not a prerequisite for any recommendation here.


## Proposal shortlist

The recommended order starts with improvements to existing Capacitor sessions. Managed shell hosting and live daemon replacement require separate design work.

| Priority | Proposal | Surfaces | Review focus |
| --- | --- | --- | --- |
| First | Explicit observer/controller roles and a Take control action | Daemon, CLI, app | One input and resize policy across all clients |
| First | Explain status with its authority, evidence, and freshness | Daemon, CLI, app | Reuse runtime and hook evidence; expose uncertainty |
| First | Next needing attention and unseen-completion navigation | App | Reuse attention tracking; preserve stable project ordering |
| Next | Readiness-aware prompt and wait commands with JSON outcomes | Daemon, CLI | Prefer structured turn IDs; distinguish delivery from completion |
| Explore | An opt-in managed shell, provisionally `kcap shell` | Daemon, CLI, app | Separate terminal lifetime from agent/session lifetime |
| Explore | Daemon-owned terminal-state checkpoints for reattachment | Daemon, app, CLI | Correct modes and screen state after retained output is truncated |
| Explore | Open the original Warp or iTerm2 session | Daemon, app | Explicit session correlation and host capability checks |
| Later | Live daemon replacement without stopping hosted processes | Daemon | Containment, ownership transfer, runtime protocols, and rollback |
| Hardening | Published compatibility floor and historical wire fixtures | Daemon, CLI, app | Extend existing capability negotiation |

A generic takeover of arbitrary external terminals is not proposed. Host-specific integrations can cooperate with an existing terminal owner; managed terminals can support reattachment because Capacitor owns them from launch. Warp Control currently supports discovery and navigation, but does not expose the terminal read/write surface needed for embedded interactive attachment.

## Terminal attachment, shell hosting, and process lifetime

These findings come from source inspection, not a live HerdR handoff test. HerdR checkout: `7df919d00e5bf8f6ed43a1781e81340cc0bbce8d`; docs checked 2026-09-18.

### What “attach” actually means

HerdR allocates a PTY and starts a child on its slave; it retains the master. Direct attach resolves a terminal already in its own registry, then connects a client to that runtime. A missing terminal ID fails. There is no arbitrary-PID adoption in this path. The CLI accepts a terminal ID or resolves a managed agent to one. Direct CLI attach is currently Unix-only, despite the broader application supporting Windows.

Sources: [PTY creation](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/pty/backend/unix.rs#L13), [attach validation and ownership](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/server/headless.rs#L1798), [CLI](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/cli.rs#L538), [platform restriction](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/client/startup.rs#L9), [official attach docs](https://herdr.dev/docs/agents/#attach-directly-to-an-agent).

Capacitor already implements the equivalent managed-agent path: `kcap agent start`, detach, and `kcap agent attach`. The daemon looks up an existing AgentInstance, rejects runtimes without terminal output, and attaches a sink. Hook/transcript recording alone does not grant control of the original PTY. See [AgentCommand](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli/Commands/AgentCommand.cs#L72) and [local attach](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs#L368).

“Impossible” needs qualification:

| Situation | What is feasible |
| --- | --- |
| Process launched inside HerdR/Capacitor-owned PTY | Reconnect through its owner; already supported. |
| Arbitrary process launched in an ordinary external terminal | HerdR offers no generic takeover in the inspected implementation. A PID or slave tty path is not the owning terminal's master stream or screen history. |
| Existing tmux session | Integrate with tmux control mode; it provides output notifications, capture-pane, send-keys, and attach-session. |
| Existing iTerm2 session | Its API can focus the session, read screen contents, and send text; iTerm2 retains ownership and lifetime responsibility. |
| Existing process on Linux/FreeBSD | reptyr is an OS-specific exception: it can move a running program to a new terminal, subject to platform and ptrace restrictions. This is not a portable macOS/Windows product foundation. |

Primary sources: [PTY semantics](https://man7.org/linux/man-pages/man7/pty.7.html), [tmux control mode](https://github.com/tmux/tmux/wiki/Control-Mode), [iTerm2 Session API](https://iterm2.com/python-api/session.html), [reptyr README](https://github.com/nelhage/reptyr/blob/master/README.md). The product recommendation is an inference from these boundaries: distinguish “open original terminal”, “interact through an integration”, “attach to managed terminal”, and “resume conversation in a new process”.

### Borrow: opt-in managed shells

HerdR can host a shell and detect agents launched subsequently inside it. A possible `kcap shell` would let people enter a managed shell once, then type normal `claude`, `codex`, or test commands. Future processes become attachable without remembering an agent launcher prefix. It would not adopt commands already running outside that shell.

This requires a terminal lifetime separate from each foreground agent/session lifetime: the shell can outlive several agent runs. Capacitor's current CLI and runtime model are agent-centric. Source: [HerdR shell spawn](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/pane.rs#L2120), [foreground detection and authority](https://herdr.dev/docs/agents/#status-authority), [Capacitor start path](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli/Commands/AgentCommand.cs#L72). Recommendation, not an existing Capacitor command.

### Borrow: observers, an explicit controller, and complete screen state

HerdR separates observer and controller connections; a second direct controller requires explicit takeover. This is a useful pattern for Capacitor's app, CLI, and browser sharing one terminal. Current Capacitor local attaches to ordinary agents can all send input; review/flow agents are read-only. Terminal geometry is clamped across attached clients. Add an observer mode and clear “Take control” action, with one policy applied to every input route.

Sources: [HerdR observe/control](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/server/headless.rs#L1365), [takeover](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/server/headless.rs#L1848), [Capacitor input loop](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs#L413), [geometry clamp](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs#L544).

HerdR also maintains a terminal emulator in the runtime and renders the current screen to attach/observe clients. Capacitor instead replays a retained tail of raw bytes (nominally 2 MiB) into a new client terminal. A daemon-owned terminal-state checkpoint could improve reconnect fidelity: a byte tail can lose earlier mode, cursor, or screen-setting sequences. This is an architectural risk inferred from the implementation, not a reproduced rendering defect. Screen emulation also supplies a stable basis for optional fallback detection; structured harness events should remain authoritative where complete.

Sources: [HerdR rendering](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/server/headless/render.rs#L610), [Capacitor buffer](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs#L394), [snapshot replay](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.App/ViewModels/TerminalTabViewModel.cs#L542).

### Borrow later: live daemon replacement

HerdR's experimental Unix handoff pauses PTY I/O, serializes runtime/session metadata, passes already-owned master FDs using SCM_RIGHTS, and commits ownership to the replacement server. Failure before commit has rollback paths. This is cooperative transfer between two HerdR servers, not adoption of someone else's terminal. Client connections, in-flight requests, waits, and subscriptions can be interrupted. The replacement reconstructs emulator state and nudges child redraw; this is not an exact process-memory or unlimited-screen-history checkpoint.

Sources: [handoff orchestration](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/server/headless/lifecycle.rs#L25), [FD transfer](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/server/handoff.rs#L383), [state import](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/pane.rs#L2249), [documented guarantees](https://herdr.dev/docs/session-state/#live-handoff).

Capacitor currently waits until agents/evals are absent before a normal queued restart. Copying only FD passing would be insufficient: Linux children have parent-death SIGKILL armed, and startup orphan handling intentionally reaps prior daemon incarnations. Non-PTY runtimes add protocol/pipe state to preserve. A persistent runtime supervisor or a carefully designed ownership-transfer protocol is a larger design investigation, not a small updater patch.

Sources: [restart busy predicate](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/RestartCoordinator.cs#L60), [restart decision](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/RestartDecision.cs#L18), [Linux parent-death signal](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Native/pty_shim.c#L630), [prior-epoch reaping](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs#L119).


## 1. Explain why an agent is in its current state

**Recommendation:** expose a daemon-owned `agent explain` result and a compact app diagnostic: lifecycle state, reason, authoritative source, last observation, session/process identity, and any rejected or superseded evidence. Keep semantic state separate from titles and presentation labels.

HerdR combines hook authority and fallback detection in one effective-state calculation; hook reports pass source, session, sequence, and process-conflict checks. Its live explain handler reports the active detector and matched evidence, or explicitly says screen detection was skipped because full lifecycle hooks own the state. This is a useful support and automation contract. [State calculation](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/terminal/state.rs#L2156), [report admission](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/terminal/state.rs#L640), [explain handler](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/app/api/agents.rs#L259), [official agent documentation](https://herdr.dev/docs/agents/).

**Capacitor already has:** monotonic activity, turn-in-flight, awaiting-input, launch stages, and generation-aware clearing of turn completion. PTY hooks can set awaiting-input, and structured runtimes attest their own turns. The local status DTO exposes the verdict but not its provenance. [Activity clock](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/AgentActivityClock.cs#L19), [hook relay](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs#L979), [status DTO](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs#L41).

**Caveat:** transfer the explainable authority model, not a second competing truth source. Prefer existing runtime/hook evidence. HerdR itself falls back to idle for known agents when no screen rule matches; that is unsuitable as proof of task completion. [Fallback implementation](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/detect/manifest.rs#L536).

## 2. Separate process liveness from readiness for input

**Recommendation:** expose input readiness independently of `Starting`/`Running`, with reason codes such as initializing, permission pending, ready, and unknown. Use it for scripted prompts and app composer affordances, while retaining deliberate interactive responses to permission dialogs.

HerdR tracks managed startup as pending, blocked, or active. It waits for the expected agent and idle state before declaring startup ready, invalidates readiness when the process disappears or changes, and rejects ordinary agent prompts when a blocker is visible or the expected agent no longer owns the foreground. [Readiness reconciliation](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/terminal/state.rs#L1970), [prompt admission](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/app/api/agents.rs#L146), [official automation guide](https://herdr.dev/docs/agent-automation/).

**Capacitor already has:** structured runtime initialization, bounded input queues, a write acknowledgement boundary, and `WaitForTurnIdleAsync`. For PTYs, `Running` is associated with first output, while structured runtimes have different lifecycle evidence. [Runtime contract](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntime.cs#L41), [input and turn boundaries](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntime.cs#L62).

**Caveat:** readiness does not universally mean idle: some harnesses accept queued prompts while working. Model supported admission explicitly instead of imposing HerdR's terminal heuristics on ACP or Codex app-server. Capacitor's PTY submission already distinguishes approvals-disabled launches from interactive launches and serializes writes; preserve that behavior. [PTY input lane](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/PtyHostedAgentRuntime.cs#L61), [submit policy](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/PtyHostedAgentRuntime.cs#L113).

## 3. Add a small, semantic prompt-and-wait CLI

**Recommendation:** provide JSON-capable prompt and wait commands over the same daemon API used by the app. Distinguish admission, transport write, observed activity, and settled turn; return typed timeout/disappearance outcomes and the observed state. This would make existing hosted agents easier to drive from scripts without embedding sleeps or reading terminal text.

HerdR captures event sequence before submission, verifies terminal/agent identity, waits for evidence that a newly submitted prompt caused activity, then waits for a requested settled state. It reports a stalled prompt separately. [Prompt/wait implementation](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/api/wait.rs#L177). Its documented limitation matters: this does not identify individual turns; when already working, the current turn can satisfy the wait. A timeout also does not prove input was never sent. [Automation semantics](https://herdr.dev/docs/agent-automation/).

**Capacitor already has:** `start`, `ls`, `stop`, and `attach` commands; local text-input IPC; structured prompt delivery; per-agent wait generations that prevent a fast subsequent turn from being cleared by an earlier send. [CLI surface](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli/Commands/AgentCommand.cs#L23), [capabilities](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs#L25), [delivery fence](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs#L4071).

**Caveat:** use structured turn IDs where available, and label weaker PTY observations honestly. Do not treat the activity clock alone as execution acknowledgement: successful input delivery itself advances it. Retries after ambiguous delivery require an explicit policy, not automatic resubmission.

## 4. Turn existing attention signals into a navigation queue

**Recommendation:** add “Next needing attention” and an optional attention filter/sort across local and remote sessions. Separate actionable pending permission/question, failed agent, finished turn awaiting the user, and an unseen completion. Track viewed completion per app client and lifecycle sequence, so opening one client does not silently acknowledge another client's work.

HerdR priority sorting combines attention with state-change sequence. Its client keeps acknowledgements scoped to server boot and pane sequence, and only acknowledges a matching rendered projection while the outer terminal is focused. [Priority sort](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/app/agent_view.rs#L70), [client acknowledgement](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/client/shell/endpoint_agent_state.rs#L13).

**Capacitor already has:** reconciliation of unresolved requests for unopened sessions, waiting-for-user and failure indicators, and local/remote session grouping. Home ordering uses creation time. The incremental opportunity is navigation and viewed-completion semantics. [Attention tracker](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.App/Services/SessionAttentionTracker.cs#L8), [existing attention predicates](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.App/ViewModels/SessionStatusDots.cs#L26), [Home ordering](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.App/ViewModels/HomeViewModel.cs#L311), [grouped rail](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.App/ViewModels/SessionRailViewModel.cs#L96).

**Caveat:** viewing a permission must never resolve it. Preserve Capacitor's deliberate exclusion of flow participants between rounds from “waiting on you.” Avoid constant reordering under the pointer; a filtered queue or next-item command can retain a stable project rail.

## 5. Make mixed-version support a published, tested promise

**Recommendation:** a lower-priority hardening task: define the supported local-control compatibility floor, retain historical released wire fixtures, and show feature-specific unavailable reasons. Avoid implying all app/daemon versions must match when negotiated capabilities suffice.

HerdR separates its stable client endpoint generation from private same-install wire protocols. It names immutable codecs, advertises optional methods/features, and tests frozen generation-one handshakes and snapshots. [Endpoint contract](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/protocol/endpoint.rs#L1), [historical fixtures](https://github.com/herdrdev/herdr/blob/7df919d00e5bf8f6ed43a1781e81340cc0bbce8d/src/protocol/endpoint.rs#L238). Its machine documentation explicitly allows compatible differing releases. [CLI reference](https://herdr.dev/docs/cli-reference/).

**Capacitor already has:** append-only frame contracts, `consent/1..3`, `input/1..2`, optional DTO fields, identity-correlated handshakes, and backward/forward compatibility tests. This is reinforcement, not a protocol replacement. [Advertised handlers](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs#L4), [unknown-field test](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecHelloTests.cs#L57), [client identity](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.Cli.Core/LocalIpc/LocalControlClient.cs#L7). App copy still recommends matching versions. [Incompatibility message](https://github.com/kurrent-io/kcap-cli/blob/fdc3bcbfab94aa1ee306503440cec2979fcd50fb/src/Capacitor.App/ViewModels/HomeViewModel.cs#L79).

**Caveat:** a missing capability and a malformed core protocol are different failures. Keep corruption/identity checks strict; degrade only the unsupported action when the connection remains compatible.

## Warp follow-up: connecting to an existing session

Documentation and installed-build behavior checked 2026-09-18; Warp Control source references verified at `03832b3a8c8eea1dde4c0ab2307e319213b64393` on 2026-09-19. iTerm2's Python API is a possible Capacitor integration; no HerdR adapter using it to adopt an existing iTerm2 terminal was found.

Warp has two relevant surfaces, with different capabilities:

- **Warp Control (`warpctrl`)** addresses already-running local Warp instances and their windows, tabs, panes, and sessions. It provides discovery, metadata, focus/activation, layout, and input staging. It uses an authenticated same-user broker and loopback HTTP control endpoint. The public catalog explicitly excludes block/output/history reads and input submission. `input insert` and `input replace` edit the input buffer without pressing Enter or executing it. Availability depends on build/feature gating and Settings > Scripting, which defaults off on public channels. [Product contract](https://github.com/warpdotdev/warp/blob/03832b3a8c8eea1dde4c0ab2307e319213b64393/specs/warp-control-cli/PRODUCT.md), [operator README](https://github.com/warpdotdev/warp/blob/03832b3a8c8eea1dde4c0ab2307e319213b64393/specs/warp-control-cli/README.md), [implemented server](https://github.com/warpdotdev/warp/blob/03832b3a8c8eea1dde4c0ab2307e319213b64393/app/src/local_control/mod.rs).
- **Remote Control / Agent Session Sharing** publishes an already-running supported CLI agent session through Warp's cloud. Browser/mobile/other Warp clients can view live output; authorized editors can send input and approvals. Closing the original session or stopping publication ends live synchronization. This does not establish PTY ownership transfer or process survival after the original host closes. [Remote Control](https://docs.warp.dev/agents/cli-agents/remote-control/), [sharing and access](https://docs.warp.dev/agents/local-agents/session-sharing/).

No documented public API for subscribing to and typing into an arbitrary existing local Warp terminal surfaced in this review. The [HTTP API](https://docs.warp.dev/api) does include agent-run operations and third-party conversation transcripts, so describing every API as cloud-agent-only would also be inaccurate. Those transcript APIs are not a live terminal attachment protocol.

Local verification: `/Applications/Warp.app` reports version `0.2026.08.19.08.15.01`. Its binary successfully answered `--warpctrl --help`; `--warpctrl input --help` listed only `insert` and `replace`, both explicitly non-submitting. No `warpctrl` wrapper was found on PATH or in the bundle's Resources/bin (which contained `oz`), but direct invocation works:

```sh
/Applications/Warp.app/Contents/MacOS/stable --warpctrl --help
/Applications/Warp.app/Contents/MacOS/stable --warpctrl input --help
/Applications/Warp.app/Contents/MacOS/stable --warpctrl --output-format json instance list
```

The last command returned `{"instances":[]}`. This verifies the CLI exists, not that a running app instance currently exposes control. No settings were changed, sessions published, or input sent. These three read-only commands exited successfully.

Recommendation: a supported Capacitor “open the original Warp pane” integration looks feasible where Warp Control is enabled and session correlation can be established. A full embedded interactive attachment still needs an additional supported interface or investigation of Warp's sharing implementation; `warpctrl` alone does not supply it.
