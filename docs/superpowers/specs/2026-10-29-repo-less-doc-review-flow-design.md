# Repo-less doc-review flow

Issue: kurrent-io/kcap-server#2806 (AI-4682)

## Intent

Muse Assistant users write specs and other documents, not code, and want an independent hosted reviewer to iterate on them until clean, then hand the result to a coding agent. Muse has no git checkout, no local daemon, and an MCP helper (`mcp_call.py`) that kills every call at 60 s. Today no flow can start from there: every start, `mode: context-only` included, resolves a repository and a daemon that hosts it, and the daemon only launches a reviewer inside a `git worktree` of a checkout it owns

A spec and a document are the same thing once the text travels inline: the reviewer reads, returns findings, and receives a revision. What differs is only the criteria, which the driver states

## Goals

- A `doc-review` built-in flow any caller can start with no repository: the document inline, the reviewer in an empty scratch directory on one of the user's daemons
- Callers bounded to short tool calls can start, send and poll without any call outliving 60 s
- Muse's connector wires `kcap-flows` and documents the loop

## Non-goals

- Reviewing code or a repo-hosted spec without a checkout — `code-review` and `spec-review` keep their repo semantics
- A repo-less `list_reviewer_vendors`; the doc-review guide asks the user for a reviewer instead
- Delivering results to Muse without a later turn or cron; Muse hooks carry no secrets

## Anti-goals

- Lending an unrelated checkout to a doc reviewer. A scratch reviewer sees nothing but its prompt
- A daemon too old to know scratch launches receiving one: it would `git worktree add` from an empty `RepoPath` and fail opaquely. Selection is gated on an advertised capability, so an old daemon is never chosen
- Changing the default of any existing argument: a coding-session driver keeps today's blocking start and send

## Design

### 1. `workspace: scratch` (server)

`FlowWorkspacePolicy` gains `scratch`: the participant runs in an empty directory the daemon creates, with no repository. A definition whose participants are all `scratch` is repo-less:

- `StartReviewFlowAsync`, the v3 path, the multi-participant preflight, the lazy launch on send and heal/relaunch never call the repo resolvers for it
- Repo fields on the request (`RepoOwner`, `RepoName`, `RepoPath`, `RequestingRepoRoot`, `patch`, `base_commit`) are ignored, and `mode: context-only` is accepted as a no-op — the prompts are already context-only, so `EnforceContextOnlyRenderable` does not apply
- Mixing `scratch` with another policy in one definition is rejected at parse time (`FlowDefinitionYamlParser`), dynamic YAML included, with a coded error

### 2. Daemon selection by vendor (server)

For a repo-less run the server picks among the owner's connected daemons that advertise the scratch capability (§4) and run the resolved reviewer vendor unattended (`DaemonSupportsVendorUnattended`):

1. a daemon on the requester's machine (`RequesterMachineId`), if it qualifies
2. otherwise the qualifying daemon connected most recently, ties by name — deterministic, so a retry lands on the same daemon

The chosen name is stored in `FlowRunStarted.DaemonName`. Lazy launch and heal use the pinned daemon while it qualifies, and reselect by the same rule when it does not — a scratch reviewer holds no workspace state worth staying for, and a `participant_parked` resume needs its own daemon anyway, so a resume on a missing daemon falls back to a fresh launch

No qualifying daemon is a `FlowRejectedException` coded `no_scratch_daemon`, never an uncoded string. Its message names the vendor and the remediation: run `kcap daemon start -d` with a kcap that supports document review on any machine, or pick another reviewer

### 3. `doc-review` built-in (server)

`Flows/Catalog/BuiltIns/doc-review.yaml`, embedded and added to `BuiltInLeaves`:

- one `reviewer`, `workspace: scratch`, no authored vendor
- prompts tell the reviewer it has no files: everything it may judge is in the message, and it must not go looking for a repository or the network
- `guidance.offer: on_request` — coding sessions are not offered it unprompted
- driver guide:
  - inline the whole document in `context`, plus its type, audience and what to scrutinise (for a spec: completeness, ambiguity, buildability, edge cases, acceptance criteria)
  - pass `vendor` for the reviewer the user named; with none named and no saved preference, ask the user rather than start
  - each follow-up round carries the revised document, or the changed sections plus a note per finding; after `participant_died`/`participant_stopped`, resend the whole document
  - a caller whose tool calls are cut short starts and sends with `wait: false` and polls `get_flow_status` with `timeout_s`

`doc-review` joins the reserved reviewer aliases (`FlowOrchestratorService`, `FlowEndpointHandlers`, `FlowReservedAliases`, `FlowParticipantSessionTitle`) so vendor and model overrides, reviewer readability checks and the "Review" session title apply. Built-in sync already skips an id a tenant published itself; such a tenant keeps its own `doc-review`, which is acceptable

### 4. Scratch launch (daemon, wire)

- `DaemonConnect` (CLI) and the server's `DaemonInfo` gain `bool SupportsScratchWorkspace = false`, appended last (JSON `supports_scratch_workspace`), advertised by every daemon that has the handler below
- `LaunchAgentCommand` (both copies) gains `bool ScratchWorkspace = false`, appended last so the SignalR binding stays compatible. With it set, `RepoPath` is `""` and `Borrowed`, `BorrowCwd`, `BaseRef` and `AttachmentIds` are absent
- The daemon skips the allowed-repo, path-exists and capture-scope checks for a scratch launch — there is no repository to allow, find or report on — and creates the workspace with `WorktreeManager.CreateScratchAsync`: an owner-only directory under `{WorktreeRoot}/scratch/`, initialised with `git init` so vendors that insist on a repository (Codex) run unchanged. Teardown deletes it, and orphan cleanup reaps leftovers
- A scratch launch with a non-empty `RepoPath`, `Borrowed` or a `BaseRef` is rejected as semantic, so a confused server cannot widen what a scratch reviewer sees

### 5. Short calls (CLI MCP)

- `start_flow` and `send_to_participant` gain `wait` (bool, default `true`). `wait: false` returns as soon as the server accepts the request: `flow_run_id`, the role and `status: running`, without `ResolveRoundResultAsync`'s poll
- `get_flow_status` gains `timeout_s` (1–210, default today's cap) bounding `wait: true`
- With no repository detected, the CLI sends the start with null repo fields instead of refusing locally; the server decides whether the definition needs one
- The hardcoded built-in lists in `McpFlowsServer` (the `kind` description and siblings) name `doc-review`

### 6. Muse connector (kcap-cli skill)

`kcap/skills/connect-kcap/SKILL.md` wires `kcap-flows` (`kcap mcp flows`) into `connector.yaml`, adds its tool rows, and documents the loop: `start_flow(definition_id: doc-review, vendor, wait: false)`, then `get_flow_status(wait: true, timeout_s: 50)` repeated until terminal, revisions with `send_to_participant(wait: false)`, `close_flow` on clean. The rule "never start `kcap daemon`" stays: the reviewer runs on a daemon the user runs elsewhere, and `no_scratch_daemon` says so

## Rollout order

The server gate fails closed on daemons without the capability and the CLI changes are additive, so the repos can merge in either order. Useful end to end once both ship and the user's daemon runs the new kcap

1. kcap-cli: daemon scratch launch + capability, wire field, MCP `wait`/`timeout_s`, repo-less start body, connector skill, README for the new MCP arguments
2. kcap-server: `scratch` policy, selection, `doc-review`, aliases, coded refusal; bump the `src/cli` submodule for the integration tests

## Left to the builder

- Exact shape of the scratch branch in each server resolver (a shared `ResolveScratchDaemonAsync` is the obvious factoring)
- Whether `get_flow_status` reports `running` for a `wait: false` start before the round is enqueued, or the start waits for enqueue (it must still return well under 60 s)
- Test placement, following each repo's conventions
