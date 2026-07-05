# CLI ↔ server version-skew handling

**Status:** Design (review round 3 — addressing round-2 findings)
**Date:** 2026-07-03
**Linear:** TBD · **GitHub:** TBD

## Problem

Server and CLI now share a consolidated version bump, so a given CLI version is
built to match the server of the same version. But the two are distributed
through different channels with different rollout speeds:

- **Server** is rolled out per tenant tier (`internal`, then `stable` / `micro`).
  Internal tenants can be upgraded to the newest tag immediately.
- **CLI** is published to npm on the single `latest` dist-tag by every release
  (`.github/workflows/release.yml` runs `npm publish` with no `--tag`). Both
  `kcap update` and the passive hint read `registry.npmjs.org/@kurrent/kcap/latest`
  (`UpdateCommand.cs:119`), as does `npm i -g @kurrent/kcap`.

Result: the moment a release goes out for internal tenants, its matching CLI lands
on `latest`. A user on a `stable`/`micro` tenant (older server) can then
`kcap update` — or `npm i -g @kurrent/kcap@latest` — and end up with a CLI **newer
than their server**.

This is a **hard failure**: a too-new CLI can hit a server endpoint/shape the older
server rejects and the operation breaks. It is scoped: the always-on recording path
(hook forwarding) stays backward-compatible. Breakage is confined to discrete
surfaces — **daemon features, MCP tools, and agent-invoked CLI commands** (`recap`,
`errors`, `eval`, …) — each talking to its own server endpoint. No hot-path problem.

## Goals

- A too-new CLI **degrades gracefully** on an older server (clear "needs server ≥ X"
  message, or the feature simply absent) instead of a hard/confusing failure.
- The **default** update path does not drag `stable`/`micro` users ahead of their
  server.
- Keep it cheap; no capability-negotiation framework before it earns its keep.

## Non-goals

- Changing the core recording / hook-forwarding wire contract (already
  backward-compatible).
- A full per-feature capability-negotiation matrix (deferred — Option B).
- Arbitrary N-way tier divergence in the release channel (Open decision 1).

## Current mechanics (grounded)

- **Version source:** MinVer from the git tag; `CapacitorVersion.Current()`.
- **Comparator:** `SemverCompare.IsNewer` **strips `-prerelease`/`+buildmetadata`**.
  Fine for stable-vs-stable but **cannot order prereleases** (`0.7.0-beta.2`,
  `0.7.0-beta.1`, `0.7.0` all compare equal). Load-bearing for Component 5.
- **How the CLI learns the server version today — and the gap:** ONLY
  `response.version` on hook responses, and ONLY the Claude session-start path reads
  it (`ClaudeHookCommand.cs:532` → `VersionNudgeEmitter`, server-newer nudge only).
  The Codex hook **discards** the response body (`CodexHookCommand.cs:183`). And
  `GET /auth/config` deserializes to `AuthDiscoveryResponse`, which **has no version
  field**. So there is **no startup/cold-cache version source** for CLI commands,
  MCP servers, or the daemon — a hole this design must close (see Component 1 +
  Server-side contract).
- **MCP servers** build **static, full** tool lists locally with no startup network
  (`McpReviewServer.cs:33,258-318`, `McpSessionsServer.cs:17-30`,
  `McpFlowsServer.cs:18-38`, `McpMemoryServer.cs:18-31`). Exception:
  `McpJudgeServer.cs:14-17` creates the auth client before serving `tools/list`.
- **Daemon** (`ServerConnection.cs`): opens `/hubs/sessions`, invokes `DaemonConnect`,
  then hub methods (`DaemonUpdateRepoPaths`, `EndAgentSession`, `RequestPermission2`,
  eval dispatch, …). Reasons about "mixed-version rollouts" (`:104,:158,:484`); sends
  its own `_config.Version` at registration (`:342`); does not learn the server
  version at connect.
- **Update cache** (`UpdateCommand.cs:136`) writes a **fixed** `"{path}.tmp"` then
  `File.Move` — races under concurrent writers; the shared cache must not copy this.
- **No `/version` or `/capabilities` endpoint** exists.

## Approaches

### Option A — version compare (`server ≥ X` per endpoint) — RECOMMENDED (now)

Each surface knows the last-seen server version and gates each server-dependent
**endpoint** against a small `MinServerVersion` table. The consolidated bump makes
`server ≥ X` a reliable proxy for "the endpoint exists" — *given the operational
assumptions below*. Needs one small server change to have a cold-cache source (a
`version` field on `/auth/config`; see Server-side contract) — far smaller than a
capability endpoint.

### Option B — capability endpoint — DEFERRED

Server advertises a capability set; surfaces gate on presence. More robust to
backports/mixed-instances/tier-divergence; costs an endpoint + per-feature
discipline. Graduate here when features multiply, tiers diverge, or the operational
assumptions stop holding. Gating goes through one `ServerSupports(...)` helper so
the version check can be swapped for a capability check without touching call sites.

### Rejected — beta channel *alone*, and reactive-error-handling *alone*

A dist-tag only changes defaults; users can `@latest` past it. Catching the server's
rejection after the fact is not a guarantee (Component 4). Both kept as secondary.

## Design

Correctness comes from the **proactive guard** (Components 1–3). The reactive
backstop (4) and beta channel (5) are secondary. Component 5 is independent.

### 1. Shared last-seen server-version cache (the enabling primitive)

On-disk `{ server_version, seen_at }`, written by every surface that learns the
version, read by every surface that gates.

- **Version sources (must be complete):**
  1. Hook `response.version` — requires fixing the **Codex** hook to stop discarding
     the body (`CodexHookCommand.cs:183`) so it seeds the cache like Claude does.
  2. A **`version` field added to `GET /auth/config`** (`AuthDiscoveryResponse`) —
     the CLI already calls this at startup/auth, so it becomes the **cold-cache/
     startup source** for CLI commands, MCP servers (incl. a bounded probe), and the
     daemon's pre-connect probe. See Server-side contract.
  3. The daemon's pre-connect probe (Component 3) and any feature response carrying
     a version.
- **Keying:** resolved **normalized server URL** + source context (`KCAP_PROFILE`,
  `--server-url`/`KCAP_URL`, repo binding). Switching any of these misses the cache.
- **Freshness/downgrade:** short TTL (stale → `unknown`); **accept downgrades** (a
  lower observed version overwrites a higher one — rollback/mixed instances are
  real); invalidate on URL change.
- **Concurrent writes:** unique-temp + atomic rename (or lock) — **not** the fixed
  `.tmp` path the update cache uses.

### 2. `MinServerVersion` table — per server-contract shape

Central map from **server-contract shape → min server version**, where a "contract
shape" is: HTTP **method + route + request (query/body) and response contract**; a
**SignalR hub method + its argument/result contract**; or an **MCP tool request
shape**. Deliberately *not* keyed on route alone: the common skew (Component 4) is a
new query param / body field / response expectation on an endpoint whose route and
method are **unchanged** (e.g. `/api/sessions/{id}/recap`, `/transcript`,
`/eval-context` gaining a new required field). A route-level key would pass while the
new shape still hard-fails. So whenever the CLI changes what it *sends or expects* on
an existing endpoint, that is a new gated contract requiring the server version that
introduced it. Per-command guards take the **max** min-version over the contract
shapes the selected flags actually emit.

- One helper: `ServerSupports(contractKey)` → reads cache, compares, returns
  `supported` / `unsupported` / `unknown` (the `unknown` policy is in Component 3).
- **Completeness test:** a unit test enumerates every server-contacting **contract
  shape** (method+route+request/response contract, hub method+arg/result, MCP tool
  request) and fails if it is neither in the table nor annotated backward-compatible.
  This keeps the table hole-free as endpoints *and their shapes* evolve.

### 3. Proactive guards, the cold-cache policy, and the daemon story

The correctness property is guarding **before** the request, not reacting to it.

- **CLI tools** (`recap`, `errors`, `eval`, …): pre-flight `ServerSupports(...)`;
  `unsupported` → exit with "needs server ≥ X; yours is Y".
- **MCP tools — three layers, because a tool is not atomic.** Filtering the tool
  *list* alone is insufficient: a tool whose *base* form a server supports can still
  advertise a version-gated **optional argument or response mode** (e.g. transcript /
  search / memory / flow optional fields that map to newer query/body/response
  contracts), and listing its full schema lets an agent invoke the too-new shape
  against an older server. So:
  1. **List filter** — hide a tool that is entirely too-new (base contract shape
     unsupported).
  2. **Schema downlevel** — for a tool whose base form is supported but which has
     version-gated optional args/modes, **advertise a downleveled schema** that omits
     the unsupported arguments/modes (hide the whole tool only if even the base form
     is unsupported). The agent then can't request a shape the server can't serve.
  3. **`tools/call` shape check (authoritative)** — before issuing HTTP, run
     `ServerSupports(...)` on the **actual requested contract shape**. This is the real
     guard: an agent may call a tool that wasn't listed, or use a stale/cached schema,
     so list-filtering and downleveling are UX steering, not the guarantee.

  Applied on **every MCP server** — review, sessions, flows, memory, judge, **and
  `flow-result`** (`McpFlowResultServer`, `submit_review_result` →
  `/api/flows/reviewer/result`, dispatched in `Program.cs`). `flow-result` is
  hosted-reviewer-only, so it may instead be annotated **non-gated/backstop-only** in
  the table — the completeness test (Component 2) forces an explicit decision rather
  than a silent omission. Plus a **non-gated `kcap_status` tool always listed on every
  MCP server** (resolving "a hidden tool can't explain its absence"): it reports the
  observed (or `unknown`) server version and which tools/modes are withheld and the
  remedy (run a recorded session to seed / upgrade). So even a fully-gated server shows
  an actionable path, not an empty list.
- **Daemon (explicit pre-connect story):** the daemon does an **HTTP version probe
  (`GET /auth/config` version field) before opening the SignalR hub**. The **core
  `DaemonConnect` + registration contract is declared backward-compatible and
  non-gated** — exactly like the recording hot path, the server must always accept
  the current daemon's connect/register. Only **newer hub methods** (eval dispatch,
  `RequestPermission2`, newer agent methods) are gated per the table. This makes
  "record the version after connect" sufficient, because the connect itself is never
  the thing that breaks.

**Cold-cache (`unknown`) policy — explicit and uniform:**

1. **Resolve `unknown` via the `/auth/config` version probe** (bounded, short
   deadline; result cached). This is the concrete source that makes `known` reachable
   for CLI commands, `judge`, the daemon pre-connect, and (optionally, behind a tight
   deadline at `tools/list`) the other MCP servers.
2. **If still `unknown` (probe unreachable / server too old to carry the field):
   fail safe, not open.** Treat `unknown` as below every gated feature — hide gated
   MCP tools (leaving `kcap_status`), refuse gated CLI commands with the seed/upgrade
   message, and have the daemon connect on the core contract only (newer hub methods
   withheld). "Agents never see unusable tools" holds whenever the version is known;
   on a truly cold, un-probeable cache we choose safety over availability and say so.

Non-gated (backward-compatible) endpoints/tools are always available.

### 4. Reactive backstop (best-effort secondary — NOT the guarantee)

Correcting round 1: mapping a server rejection to a clean message is not a
sufficient correctness layer, because skew often does **not** produce a clean `404`.
A too-new CLI sends new query/body/response assumptions to endpoints that **already
exist** (`eval` → `/questions`, `/eval-context`, `/evals/v3`; `recap` → `/turns`;
memory → new snake_case fields; flows → `async:true` + poll) — an older server may
answer `400`/`422`, `500`, or **`200` with an old shape**, none of which a 404-mapper
catches, and old servers can't be retrofitted to emit structured errors. So:

- The **proactive guard (Component 3) is the correctness mechanism.**
- The backstop only **improves messaging** for cases the server signals cleanly (a
  `404` on a missing route; or a structured `unsupported_version` error from
  new-enough servers) — e.g. `McpReviewServer.DispatchAsync` returns
  `Error: HTTP 404 — <body>` today (`:195`); make that a clean message. It does not
  make a cold-cache CLI safe on its own.

### 5. Beta dist-tag release channel (secondary — noise reducer)

Three changes that don't exist today:

- **Release workflow:** detect a prerelease tag → publish **every** package (platform
  + wrapper) with `npm publish --tag beta`, leaving `latest` untouched; non-prerelease
  → `latest`. Plugin-version bump must accept prerelease strings.
- **Channel-aware update path (new):** launcher + `UpdateCommand` hardcode `latest`.
  Add an opt-in (`kcap update --beta` + persisted config) so update check / hint query
  the `beta` dist-tag for opted-in users. Default stays `latest`.
- **Prerelease-aware comparator (new):** `SemverCompare` strips `-prerelease` and so
  can't order betas or stable-vs-prerelease. The beta update decision (and any
  server-version compare where the server might report a prerelease) needs a real
  SemVer 2.0 precedence comparator.

**Release invariant (the discipline the mechanism depends on).** The tag→dist-tag
mapping is only half the fix; it prevents skew *only if* releases are tagged to match
their rollout:

- Any release deployed **internal-tenants-first must be cut as a SemVer prerelease**
  (`vN-beta.k` → `beta`), never as a bare `vN`.
- A **non-prerelease `vN` tag (→ `latest`) may be cut only once the matching server
  version is available to the cohorts that consume `latest`** (stable/micro — unless
  a cohort is split onto its own dist-tag per Open decision 1).

Without this, a normal-looking `vN` tag used for an internal-first rollout would still
publish `@kurrent/kcap@latest` and recreate the original skew. This is a process
acceptance criterion (Operational assumptions #4); the release workflow should also
*guard* it where it can — e.g. fail/require an explicit override if a bare `vN` tag is
pushed while the release is flagged internal-only.

## Operational assumptions (acceptance criteria for Option A)

Version-as-capability is safe **only if** these hold (they are acceptance criteria):

1. Each tenant URL is rolled **atomically** — no window serving mixed old/new
   instances behind one URL.
2. No **rollback** below a version the CLI may have cached (or the cache TTL +
   accept-downgrade absorbs it).
3. Endpoints are **not feature-flagged independently** of the version number.
4. **Release-tagging discipline** (Component 5): internal-first releases are cut as
   prereleases (→ `beta`); a non-prerelease `vN` (→ `latest`) is cut only once the
   `latest` cohorts (stable/micro) can serve that version. This is the release-side
   analog of atomic rollout — without it, the beta channel doesn't stop the skew.

We just observed the server serving `502`s and a mid-rollout replay, so assumption 1
is not free. Mitigations in the design: short TTL, accept-downgrade, and graceful
degradation rather than a crash when a guard is wrong. Persistent violation is the
trigger to move to Option B (which doesn't depend on these).

## Server-side contract (outside this repo)

Two changes, both small and independently deployable:

1. **Add `version` to `GET /auth/config`** (`AuthDiscoveryResponse`). This is the
   linchpin the round-2 review identified: it gives CLI commands, MCP servers, and the
   daemon a real cold-cache version source at startup, via a call the CLI already
   makes — without a new endpoint and far short of Option B. **If no server change is
   acceptable at all**, the fallback is: cold-cache strictly fail-safes (gated features
   withheld) until a recorded hook seeds `response.version`; there is no other current
   source, so the bounded probe simply doesn't exist and availability suffers.
2. **A structured rejection** (`{ "error": "unsupported_version", "min_version": … }`)
   on feature endpoints an older server lacks — improves Component 4 messaging. Absent
   it, treat a clean `404` as "feature absent."

`GET /version` / `/capabilities` (Option B) remains deferred.

## Phasing

1. **Phase 1 — Beta channel (Component 5).** Independent; prerelease detection +
   `--tag beta`, channel-aware opt-in update, prerelease-aware comparator.
2. **Phase 2 — Cache + proactive guards (Components 1, 2, 3) + the `/auth/config`
   version field.** The correctness foundation, shipped together (the guard depends on
   the cache; the cache's cold-start depends on the version field). Includes: Codex hook
   fix, daemon pre-connect probe + core-contract-backward-compatible declaration, the
   per-contract-shape table + completeness test, `kcap_status`, and **all gating and
   tool-list filtering the table requires, on every MCP server** — filtering *is* the
   proactive correctness mechanism, so none of it is deferred.
3. **Phase 3 — Reactive backstop only (Component 4).** Strictly message cleanup for
   clean server rejections; **no correctness-critical filtering lives here.**
4. **Phase 4 (future) — Option B capability endpoint.**

## Open decisions

1. **Tiers vs dist-tags.** Two tags cover two cohorts assuming `stable`+`micro` share
   a version (→ `latest`; internal → `beta`). If `micro` must lead `stable`, add a
   `next` tag or accept manual pinning. Default: stable+micro = one GA cohort.
2. **`/auth/config` version field — acceptable?** It's the smallest change that makes
   cold-cache correctness real (finding from round 2). If rejected, we accept
   cold-cache fail-safe until hook-seeded (worse availability).
3. **Bounded probe at MCP `tools/list`?** Short startup probe (better availability) vs
   strictly network-free startup + fail-safe-hide (faster startup, `kcap_status`
   explains).
4. **Structured rejection now, or `404`-first?**

## Testing

- Prerelease-aware comparator: SemVer 2.0 precedence incl. beta ordering.
- `ServerSupports` tri-state incl. cold-cache `unknown` → fail-safe.
- `/auth/config` version probe: parsed, cached, drives `known`; absent field →
  `unknown` → fail-safe.
- Cache: round-trip; keying isolation across URL/profile/override; TTL expiry;
  accept-downgrade; concurrent-write atomicity.
- Codex hook records `response.version` (regression for `:183`).
- Daemon: pre-connect probe gates newer hub methods; core `DaemonConnect` works
  against an older server (backward-compatible); `unknown` → core-only connect.
- Table completeness test — every endpoint gated or annotated.
- MCP across all six servers (review, sessions, flows, memory, judge, flow-result):
  `tools/list` known-below/at/above → hidden/shown; a tool whose base form is supported
  but has a version-gated optional arg → **schema downleveled** (the arg omitted);
  `tools/call` with an unsupported requested shape (incl. a stale/unlisted call) →
  refused by the shape check before HTTP; cold cache → gated hidden, `kcap_status`
  present with explanation.
- Backstop: clean `404`/structured error → clean message; documented case where a
  shape-change on an existing endpoint is *not* caught (why the proactive guard exists).
- Release workflow: prerelease → `--tag beta` all packages, `latest` untouched;
  non-prerelease → `latest` moves; and the release-invariant guard — a bare `vN` tag
  pushed for an internal-only rollout fails (or requires explicit override).
- **AOT:** `dotnet publish -c Release` clean of IL3050/IL2026.

## Risks

- **Cache says `supported` but request fails** (mixed instances/rollback): short TTL,
  accept-downgrade, graceful-degrade-not-crash; residual → Option B.
- **Cold-cache over-restriction:** fail-safe withholds gated features until seeded;
  mitigated by the `/auth/config` probe and seeding from all hook vendors + daemon.
- **Beta misroute** (prerelease moving `latest`): tag-shape detection + workflow test.
- **README/docs sync** (CLAUDE.md): `kcap update --beta` / opt-in config is
  user-facing → update `README.md` (quick-start + per-command) in the same PR.
