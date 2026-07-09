# Unattended review-flow reviewer: auto-approve its kcap-owned MCP tools

**Issue:** AI-1292 · **Repo:** kcap-cli (daemon) · **Status:** design (rev 2 — addresses Codex spec-review round 1)

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
`bypassPermissions` suppresses MCP-tool prompts entirely (they run through the same bridge,
so the fix should be vendor-agnostic at the bridge).

## Goal / success criteria

1. An unattended review-flow reviewer completes **without any interactive permission
   prompt** for the MCP tools its launch grants it (`get_pr_summary`, `search_context`,
   `list_pr_files`, `get_transcript`, `submit_review_result`, …), for **every** requester.
2. **No behavioural change** for interactive hosted agents or the user's own sessions —
   they prompt exactly as today.
3. The server permission boundary is **not** weakened for anything except an unattended
   reviewer's own launch-granted MCP tools.

## Constraints & safety (non-negotiable)

1. **Daemon-originated trust.** The "this is an unattended reviewer" signal MUST originate
   from the daemon's launch knowledge, never from the agent's self-declaration. Every
   spawned agent today receives the *same* bridge URL/token via `KCAP_DAEMON_URL`
   (`PtyHostedAgentRuntimeFactory.cs:91`), so a body flag or a fixed path segment would let
   an interactive hosted agent (which knows that shared token) claim unattended status. The
   signal must be a **secret only the reviewer process holds**.
2. **Scope:** unattended launches only — `LaunchKind.ReviewFlow`. Never `Default`
   (interactive) or `Review` (PR-review, on-request by design).
3. **Bounded by the launch's MCP allowlist.** The reviewer's *callable* MCP tools are
   physically confined by its Codex MCP config, which `CodexLauncher` clears
   (`mcp_servers={}`) then whitelists to exactly the launch's kcap-owned allowlist
   (`kcap-flow-result` + the flow's allowlist via `KcapMcpRegistry`, flow-starting servers
   stripped). Auto-approval is *authorized by the reviewer token* and *bounded by that config
   lock* — it can never exceed what the launch granted. Adding a mutating server to a flow's
   allowlist is therefore an intentional grant of unattended use.
4. **Fail-safe.** Any uncertainty — unknown/revoked token, malformed body — falls through to
   the existing 404 / prompt / deny path. Never auto-allow on doubt.
5. Native (shell/exec/patch) tools are already covered by `--ask-for-approval never` + the
   sandbox and never reach the bridge — out of scope.
6. **Token secrecy.** The per-reviewer token is a bearer credential carrying extra
   permission. It MUST be **CSPRNG-generated** (`RandomNumberGenerator`, ≥128 bits),
   unguessable, and unique per launch. `KCAP_DAEMON_URL` (which carries it) is already in
   `PtyEnvScrub`'s scrub list (`PtyEnvScrub.cs:24`) so it is not propagated to child/native
   processes; it MUST NOT be logged, persisted, or surfaced via session/transcript/
   agent-visible diagnostics. (Residual, accepted — same as today's shared token: a same-user
   process reading another process's env is out of scope.)

## Design

### Correlation: a per-reviewer bridge token (recommended)

Today the bridge binds a single secret path token
(`http://127.0.0.1:{port}/{token}`, `LocalPermissionBridge.StartAsync`), published to every
agent via `KCAP_DAEMON_URL`, and distinguishes callers only by `session_id` in the POST
body. Two facts rule out a `session_id`-keyed lookup: the daemon *"doesn't track sessionId on
its own (only agentId)"* (`AgentOrchestrator.cs:741`) and learns it late, so the first tool
call can precede it (race); and `session_id` is not a secret, so it can't satisfy constraint 1.

Instead, at launch of an unattended reviewer:

- The orchestrator asks the bridge to **mint a distinct per-reviewer token** (CSPRNG) bound
  to that launch's kcap-owned MCP allowlist, and sets the reviewer's `DaemonBridgeUrl`
  (→ `KCAP_DAEMON_URL`, `AgentOrchestrator.cs:401`) to the minted token's URL instead of the
  shared one. The URL stays `http://127.0.0.1:{port}/{reviewerToken}`, so it still passes the
  reviewer hook's loopback validation (`DaemonBridgeUrl.cs`).
- The bridge keeps a map of **live** tokens: the **shared** token → interactive (prompt as
  today); each **per-reviewer** token → unattended (auto-approve, bound to its allowlist).
- On agent exit/cleanup the orchestrator **revokes** the token (see Lifecycle).

Why this is right: **secure** (only the reviewer process gets its token; interactive agents
keep the shared token and cannot address the unattended path); **race-free** (the token
exists at launch, no `session_id` timing dependence); **small blast radius** (auto-approve
confined to that one token, torn down when the reviewer ends).

### Request classification (explicit order)

On each POST the bridge decides in this order — token authenticity is checked **before** any
tool-specific auto-approval (constraint 4):

1. **Token check.** The path token must be one of the **live** registered tokens (shared +
   reviewer). Unknown/revoked/malformed → **404** (as today).
2. **`submit_review_result` → auto-approve**, on any live token (unchanged; preserves #255 —
   the tool is unique to `kcap-flow-result`, only injected for reviewers).
3. **Live reviewer token → auto-approve.** The token is the daemon-minted authorization, and
   the reviewer's Codex MCP config confines its callable tools to the launch's kcap-owned
   allowlist (constraint 3) — so every MCP tool call arriving on the reviewer token is, by
   construction, a launch-granted kcap tool.
4. **Else** (shared token, any other tool) → `server.RequestPermissionAsync` (interactive
   prompt, unchanged).

### Why token-based, not tool-name parsing

An earlier draft matched a "kcap-owned" tool-name pattern (`IsKcapOwnedTool`). Rejected:
Codex supplies **bare tool names** (no server qualifier), so a name filter both can't prove
server ownership and is spoofable via prefix/substring (`kcap-review-evil`,
`mcp__kcap_review_evil__…`). The reviewer **token** — a daemon-minted secret bound to the
launch's locked allowlist — is the authorization instead; the Codex MCP-config lock is the
enforcement of *which* tools. No tool-name heuristic, no spoof surface. (Forward hook: if a
future vendor sends server-qualified names, the bridge MAY additionally verify the `<server>`
∈ the token's bound allowlist; not needed for Codex today.)

### Vendor-agnostic

Keyed by the reviewer token, not the vendor. Codex is the only current beneficiary (Claude
uses `bypassPermissions`); a future unattended vendor that routes MCP prompts through the
bridge benefits for free.

## Lifecycle & concurrency

- **Mint at launch; revoke after process exit.** The token is minted before spawning the
  reviewer and revoked only **after the reviewer process has exited** (teardown), so no
  in-flight permission request — including a final `submit_review_result` racing with teardown
  — is orphaned by early revocation. (Belt-and-braces: `submit_review_result` is also
  unconditionally auto-approved while any token is live, per classification step 2.)
- **Failed-launch cleanup** revokes any token minted for that launch (mirrors the existing
  failed-launch worktree/cleanup path).
- **Concurrent reviewers.** Each launch mints its own token; the bridge holds a set; revoking
  one never affects another. CSPRNG uniqueness makes collisions negligible; a mint that would
  collide is rejected (and logged) rather than silently reused.
- **Crash / relaunch.** A relaunch mints a fresh token; the crashed reviewer's old token is
  revoked on its cleanup, so a late request on the old token is denied (404, fail-safe).

## Components touched

- **`LocalPermissionBridge`** — a live-token registry (shared token + reviewer tokens;
  `RegisterReviewerToken(...)` returning the token/URL, `RevokeReviewerToken(...)`); classify
  requests per §Request classification; `IsFlowResultSubmission` unchanged; CSPRNG token gen;
  never log the token.
- **`AgentOrchestrator`** — for an unattended `ReviewFlow` launch: mint+register a reviewer
  token (bound to the launch's kcap-owned allowlist), use its URL as this launch's
  `DaemonBridgeUrl`; revoke on cleanup/exit (both the success teardown and the failed-launch
  cleanup path), **after** process exit.

## Alternatives considered

- **`session_id` registry** — rejected: learned late (race with the first tool call), needs a
  reverse map, and `session_id` is spoofable by any agent holding the shared token.
- **Agent self-declared "unattended" flag in the POST body / fixed `/unattended` path
  segment** — rejected: violates constraint 1 (any hosted agent knows the shared token).
- **Tool-name "kcap-owned" filter at the bridge** — rejected: Codex sends bare names
  (unverifiable + spoofable); token + config-lock is safer and simpler (see above).
- **Auto-approve kcap-owned tools for all sessions** — rejected: would suppress prompts in
  interactive hosted agents and the user's own sessions.

## Test plan

`LocalPermissionBridge` unit tests (extend `LocalPermissionBridgeTests`):
- live reviewer token + `get_pr_summary` → auto-approved, **no** `server.RequestPermissionAsync` call;
- live reviewer token + `submit_review_result` → auto-approved (regression);
- **shared** token + `get_pr_summary` → prompts (interactive unchanged — proves an agent
  holding only the shared token can't get kcap tools auto-approved, i.e. no escalation);
- **shared** token + `submit_review_result` → auto-approved (#255 regression);
- **revoked** reviewer token + `get_pr_summary` **and** + `submit_review_result` → 404/deny
  (fail-safe; a revoked reviewer token auto-approves nothing);
- unknown/malformed token → 404 (unchanged);
- **secrecy:** the minted token does not appear in the bridge's emitted log output;
- **concurrency:** two live reviewer tokens; revoking token A still auto-approves on token B.

`AgentOrchestrator` tests:
- an unattended `ReviewFlow` launch mints+registers a reviewer token and injects its URL as
  `KCAP_DAEMON_URL`; a `Default` launch uses the shared token;
- the reviewer token is revoked on normal teardown (after exit) **and** on failed-launch cleanup.

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
