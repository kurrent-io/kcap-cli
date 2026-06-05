# AI-768 — Soft, agent-led CLI upgrade nudge at session start

Linear: [AI-768](https://linear.app/kurrent/issue/AI-768/check-cli-vs-server-version-at-session-start)

## Problem

When the user's local `kcap` CLI is older than the Kurrent Capacitor server it talks to, nothing in a Claude Code session tells them. The existing stderr update hint (`UpdateCommand.PrintUpdateHintIfAvailable`) hits the npm registry directly and prints on bare `kcap …` invocations, but it never reaches the coding agent's context — so the agent can't proactively offer to upgrade on the user's behalf.

We want a *soft, agent-led* nudge: when the CLI processes a `SessionStart` hook, if the server reports a newer version than the local CLI, inject additional context into Claude telling it a newer kcap is available and to offer the user to install it.

Not a hard version floor. Not a blocker. Not a stderr line. The agent decides whether to surface it; the user decides whether to act.

## Scope and vendor-neutrality

The design is split into a **vendor-neutral core** and **per-vendor delivery shims**. v1 ships the Claude Code delivery shim because that vendor's hook protocol has the channel we need today; the Codex and Cursor shims are real follow-up work that reuse the core unchanged.

**Vendor-neutral core (lands in v1):**

- The server `version` field on the shared `/hooks/session-start` response (all three vendors POST to this — Codex via `/hooks/session-start/codex`, but the response shape is shared).
- The semver comparison helper.
- `VersionNudgeEmitter.BuildFragment` — returns the human-readable nudge text or `null`. No JSON envelope, no vendor-specific framing. Pure function over `(JsonNode? responseBody, string currentCliVersion)`.

**Per-vendor delivery shims:**

- **Claude Code (v1):** the existing `ClaudeHookCommand` session-start case calls the core, packs the fragment into the SessionStart `hookSpecificOutput.additionalContext` envelope, writes it to stdout. Implemented in this spec.
- **Codex (follow-up):** `CodexHookCommand.HandleSessionStart` will call the core, then surface the fragment via Codex's `session-start.command.output` schema. Whether that schema has an `additionalContext` analog needs verification against Codex's hook docs; that verification is the gating work for the Codex follow-up ticket. Out of scope for this spec.
- **Cursor (follow-up):** Cursor's SessionStart hook stdout must stay silent (`CursorHookCommand` drops response bodies; `docs/superpowers/specs/2026-06-01-ai-669-cursor-hooks-ingest-design.md:121`). Delivery via a different channel — e.g. piggybacking Cursor's `beforeSubmitPrompt` hook (mapped to `user-prompt/cursor` in `CursorHookEventMap.cs:18`), a daemon-side injection, or the shared MCP server's `instructions` field — needs a separate design pass. Out of scope for this spec.

**Known follow-up refactor needed for both Codex and Cursor shims (out of scope here, but recorded so the follow-up tickets start with accurate context):** both vendors' session-start helpers currently discard the response body. `CodexHookCommand.PostHookAsync` (`src/Capacitor.Cli/Commands/CodexHookCommand.cs:263`) returns only an `int`; `CursorHookCommand.TryPostHookAsync` (`src/Capacitor.Cli/Commands/CursorHookCommand.cs:167,197`) returns only a `bool`. The follow-up tickets must lift one or both helpers to also return a parsed `JsonNode?` body (at least for the session-start route) before calling `VersionNudgeEmitter.BuildFragment`. That refactor is not needed to land v1, since v1 only changes the Claude path, but it's load-bearing for v2/v3.

**The shape rule this enforces:** `VersionNudgeEmitter.BuildFragment` and the semver helper must not depend on Claude-specific concepts (no `hookSpecificOutput`, no SessionStart envelope shape, no `Console.WriteLine`). They take JSON in, return text out. The Claude-shaped envelope is built one level up, in the Claude delivery shim, by `SessionStartAdditionalContext.BuildEnvelope`. When Codex and Cursor shims land they wrap the same fragment in *their* native shape.

## Server contract

The existing `POST /hooks/session-start` response body gains one optional field:

```json
{
  "slug": "...",
  "top_clusters": [...],
  "version": "0.6.5"
}
```

- **Field name:** `version` (string, optional).
- **Meaning:** the server's own version. Server and CLI are released hand-in-hand, so by convention this is also the matching CLI version. The server does not query npm or speculate about CLI releases — it reports what it knows about itself.
- **Absent:** older servers that don't ship this field. The CLI silently does nothing. Backward compatible.

Server-side implementation is out of scope for this spec — this document defines the contract the CLI consumes.

## CLI behaviour

This section describes the **Claude Code delivery shim**. The core (fragment builder + semver helper) is vendor-neutral; see the previous section.

On every Claude `SessionStart` (including `resume` and `compact` sources):

1. Each contributor (recurring-lessons, version-nudge) produces a `string?` *text fragment* — the human-readable lines that belong in `additionalContext`.
2. The call site collects the non-null fragments and, if any are present, serializes **one** `SessionStart` `hookSpecificOutput` JSON object whose `additionalContext` is the fragments joined by a blank-line separator. That single object is written to stdout.
3. If every contributor returns `null`, nothing is written. Stays backward compatible with the current "no top_clusters → no output" behaviour.

This avoids emitting two top-level JSON objects on stdout — Claude Code parses hook stdout as a single JSON object (with plain-text fallback for non-JSON stdout), so two consecutive envelopes are not a supported shape. See the Claude Code hook docs.

For the version-nudge contributor specifically:

- Read `response.version`.
- Compare against `CapacitorVersion.Current()` using the shared semver helper described below.
- If `server.version` is strictly greater, return a two-line text fragment (current → server + the `npm install -g …` line).
- In every other case (absent field, unparseable on either side, equal, CLI strictly greater, current CLI is `"unknown"`), return `null`.

The nudge fires on every session-start where the server is strictly newer. No caching, no throttle. If procrastinated upgrades become a context-window problem in practice, we can add a cache or opt-out later — not in v1.

The existing `UpdateCommand.PrintUpdateHintIfAvailable` (npm-registry-based stderr hint) is user-facing-unchanged. It serves a different surface (humans running `kcap` directly), and the two surfaces can disagree harmlessly across short windows. Internally its semver comparison is rerouted through the shared helper, which is a behaviour-tightening described in the helper section below.

## Components

### Vendor-neutral core (shared across all three vendors)

**`src/Capacitor.Cli.Core/VersionNudgeEmitter.cs` (new, in Core):**

Lives in `Capacitor.Cli.Core` (not `Capacitor.Cli`) so the future Codex and Cursor shims can call it without taking a dependency on the Claude command project.

```csharp
namespace Capacitor.Cli.Core;

public static class VersionNudgeEmitter {
    /// <summary>
    /// Vendor-neutral. Returns the human-readable text fragment nudging the
    /// user to upgrade kcap, or null when no nudge is needed (server didn't
    /// supply a version, CLI is current or ahead, either version unparseable,
    /// or current CLI is "unknown"). The caller is responsible for delivering
    /// this fragment via its vendor's native channel — no JSON envelope is
    /// produced here.
    /// </summary>
    public static string? BuildFragment(JsonNode? responseNode, string currentCliVersion);
}
```

The fragment is two short lines:

```
A newer kcap version is available: <current> → <server>.
Offer the user to upgrade by running: npm install -g @kurrent/kcap
```

Phrased to invite the agent to *propose* the install command, not to silently run it — each vendor's normal tool-permission flow still applies. The fragment is intentionally plain text with no markdown fencing, no Claude-specific phrasing, and no `hookSpecificOutput`-shaped wrapping.

**Shared semver helper:** also in Core (likely `Capacitor.Cli.Core.SemverCompare`). Both `UpdateCommand`, `VersionNudgeEmitter`, and any future vendor shim call it. See the **Shared semver helper** subsection below.

### Claude Code delivery shim (this is what v1 implements)

**`src/Capacitor.Cli/SessionGuidelinesEmitter.cs` (refactor):**

Split the existing `BuildAdditionalContext(string|JsonNode, bool)` into two responsibilities:

- `string? BuildFragment(JsonNode? responseNode, bool disabled)` — returns just the `"Recurring lessons …"` text block built from `top_clusters`, or `null`. No JSON envelope. Stays in `Capacitor.Cli` (Claude-only feature today).
- The current `BuildAdditionalContext` methods are removed or kept as thin wrappers that build the envelope via the new aggregator; tests already pinned to the envelope shape continue to pass.

**`src/Capacitor.Cli/SessionStartAdditionalContext.cs` (new aggregator, Claude-shaped):**

```csharp
namespace Capacitor.Cli;

static class SessionStartAdditionalContext {
    /// <summary>
    /// Claude-Code-specific: joins non-null fragments with a blank line and
    /// wraps them in a single SessionStart hookSpecificOutput JSON envelope.
    /// Returns null when every fragment is null/empty so the caller emits
    /// nothing at all.
    /// </summary>
    public static string? BuildEnvelope(params string?[] fragments);
}
```

The Claude-shaped envelope-builder stays in `Capacitor.Cli`. Codex and Cursor shims will have their own envelope-builders (or equivalent) in their respective code paths.

### Shared semver helper

`UpdateCommand.IsNewer` plus its prerelease-strip and the build-metadata strip currently in `UpdateCommand.GetCurrentVersion` are consolidated into a new public helper in `Capacitor.Cli.Core` so every vendor shim — Claude today, Codex and Cursor tomorrow — can call it. Likely location: `Capacitor.Cli.Core.SemverCompare`, or static methods on the existing `CapacitorVersion`.

The new helper differs from today's `IsNewer` in two intentional ways:

1. **Build-metadata is stripped inside the helper** (was only stripped in `GetCurrentVersion` before passing to `IsNewer`). Both sides of the comparison get normalized consistently.
2. **Unparseable input returns `false`** (was `latest != current` string-inequality fallback). This silences the existing stderr hint when version strings are garbage instead of printing a confusing "Update available: garbage → garbage" line.

Both are strict tightenings of the existing behaviour — the only `UpdateCommand` callers that change are pathological inputs that never occur in practice (npm registry doesn't return unparseable versions; assembly metadata is well-formed or literally `"unknown"`, which the helper handles as unparseable). The existing `"unknown"` and `null` guards in `GetCurrentVersion` and `IsNewer` remain.

### Call site: `ClaudeHookCommand` session-start case

In `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs`, the existing `case "session-start":` block is updated to collect fragments and emit at most one envelope:

```csharp
try {
    var disabled = AppConfig.ResolvedProfile?.Profile?.DisableSessionGuidelines is true;
    var lessonsFragment = SessionGuidelinesEmitter.BuildFragment(responseNode, disabled);
    var nudgeFragment   = VersionNudgeEmitter.BuildFragment(responseNode, CapacitorVersion.Current());

    var envelope = SessionStartAdditionalContext.BuildEnvelope(lessonsFragment, nudgeFragment);
    if (envelope is not null) Console.WriteLine(envelope);
} catch {
    // Best effort — never break session capture for hook output emission.
}
```

The try/catch boundary stays where it is today; failure to build either fragment must not break session capture.

## Testing

### New unit tests — `test/Capacitor.Cli.Tests.Unit/VersionNudgeEmitterTests.cs`

Pure-function tests on `VersionNudgeEmitter.BuildFragment`:

- Returns `null` when response has no `version` field.
- Returns `null` when `version` is empty / whitespace / unparseable.
- Returns `null` when `currentCliVersion` equals `server.version`.
- Returns `null` when `currentCliVersion` is strictly newer than `server.version` (dev-build / prerelease case).
- Returns `null` when `currentCliVersion` is `"unknown"`.
- Returns `null` when prerelease suffixes make the cores equal (`0.6.5-alpha.1` vs `0.6.5`).
- Returns `null` when build metadata makes the cores equal (`0.6.5+sha` vs `0.6.5`).
- Returns a fragment string when server is strictly newer.
- Returned fragment contains both versions and the literal `npm install -g @kurrent/kcap`.
- Returned fragment is plain text (no JSON braces, no leading `{`).

### Aggregator unit tests — repurposed `SessionStartAdditionalContextTests`

`test/Capacitor.Cli.Tests.Unit/HookForwardingTests.cs:145` already declares a `public class SessionStartAdditionalContextTests`. Despite its name, every `[Test]` method in that class (seven at the time of writing — `SessionStart_EmitsAdditionalContextJson_WhenServerReturnsTopClusters`, `…_EmitsNothing_WhenTopClustersAbsent`, `…_EmitsNothing_WhenDisableSessionGuidelinesConfigSet`, `…_EmitsNothing_WhenTopClustersEmpty`, `…_EmitsNothing_WhenTopClustersIsObject`, `…_EmitsNothing_WhenResponseNodeIsArray`, `…_SkipsEntries_WithBlankText`) calls `SessionGuidelinesEmitter.BuildAdditionalContext` directly. They belong with the emitter's own tests. Action:

1. Move **every** existing `[Test]` method in this class into `SessionGuidelinesEmitterTests` (creating that file if absent). Adjust each to assert on `BuildFragment` output — plain text, no envelope JSON, no `hookEventName` field. Verify by grepping the moved file for `hookSpecificOutput` / `hookEventName` afterwards: zero hits expected.
2. Reuse the now-empty `SessionStartAdditionalContextTests` class for the new aggregator tests below.

(Method count is checked at implementation time — the spec count may drift if the existing class grows before the refactor lands.)

Pure-function tests on `SessionStartAdditionalContext.BuildEnvelope`:

- Returns `null` when every fragment is `null`/empty/whitespace.
- Single non-null fragment → valid envelope containing exactly that fragment.
- Multiple non-null fragments → single envelope, fragments joined by a blank line in order passed.
- Returned JSON parses as a single object with `hookSpecificOutput.hookEventName == "SessionStart"` and `additionalContext` matching the joined fragments.

### Shared semver helper tests — `test/Capacitor.Cli.Tests.Unit/SemverCompareTests.cs`

Cover the tightened helper directly (relocating any existing comparison tests that live under `UpdateCommand`):

- Strictly-newer cases: `0.6.3 < 0.6.5`, `0.6.5 < 0.7.0`, `1.0.0 > 0.9.9`, etc.
- Equal cases: identical strings, prerelease-equal (`0.6.5` vs `0.6.5-alpha.1` → not newer), build-metadata-equal (`0.6.5` vs `0.6.5+sha` → not newer).
- Unparseable on either side → `false` (regression-fence vs the old `latest != current` fallback).
- `null` / `"unknown"` on either side → `false`.

### Refactor: existing `SessionGuidelinesEmitterTests`

The current tests assert the full envelope shape. After the refactor, the file is split:

- Direct `SessionGuidelinesEmitter.BuildFragment` tests assert the plain-text block only (no JSON envelope).
- Envelope-shape assertions migrate to `SessionStartAdditionalContextTests` (or stay in a thin integration test that pipes a fragment through the aggregator). Net coverage is preserved.

### Integration tests — `test/Capacitor.Cli.Tests.Integration/`

`HookRoundTripTests` today posts directly via `HttpClient` and never invokes `ClaudeHookCommand.Handle`, so it can't observe stdout. We add a new fixture (e.g. `ClaudeHookStdoutTests.cs`) that:

- Boots a `WireMockServer` and points the CLI at it via the `baseUrl` parameter to `ClaudeHookCommand.Handle`.
- Redirects `Console.Out` to a `StringWriter` for the duration of each test (and restores it in a `finally`).
- Calls `ClaudeHookCommand.Handle(baseUrl, new StringReader(payload))` with a session-start payload.
- Marks the class `[NotInParallel]` because `Console.Out` is process-global.

**Critical:** the test payloads must **omit `transcript_path`** (and/or `session_id`). `ClaudeHookCommand.session-start:290-292` calls `WatcherManager.EnsureWatcherRunning` only when both fields are present, and the watcher spawn (`Process.Start` at `WatcherManager.cs:85`) under TUnit's `Console` capture corrupts subsequent stdout reads — exactly the failure mode documented at `test/Capacitor.Cli.Tests.Unit/Codex/CodexHookCommandTests.cs:47-53`. Omitting `transcript_path` short-circuits the spawn while still exercising the full request-build → POST → response-parse → stdout-emit path that this feature lives in.

Three symmetric cases:

1. Mock returns `{ "version": "<much-newer>" }` only → stdout contains one parseable JSON object whose `additionalContext` references the upgrade and the `npm install -g …` command.
2. Mock returns `{ "top_clusters": [...], "version": "<much-newer>" }` → stdout contains **one** parseable JSON object whose `additionalContext` contains both the lessons block and the upgrade nudge, separated by a blank line. This case directly guards the "single envelope" invariant.
3. Mock returns `{}` (no `version`, no `top_clusters`) → stdout is empty (no envelope emitted).

## Docs

- `README.md` — brief Claude-Code-specific mention under the auto-capture description that Claude Code sessions may surface an upgrade prompt when a newer kcap is available. Phrase it explicitly as "in Claude Code sessions" so it doesn't imply equivalent behaviour for Cursor or Codex (which it doesn't). No new commands or flags, so no quick-start or `## CLI commands` changes.
- No `Resources/help-*.txt` changes.

## Edge cases and behaviour matrix

| `server.version` | `cliCurrent`            | Outcome              |
|------------------|-------------------------|----------------------|
| absent           | any                     | no nudge             |
| empty / garbage  | any                     | no nudge             |
| `"0.6.5"`        | `"0.6.3"`               | **nudge**            |
| `"0.6.5"`        | `"0.6.5"`               | no nudge             |
| `"0.6.5"`        | `"0.6.5-alpha.1"`       | no nudge             |
| `"0.6.5"`        | `"0.6.5+abc"`           | no nudge             |
| `"0.6.5"`        | `"0.7.0"`               | no nudge (CLI ahead) |
| `"0.6.5"`        | `"unknown"`             | no nudge             |
| `"0.6.5"`        | unparseable             | no nudge             |

## Out of scope

- Server-side implementation of the `version` field (separate ticket, separate repo).
- Cursor and Codex **delivery shims**. Only the Claude shim ships in v1. The vendor-neutral core (`Capacitor.Cli.Core.VersionNudgeEmitter`, semver helper) is sized to be reused by Codex/Cursor shims in follow-up tickets without redesign. See the **Scope and vendor-neutrality** section.
- Functional changes to `UpdateCommand.PrintUpdateHintIfAvailable` — it coexists unchanged from the user's point of view. Internally, its semver comparison is rerouted through the new shared helper; the only observable difference is that pathological unparseable inputs now produce no hint instead of a "garbage → garbage" line.
- User-facing toggle to suppress the in-agent nudge (can be added later if needed).
- Branching by install method (brew, manual). `npm install -g` is the only documented channel.
- Hard version floors or blocking behaviour. Explicitly soft by design.
- Caching or throttling. Explicitly fire every session-start; revisit only if we hear complaints.

## Risks

- **Context-window cost.** The nudge fires every session-start while a user is behind. Mitigated by keeping the prompt to two short lines; revisitable if it becomes a real complaint.
- **Agent ignoring the nudge.** Working as intended — "soft, agent-led" means the agent may judge it off-topic in some sessions.
- **AOT trimming.** No new patterns introduced; `JsonNode` parsing is already used by the existing emitter. No new IL3050/IL2026 risk.
