# AI-768 — Soft, agent-led CLI upgrade nudge at session start

Linear: [AI-768](https://linear.app/kurrent/issue/AI-768/check-cli-vs-server-version-at-session-start)

## Problem

When the user's local `kcap` CLI is older than the Kurrent Capacitor server it talks to, nothing in a Claude Code session tells them. The existing stderr update hint (`UpdateCommand.PrintUpdateHintIfAvailable`) hits the npm registry directly and prints on bare `kcap …` invocations, but it never reaches the coding agent's context — so the agent can't proactively offer to upgrade on the user's behalf.

We want a *soft, agent-led* nudge: when the CLI processes a `SessionStart` hook, if the server reports a newer version than the local CLI, inject additional context into Claude telling it a newer kcap is available and to offer the user to install it.

Not a hard version floor. Not a blocker. Not a stderr line. The agent decides whether to surface it; the user decides whether to act.

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

On every `SessionStart` (including `resume` and `compact` sources):

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

The existing `UpdateCommand.PrintUpdateHintIfAvailable` (npm-registry-based stderr hint) is untouched. It serves a different surface (humans running `kcap` directly) and the two can disagree harmlessly across short windows.

## Components

### Refactor: fragment-returning emitters + single aggregator

To guarantee a single JSON object on stdout regardless of how many contributors fire, both the existing and new emitters return text fragments (not envelopes), and the call site does the JSON serialization once.

**`src/Capacitor.Cli/SessionGuidelinesEmitter.cs` (refactor):**

Split the existing `BuildAdditionalContext(string|JsonNode, bool)` into two responsibilities:

- `string? BuildFragment(JsonNode? responseNode, bool disabled)` — returns just the `"Recurring lessons …"` text block built from `top_clusters`, or `null`. No JSON envelope.
- The current `BuildAdditionalContext` methods are removed or kept as thin wrappers that build the envelope via the new aggregator; tests already pinned to the envelope shape continue to pass.

**`src/Capacitor.Cli/VersionNudgeEmitter.cs` (new):**

```csharp
static class VersionNudgeEmitter {
    /// <summary>
    /// Returns the text fragment nudging the user to upgrade kcap, or null
    /// when no nudge is needed (server didn't supply a version, CLI is current
    /// or ahead, either version unparseable, or current CLI is "unknown").
    /// </summary>
    public static string? BuildFragment(JsonNode? responseNode, string currentCliVersion);
}
```

The fragment is two short lines:

```
A newer kcap version is available: <current> → <server>.
Offer the user to upgrade by running: npm install -g @kurrent/kcap
```

Phrased to invite the agent to *propose* the install command, not to silently run it — the agent's normal tool-permission flow still applies.

**`src/Capacitor.Cli/SessionStartAdditionalContext.cs` (new aggregator):**

```csharp
static class SessionStartAdditionalContext {
    /// <summary>
    /// Joins non-null fragments with a blank line and wraps them in a single
    /// SessionStart hookSpecificOutput JSON envelope. Returns null when every
    /// fragment is null/empty so the caller emits nothing at all.
    /// </summary>
    public static string? BuildEnvelope(params string?[] fragments);
}
```

### Shared semver helper

`UpdateCommand.IsNewer` plus its prerelease-strip and the build-metadata strip currently in `UpdateCommand.GetCurrentVersion` are consolidated into a new public helper that both `UpdateCommand` and `VersionNudgeEmitter` call. Likely location: `Capacitor.Cli.Core.SemverCompare` (in Core, since both call sites reach Core), or static methods on `CapacitorVersion`.

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

### New unit tests — `test/Capacitor.Cli.Tests.Unit/SessionStartAdditionalContextTests.cs`

Pure-function tests on the aggregator:

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

- Boots a `WireMockServer` and points the CLI at it via `KCAP_URL` (or by passing `baseUrl` directly to `ClaudeHookCommand.Handle`).
- Redirects `Console.Out` to a `StringWriter` for the duration of each test (and restores it in a `finally`).
- Calls `ClaudeHookCommand.Handle(baseUrl, new StringReader(payload))` with a session-start payload.
- Marks the class `[NotInParallel]` because `Console.Out` is process-global.

Three symmetric cases:

1. Mock returns `{ "version": "<much-newer>" }` only → stdout contains one parseable JSON object whose `additionalContext` references the upgrade and the `npm install -g …` command.
2. Mock returns `{ "top_clusters": [...], "version": "<much-newer>" }` → stdout contains **one** parseable JSON object whose `additionalContext` contains both the lessons block and the upgrade nudge, separated by a blank line. This case directly guards the "single envelope" invariant.
3. Mock returns `{}` (no `version`, no `top_clusters`) → stdout is empty (no envelope emitted).

## Docs

- `README.md` — brief mention under the auto-capture description that the agent may surface an upgrade prompt when a newer kcap is available. No new commands or flags, so no quick-start or `## CLI commands` changes.
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
- Functional changes to `UpdateCommand.PrintUpdateHintIfAvailable` — it coexists unchanged from the user's point of view. Internally, its semver comparison is rerouted through the new shared helper; the only observable difference is that pathological unparseable inputs now produce no hint instead of a "garbage → garbage" line.
- User-facing toggle to suppress the in-agent nudge (can be added later if needed).
- Branching by install method (brew, manual). `npm install -g` is the only documented channel.
- Hard version floors or blocking behaviour. Explicitly soft by design.
- Caching or throttling. Explicitly fire every session-start; revisit only if we hear complaints.

## Risks

- **Context-window cost.** The nudge fires every session-start while a user is behind. Mitigated by keeping the prompt to two short lines; revisitable if it becomes a real complaint.
- **Agent ignoring the nudge.** Working as intended — "soft, agent-led" means the agent may judge it off-topic in some sessions.
- **AOT trimming.** No new patterns introduced; `JsonNode` parsing is already used by the existing emitter. No new IL3050/IL2026 risk.
