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
   unguessable, and unique per launch. It MUST NOT leak onto any surface another hosted agent
   could later read — otherwise the correlation property (constraint 1) fails. Concretely it
   must not appear in: (a) the recorded session env — `KCAP_DAEMON_URL` is already in
   `PtyEnvScrub`'s scrub list (`PtyEnvScrub.cs:24`); verify it also covers any per-reviewer
   variant; (b) the session **transcript** / recorded events (readable via the kcap-owned read
   tools like `kcap-sessions`/`kcap-review`); (c) agent run/status metadata broadcast to the
   server; (d) daemon/launch/debug logs. Add absence assertions for each surface. (Residual,
   accepted — same as today's shared token: a same-user process reading another process's live
   env is out of scope.)

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
- **Register-time validation (defense in depth).** The token is bound to the **exact same
  post-strip allowlist** the orchestrator hands `CodexLauncher` to build the reviewer's MCP
  config — computed once, used for both, so the config lock and the bridge's bound allowlist
  cannot drift. Before minting, the orchestrator asserts that allowlist ⊆ kcap-owned,
  non-flow-starting servers (reusing `KcapMcpRegistry` + the same `FlowMcpServers.Sanitize`
  check the config uses). If it contains anything else, the orchestrator does **not** mint an
  unattended token (logs + falls back to the shared token) — so a future config regression
  that leaks a non-kcap or flow-starting server can never be silently auto-approved; those
  tools simply prompt.
- The bridge keeps a map of **live** tokens: the **shared** token → interactive (prompt as
  today); each **per-reviewer** token → unattended (auto-approve, bound to its allowlist).
- On agent exit/cleanup the orchestrator **revokes** the token (see Lifecycle).

Why this is right: **secure** (only the reviewer process gets its token; interactive agents
keep the shared token and cannot address the unattended path); **race-free** (the token
exists at launch, no `session_id` timing dependence); **small blast radius** (auto-approve
confined to that one token, torn down when the reviewer ends).

### Request classification (explicit order)

On each POST the bridge decides in this order — token **and body** authenticity are checked
**before** any tool-specific auto-approval (constraint 4). No step may approve on a request it
hasn't fully validated:

1. **Token check.** The path token must be one of the **live** registered tokens (shared +
   reviewer). Unknown/revoked/malformed token → **404** (as today).
2. **Body check.** Parse the JSON body and require a well-formed permission request — a
   non-empty `session_id` and a non-empty `tool_name`. Malformed JSON, missing/empty required
   fields, or an unrecognised request shape → **400** (as today), **never** an approval. Only a
   well-formed tool-call permission request can reach steps 3–4. (Native tool requests never
   reach the bridge — constraint 5 — so a reviewer-token request is always an MCP tool call.)
3. **`submit_review_result` → auto-approve**, on any live token (unchanged; preserves #255 —
   the tool is unique to `kcap-flow-result`, only injected for reviewers).
4. **Live reviewer token → auto-approve**, bounded by the token's **registered allowlist**:
   - if `tool_name` is **server-qualified** (Claude `mcp__<server>__<tool>`), require
     `<server>` ∈ the token's bound allowlist, else fall through to step 5;
   - if `tool_name` is **bare** (Codex), auto-approve — bounded by the Codex MCP-config lock
     (constraint 3) + the orchestrator's register-time allowlist validation (below), which
     together guarantee the reviewer can only call servers in the bound allowlist.
5. **Else** (shared token / server not in the bound allowlist / any other tool) →
   `server.RequestPermissionAsync` (interactive prompt, unchanged).

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
  `RegisterReviewerToken(allowlist)` returning the token/URL, `RevokeReviewerToken(...)`, each
  token carrying its bound allowlist); parse+validate the body then classify per §Request
  classification (enforcing server-qualified names against the bound allowlist);
  `IsFlowResultSubmission` unchanged; CSPRNG token gen; never log the token.
- **`AgentOrchestrator`** — for an unattended `ReviewFlow` launch: compute the post-strip
  kcap-owned allowlist **once**, use it both to build the reviewer MCP config (via
  `CodexLauncher`) and to register the reviewer token; assert it is kcap-owned +
  non-flow-starting before minting (else fall back to the shared token); use the minted URL as
  this launch's `DaemonBridgeUrl`; revoke on cleanup/exit (success teardown **and**
  failed-launch cleanup), **after** process exit.

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
- live reviewer token + `get_pr_summary` (bare + server-qualified in-allowlist forms) →
  auto-approved, **no** `server.RequestPermissionAsync` call;
- live reviewer token + `submit_review_result` → auto-approved (regression);
- live reviewer token + **malformed JSON** → 400, no approval;
- live reviewer token + **missing/empty `tool_name`** → 400, no approval (never blanket-approve);
- live reviewer token + server-qualified name whose `<server>` is **not** in the bound
  allowlist → prompts (bound-allowlist enforcement);
- **shared** token + `get_pr_summary` → prompts (interactive unchanged — proves an agent
  holding only the shared token can't get kcap tools auto-approved, i.e. no escalation);
- **shared** token + `submit_review_result` → auto-approved (#255 regression);
- **revoked** reviewer token + `get_pr_summary` **and** + `submit_review_result` → 404/deny
  (fail-safe; a revoked reviewer token auto-approves nothing);
- unknown/malformed **token** → 404 (unchanged);
- **concurrency:** two live reviewer tokens; revoking token A still auto-approves on token B.

`AgentOrchestrator` tests:
- an unattended `ReviewFlow` launch mints+registers a reviewer token and injects its URL as
  `KCAP_DAEMON_URL`; a `Default` launch uses the shared token;
- the token's registered allowlist **equals** the allowlist used to build the reviewer MCP
  config (single source, no drift);
- a launch whose computed allowlist contains a non-kcap or flow-starting server mints **no**
  reviewer token — it falls back to the shared token so those tools prompt;
- the reviewer token is revoked on normal teardown (after exit) **and** on failed-launch cleanup.

**Secrecy assertions** (a minted reviewer token must not appear in): the bridge's emitted logs;
the daemon/launch logs; the recorded session env (`PtyEnvScrub` coverage); the session
transcript / recorded events; agent run/status metadata sent to the server.

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
