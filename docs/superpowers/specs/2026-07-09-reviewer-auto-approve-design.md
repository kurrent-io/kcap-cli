# Unattended review-flow reviewer: auto-approve its kcap-owned MCP tools

**Issue:** AI-1292
**Repo:** kcap-cli (daemon)
**Status:** design

## Problem

A daemon-hosted **review-flow reviewer** (Codex) is launched unattended
(`CodexLauncher`: `--ask-for-approval never`), but Codex still fires a `PermissionRequest`
hook for **MCP tool calls** even under that flag. `LocalPermissionBridge` auto-approves
exactly one tool — `submit_review_result` (`IsFlowResultSubmission`) — and routes every
other MCP tool call to `server.RequestPermissionAsync`, i.e. an **interactive UI prompt
with no human present**. The code-review flow whitelists `kcap-review` for the reviewer, so
its first `get_pr_summary` call blocks the flow until someone manually clicks *Allow*,
defeating the "unattended reviewer" promise.

This is **requester-independent**: the code-review reviewer is always Codex
(`code-review.yaml` → `vendor: codex`), and neither the server nor the daemon branches on
the requesting agent's vendor. Claude reviewers avoid the symptom only because
`bypassPermissions` suppresses MCP-tool prompts entirely (and they also run through the
same bridge, which is why the fix should be vendor-agnostic at the bridge).

## Goal / success criteria

1. An unattended review-flow reviewer completes **without any interactive permission
   prompt** for its kcap-owned MCP tools (`get_pr_summary`, `search_context`,
   `list_pr_files`, `get_transcript`, `submit_review_result`, …), for **every** requester.
2. **No behavioural change** for interactive hosted agents or the user's own sessions —
   they prompt exactly as today.
3. The server permission boundary is **not** weakened for anything except an unattended
   reviewer's own kcap-owned tools.

## Constraints & safety (non-negotiable)

1. **Daemon-originated trust.** The "this is an unattended reviewer" signal MUST originate
   from the daemon's launch knowledge, never from the agent's self-declaration. Every
   spawned agent today receives the *same* bridge URL/token via `KCAP_DAEMON_URL`
   (`PtyHostedAgentRuntimeFactory.cs:91`), so a body flag or a fixed path segment would let
   an interactive hosted agent (which knows that shared token) claim unattended status and
   bypass the user's prompt. The signal must be a **secret only the reviewer process holds**.
2. **Scope:** unattended launches only — `LaunchKind.ReviewFlow`. Never `Default`
   (interactive) or `Review` (PR-review, on-request by design).
3. **kcap-owned tools only.** The reviewer's MCP set is already locked to kcap-owned
   servers (`CodexLauncher` clears `mcp_servers={}`, then whitelists `kcap-flow-result` +
   the flow's kcap-owned allowlist via `KcapMcpRegistry`, with flow-starting servers
   stripped). Auto-approve only tools that name a kcap-owned MCP server; anything else on
   the reviewer still prompts.
4. **Fail-safe.** Any uncertainty — unknown/revoked token, lookup miss, malformed body —
   falls through to the existing prompt/deny path. Never auto-allow on doubt.
5. Native (shell/exec/patch) tools are already covered by `--ask-for-approval never` + the
   sandbox and never reach the bridge — out of scope.

## Design

### Correlation: a per-reviewer bridge token (recommended)

Today the bridge binds a single secret path token
(`http://127.0.0.1:{port}/{token}`, `LocalPermissionBridge.StartAsync`), published to every
agent via `KCAP_DAEMON_URL`, and distinguishes callers only by `session_id` in the POST
body. Two facts rule out a `session_id`-keyed lookup: the daemon *"doesn't track sessionId
on its own (only agentId)"* (`AgentOrchestrator.cs:741`) and learns it late, so the first
tool call can precede it (race); and `session_id` is not a secret, so it can't satisfy
constraint 1.

Instead, at launch of an unattended reviewer:

- The orchestrator asks the bridge to **mint a distinct per-reviewer token** bound to an
  "unattended reviewer" context, and sets that reviewer's `DaemonBridgeUrl`
  (→ `KCAP_DAEMON_URL`, `AgentOrchestrator.cs:401`) to the minted token's URL instead of the
  shared one. The URL stays `http://127.0.0.1:{port}/{reviewerToken}`, so it still passes the
  reviewer hook's loopback validation (`DaemonBridgeUrl.cs`).
- The bridge keeps a small map of live tokens: the **shared** token → interactive (prompt as
  today); each **per-reviewer** token → unattended (auto-approve kcap-owned tools).
- On agent exit/cleanup the orchestrator **revokes** the token.

Why this is right:
- **Secure** — only the reviewer process receives its token (in its own env). Interactive
  agents keep the shared token and cannot address the unattended path.
- **Race-free** — the token exists at launch; no dependence on `session_id` timing.
- **Small blast radius** — auto-approve is confined to requests bearing that one token, and
  is torn down when the reviewer ends.

### Auto-approve rule

Two independent carve-outs, evaluated before the fall-through to `server.RequestPermissionAsync`:

1. **`submit_review_result` — unconditional (unchanged, preserves #255).** Keep the existing
   `IsFlowResultSubmission` auto-approve exactly as-is, on **any** token. The tool is unique to
   the `kcap-flow-result` server, which is only injected for review-flow reviewers, so matching
   it is always safe and must not regress if a reviewer ever runs without a reviewer token
   (feature-off fallback).
2. **kcap-owned tools — only on an unattended-reviewer token (new).** When the request arrives
   on a per-reviewer token, also auto-approve any tool that **names a kcap-owned MCP server**.
   Add an `IsKcapOwnedTool` matcher alongside `IsFlowResultSubmission`, accepting both naming
   conventions the bridge already handles: Claude's sanitised `mcp__kcap_<server>__<tool>` and
   Codex's bare/`kcap-<server>` forms (kcap servers: `kcap-review`, `kcap-sessions`,
   `kcap-memory`, `kcap-flows`, `kcap-flow-result`).

Anything else on the reviewer token, and every non-`submit_review_result` tool on the shared
token, still prompts — no change to interactive behaviour.

*Alternative (rejected): blanket auto-approve any tool on the reviewer token.* The reviewer's
MCP set is already kcap-owned, so this is functionally equivalent, but the explicit
kcap-owned filter is defense-in-depth + auditable, so it wins.

### Vendor-agnostic

Keyed by the reviewer token, not the vendor. Codex is the only current beneficiary (Claude
uses `bypassPermissions` and needs no bridge auto-approve), but a future unattended vendor
that routes MCP prompts through the bridge benefits with no extra work.

## Components touched

- **`LocalPermissionBridge`** — a token→context registry (register/revoke an
  unattended-reviewer token; accept & classify requests by which live token they arrive on);
  add `IsKcapOwnedTool` **alongside** the unchanged `IsFlowResultSubmission`; auto-approve
  kcap-owned tools on a reviewer token, else fall through unchanged.
- **`AgentOrchestrator`** — for an unattended `ReviewFlow` launch: mint+register a reviewer
  token, use its URL as this launch's `DaemonBridgeUrl`; revoke on cleanup/exit (both the
  success teardown and the failed-launch cleanup path).

## Alternatives considered

- **`session_id` registry** (orchestrator records reviewer `session_id` → auto-approve):
  rejected — learned late (race with the first tool call), needs a reverse map, and
  `session_id` is spoofable by any agent holding the shared token.
- **Agent self-declared "unattended" flag in the POST body / fixed `/unattended` path
  segment**: rejected — violates constraint 1 (any hosted agent knows the shared token and
  could claim it).
- **Auto-approve kcap-owned tools for all sessions**: rejected — would suppress prompts in
  interactive hosted agents and the user's own sessions.

## Test plan

`LocalPermissionBridge` unit tests (extend `LocalPermissionBridgeTests`):
- reviewer token + `get_pr_summary` in **both** name forms (`mcp__kcap_review__get_pr_summary`
  and the Codex bare form) → auto-approved, **no** `server.RequestPermissionAsync` call;
- reviewer token + `submit_review_result` → auto-approved (regression);
- reviewer token + a **non-kcap** tool (e.g. `Bash`/`shell`) → prompts (server call);
- **shared** token + `get_pr_summary` → prompts (interactive unchanged);
- **shared** token + `submit_review_result` → auto-approved (unconditional carve-out, #255 regression);
- unknown/revoked token → 404/deny (fail-safe).

`AgentOrchestrator` test:
- launching an unattended `ReviewFlow` reviewer registers a reviewer token and injects its
  URL as `KCAP_DAEMON_URL`; a `Default` launch uses the shared token; the token is revoked on
  teardown.

## Out of scope

- Client-side per-harness MCP tool-trust (Gemini `trust:true` / Copilot `tools` allowlist) —
  tracked separately (the "B" workstream).
- The Claude reviewer path (already clean via `bypassPermissions`).
- `LaunchKind.Review` (PR-review) — interactive by design.

## Rollout / risk

- Pure daemon change; no server or wire-protocol change. With no reviewer tokens registered
  the behaviour is byte-identical to today (feature effectively off).
- Touches the daemon flow/permission internals (Alexey's area) — request his review after the
  Codex code-review cycle.
