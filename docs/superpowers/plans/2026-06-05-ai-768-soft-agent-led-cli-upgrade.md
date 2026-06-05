# AI-768 — Soft, agent-led CLI upgrade nudge — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the Kurrent Capacitor server tells the kcap CLI it's running a newer version than the local install at SessionStart time, inject additional context into Claude Code's session so the agent can offer the user to upgrade by running `npm install -g @kurrent/kcap`.

**Architecture:** Two layers. Vendor-neutral core in `Capacitor.Cli.Core` (semver comparison helper + a pure-text "version nudge" fragment builder). Claude-Code-specific delivery shim in `Capacitor.Cli` (a Claude-shaped envelope aggregator that joins all fragments — recurring lessons + version nudge — into a single SessionStart `hookSpecificOutput` JSON object). The existing `SessionGuidelinesEmitter` is refactored to also return text fragments so both contributors flow through one envelope. Codex/Cursor delivery shims are explicit follow-up tickets and are not implemented in v1.

**Tech Stack:** .NET 10 (NativeAOT), TUnit on Microsoft Testing Platform, WireMock.Net for HTTP mocking, `System.Text.Json` (AOT-safe `JsonNode`).

**Spec:** `docs/superpowers/specs/2026-06-05-ai-768-soft-agent-led-cli-upgrade-design.md`

---

## File overview

**Create (production):**
- `src/Capacitor.Cli.Core/SemverCompare.cs` — strict-greater semver comparator, strips prerelease and build metadata, returns `false` on unparseable/null/`"unknown"`.
- `src/Capacitor.Cli.Core/VersionNudgeEmitter.cs` — vendor-neutral fragment builder. Pure function over `(JsonNode? responseBody, string currentCliVersion)`.
- `src/Capacitor.Cli/SessionStartAdditionalContext.cs` — Claude-shaped aggregator. Joins non-null fragments with a blank line and wraps in one SessionStart `hookSpecificOutput` envelope.

**Modify (production):**
- `src/Capacitor.Cli/SessionGuidelinesEmitter.cs` — replace `BuildAdditionalContext` with `BuildFragment` (plain text, no envelope).
- `src/Capacitor.Cli/Commands/UpdateCommand.cs` — reroute `IsNewer` and the build-metadata strip in `GetCurrentVersion` through `SemverCompare`.
- `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs` — replace the single `SessionGuidelinesEmitter.BuildAdditionalContext` call in `case "session-start"` with a call to both fragment builders + the aggregator.
- `README.md` — add a Claude-Code-specific bullet under "What it records".

**Create (tests):**
- `test/Capacitor.Cli.Tests.Unit/SemverCompareTests.cs`
- `test/Capacitor.Cli.Tests.Unit/VersionNudgeEmitterTests.cs`
- `test/Capacitor.Cli.Tests.Unit/SessionGuidelinesEmitterTests.cs` — receives the seven existing `SessionStartAdditionalContextTests` methods, adapted to `BuildFragment`.
- `test/Capacitor.Cli.Tests.Integration/ClaudeHookStdoutTests.cs` — invokes `ClaudeHookCommand.Handle` and captures stdout. `NotInParallel`, payload omits `transcript_path` so the watcher doesn't spawn.

**Modify (tests):**
- `test/Capacitor.Cli.Tests.Unit/HookForwardingTests.cs` — empty out the existing `SessionStartAdditionalContextTests` class (move its seven methods to `SessionGuidelinesEmitterTests`) and repopulate it with aggregator tests.

---

## Task 1: `SemverCompare` helper (TDD)

**Files:**
- Create: `test/Capacitor.Cli.Tests.Unit/SemverCompareTests.cs`
- Create: `src/Capacitor.Cli.Core/SemverCompare.cs`

The helper is a single public static method that answers "is `latest` strictly newer than `current`?" with the comparison rules from the spec: strip `-prerelease`, strip `+metadata`, parse the remaining triplet with `System.Version.TryParse`, return `false` on any failure (including either input being `null`, empty, `"unknown"`, or unparseable). This is the new authority — `UpdateCommand` will reroute through it in Task 2.

### Steps

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Tests.Unit/SemverCompareTests.cs`:

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class SemverCompareTests {
    [Test] public async Task Returns_true_when_latest_is_strictly_newer_patch()  => await Assert.That(SemverCompare.IsNewer("0.6.5", "0.6.3")).IsTrue();
    [Test] public async Task Returns_true_when_latest_is_strictly_newer_minor()  => await Assert.That(SemverCompare.IsNewer("0.7.0", "0.6.5")).IsTrue();
    [Test] public async Task Returns_true_when_latest_is_strictly_newer_major()  => await Assert.That(SemverCompare.IsNewer("1.0.0", "0.9.9")).IsTrue();

    [Test] public async Task Returns_false_when_equal()                          => await Assert.That(SemverCompare.IsNewer("0.6.5", "0.6.5")).IsFalse();
    [Test] public async Task Returns_false_when_current_is_strictly_newer()      => await Assert.That(SemverCompare.IsNewer("0.6.5", "0.7.0")).IsFalse();

    [Test] public async Task Returns_false_when_current_has_prerelease_suffix()  => await Assert.That(SemverCompare.IsNewer("0.6.5", "0.6.5-alpha.1")).IsFalse();
    [Test] public async Task Returns_false_when_latest_has_prerelease_suffix()   => await Assert.That(SemverCompare.IsNewer("0.6.5-rc.1", "0.6.5")).IsFalse();

    [Test] public async Task Returns_false_when_current_has_build_metadata()     => await Assert.That(SemverCompare.IsNewer("0.6.5", "0.6.5+abcdef")).IsFalse();
    [Test] public async Task Returns_false_when_latest_has_build_metadata()      => await Assert.That(SemverCompare.IsNewer("0.6.5+abcdef", "0.6.5")).IsFalse();

    [Test] public async Task Returns_false_when_latest_is_null()                 => await Assert.That(SemverCompare.IsNewer(null, "0.6.5")).IsFalse();
    [Test] public async Task Returns_false_when_current_is_null()                => await Assert.That(SemverCompare.IsNewer("0.6.5", null)).IsFalse();
    [Test] public async Task Returns_false_when_latest_is_unknown_literal()      => await Assert.That(SemverCompare.IsNewer("unknown", "0.6.5")).IsFalse();
    [Test] public async Task Returns_false_when_current_is_unknown_literal()     => await Assert.That(SemverCompare.IsNewer("0.6.5", "unknown")).IsFalse();
    [Test] public async Task Returns_false_when_latest_is_unparseable_garbage()  => await Assert.That(SemverCompare.IsNewer("not.a.version", "0.6.5")).IsFalse();
    [Test] public async Task Returns_false_when_current_is_unparseable_garbage() => await Assert.That(SemverCompare.IsNewer("0.6.5", "not.a.version")).IsFalse();
    [Test] public async Task Returns_false_when_both_empty()                     => await Assert.That(SemverCompare.IsNewer("", "")).IsFalse();
    [Test] public async Task Returns_false_when_both_whitespace()                => await Assert.That(SemverCompare.IsNewer("  ", "  ")).IsFalse();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SemverCompareTests/*"`

Expected: tests fail because `Capacitor.Cli.Core.SemverCompare` does not exist (compile error).

- [ ] **Step 3: Implement the helper**

Create `src/Capacitor.Cli.Core/SemverCompare.cs`:

```csharp
namespace Capacitor.Cli.Core;

/// <summary>
/// Strict-greater semver-ish comparator. Strips <c>-prerelease</c> and
/// <c>+buildmetadata</c> from both sides before parsing the remaining
/// dotted triplet with <see cref="System.Version.TryParse"/>. Any
/// unparseable, <c>null</c>, empty, whitespace, or literal
/// <c>"unknown"</c> input returns <c>false</c> — i.e. "we don't know,
/// so don't claim newer". Authoritative for both the in-agent upgrade
/// nudge and the stderr update hint.
/// </summary>
public static class SemverCompare {
    public static bool IsNewer(string? latest, string? current) {
        var l = ParseCore(latest);
        var c = ParseCore(current);
        if (l is null || c is null) return false;
        return l > c;
    }

    static Version? ParseCore(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (string.Equals(raw, "unknown", StringComparison.Ordinal)) return null;

        var s = raw;

        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];

        return Version.TryParse(s, out var parsed) ? parsed : null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SemverCompareTests/*"`

Expected: all 18 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/SemverCompare.cs test/Capacitor.Cli.Tests.Unit/SemverCompareTests.cs
git commit -m "feat: add SemverCompare helper for strict-greater version checks (AI-768)"
```

---

## Task 2: Reroute `UpdateCommand` through `SemverCompare`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/UpdateCommand.cs`

`UpdateCommand.IsNewer` and the `+`-strip in `GetCurrentVersion` are duplicates of what `SemverCompare` now does. Delete both, route through `SemverCompare`. The only observable behaviour change is that pathological unparseable inputs no longer produce a `"Update available: garbage → garbage"` stderr line — they produce no hint at all. That is a strict improvement.

### Steps

- [ ] **Step 1: Read the current file**

Read `src/Capacitor.Cli/Commands/UpdateCommand.cs` end-to-end to confirm where `IsNewer` and `GetCurrentVersion` live (lines 118–150 at time of writing).

- [ ] **Step 2: Replace the local `IsNewer` and simplify `GetCurrentVersion`**

In `src/Capacitor.Cli/Commands/UpdateCommand.cs`:

a) Delete the entire local `static bool IsNewer(string? latest, string? current)` method (lines 113–135 at time of writing) and replace every call site (`IsNewer(latest, current)`) with `SemverCompare.IsNewer(latest, current)`.

b) Replace `GetCurrentVersion` to just return the raw informational version unchanged — the `+` strip moves into `SemverCompare`:

```csharp
static string? GetCurrentVersion() =>
    typeof(UpdateCommand).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion;
```

c) Add `using Capacitor.Cli.Core;` to the top of the file if not already present (it is, but verify).

- [ ] **Step 3: Verify the file still compiles**

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`

Expected: clean build.

- [ ] **Step 4: Run the existing unit tests to confirm no regression**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`

Expected: all tests pass (no `UpdateCommand`-specific test file exists today; SemverCompareTests from Task 1 still pass).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/UpdateCommand.cs
git commit -m "refactor: route UpdateCommand version compare through SemverCompare (AI-768)"
```

---

## Task 3: `VersionNudgeEmitter` (TDD)

**Files:**
- Create: `test/Capacitor.Cli.Tests.Unit/VersionNudgeEmitterTests.cs`
- Create: `src/Capacitor.Cli.Core/VersionNudgeEmitter.cs`

Pure-function fragment builder. Reads `responseNode["version"]` (string), compares against the supplied current CLI version via `SemverCompare.IsNewer`, returns a two-line plain-text fragment if the server is strictly newer, else `null`. No JSON envelope, no Claude-specific framing. Lives in `Capacitor.Cli.Core` so future Codex/Cursor shims can reuse it.

### Steps

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Tests.Unit/VersionNudgeEmitterTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class VersionNudgeEmitterTests {
    static JsonNode? ResponseWithVersion(string? version) {
        if (version is null) return JsonNode.Parse("{}");
        return JsonNode.Parse($$"""{ "version": {{System.Text.Json.JsonSerializer.Serialize(version)}} }""");
    }

    [Test]
    public async Task Returns_null_when_response_node_is_null() {
        var result = VersionNudgeEmitter.BuildFragment(responseNode: null, currentCliVersion: "0.6.3");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_version_field_absent() {
        var result = VersionNudgeEmitter.BuildFragment(JsonNode.Parse("{}"), "0.6.3");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_version_field_empty_string() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion(""), "0.6.3");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_version_field_whitespace() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("   "), "0.6.3");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_version_field_unparseable() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("not-a-version"), "0.6.3");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_current_equals_server() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.5");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_current_is_strictly_newer() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.7.0");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_current_is_unknown_literal() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "unknown");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_prerelease_makes_cores_equal() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.5-alpha.1");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_build_metadata_makes_cores_equal() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.5+abcdef");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_fragment_when_server_strictly_newer() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.3");
        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task Fragment_contains_both_versions() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.3")!;
        await Assert.That(result).Contains("0.6.3");
        await Assert.That(result).Contains("0.6.5");
    }

    [Test]
    public async Task Fragment_contains_npm_install_command() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.3")!;
        await Assert.That(result).Contains("npm install -g @kurrent/kcap");
    }

    [Test]
    public async Task Fragment_is_plain_text_not_json() {
        var result = VersionNudgeEmitter.BuildFragment(ResponseWithVersion("0.6.5"), "0.6.3")!;
        await Assert.That(result.TrimStart().StartsWith("{")).IsFalse();
        await Assert.That(result).DoesNotContain("hookSpecificOutput");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/VersionNudgeEmitterTests/*"`

Expected: tests fail because `Capacitor.Cli.Core.VersionNudgeEmitter` does not exist (compile error).

- [ ] **Step 3: Implement the emitter**

Create `src/Capacitor.Cli.Core/VersionNudgeEmitter.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core;

/// <summary>
/// Vendor-neutral. Reads <c>response.version</c> from a Kurrent Capacitor
/// hook response and, when the server is strictly newer than the local CLI,
/// returns a short plain-text fragment that downstream vendor-specific
/// delivery shims (Claude Code today, Codex/Cursor later) wrap into their
/// native "additional context" channels.
///
/// Pure function: no I/O, no <c>Console</c>, no JSON envelope.
/// </summary>
public static class VersionNudgeEmitter {
    public static string? BuildFragment(JsonNode? responseNode, string currentCliVersion) {
        if (responseNode is not JsonObject obj) return null;

        string? serverVersion;
        try {
            serverVersion = obj["version"]?.GetValue<string>();
        } catch {
            return null;
        }

        if (!SemverCompare.IsNewer(serverVersion, currentCliVersion)) return null;

        return
            $"A newer kcap version is available: {currentCliVersion} → {serverVersion}.\n" +
            "Offer the user to upgrade by running: npm install -g @kurrent/kcap";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/VersionNudgeEmitterTests/*"`

Expected: all 14 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/VersionNudgeEmitter.cs test/Capacitor.Cli.Tests.Unit/VersionNudgeEmitterTests.cs
git commit -m "feat: add vendor-neutral VersionNudgeEmitter fragment builder (AI-768)"
```

---

## Task 4: Refactor `SessionGuidelinesEmitter` to `BuildFragment`

**Files:**
- Modify: `src/Capacitor.Cli/SessionGuidelinesEmitter.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/HookForwardingTests.cs` (gut the `SessionStartAdditionalContextTests` class)
- Create: `test/Capacitor.Cli.Tests.Unit/SessionGuidelinesEmitterTests.cs` (receives the seven existing methods, adapted)

`SessionGuidelinesEmitter` currently returns a full `hookSpecificOutput` envelope. We need it to return just the human-readable fragment so the new aggregator (Task 5) can combine it with the version-nudge fragment into one envelope. The existing tests in `HookForwardingTests.SessionStartAdditionalContextTests` actually test this emitter (despite the class name) — move them to a properly-named file and adapt them to assert on the fragment.

### Steps

- [ ] **Step 1: Note the current method count in the existing class**

Run: `awk '/^public class SessionStartAdditionalContextTests/,/^}$/' test/Capacitor.Cli.Tests.Unit/HookForwardingTests.cs | grep -c '\[Test\]'`

Note the number printed. The spec was written when it was 7. If it has drifted, every `[Test]` method in that class must be migrated below — do not skip any.

- [ ] **Step 2: Create the new test file and move every existing test method**

Create `test/Capacitor.Cli.Tests.Unit/SessionGuidelinesEmitterTests.cs`:

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class SessionGuidelinesEmitterTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task BuildFragment_returns_lessons_text_when_server_returns_top_clusters() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(
                        """
                        {
                          "top_clusters": [
                            { "category": "safety",          "text": "always close the writer" },
                            { "category": "maintainability", "text": "prefer JsonNode.Parse for AOT-safe string assignment" }
                          ]
                        }
                        """
                    )
            );

        using var client   = new HttpClient();
        using var content  = new StringContent("{}", Encoding.UTF8, "application/json");
        var       response = await client.PostAsync($"{_server.Url}/hooks/session-start", content);
        var       body     = await response.Content.ReadAsStringAsync();

        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);

        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("Recurring lessons");
        await Assert.That(fragment).Contains("- always close the writer");
        await Assert.That(fragment).Contains("- prefer JsonNode.Parse for AOT-safe string assignment");
        await Assert.That(fragment.TrimStart().StartsWith("{")).IsFalse(); // not a JSON envelope
        await Assert.That(fragment).DoesNotContain("hookSpecificOutput");
        await Assert.That(fragment).DoesNotContain("hookEventName");
    }

    [Test]
    public async Task BuildFragment_returns_null_when_top_clusters_absent() {
        var body     = """{ "slug": "some-resumed-session" }""";
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task BuildFragment_returns_null_when_disabled_flag_set() {
        var body = """{ "top_clusters": [ { "category": "safety", "text": "x" } ] }""";
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: true);
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task BuildFragment_returns_null_when_top_clusters_empty_array() {
        var body = """{ "top_clusters": [] }""";
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task BuildFragment_returns_null_when_top_clusters_is_object_not_array() {
        var body = """{ "top_clusters": { "category": "safety", "text": "x" } }""";
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task BuildFragment_returns_null_when_response_is_top_level_array() {
        var body = """[ { "category": "safety", "text": "x" } ]""";
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task BuildFragment_skips_entries_with_blank_text() {
        var body = """
                   {
                     "top_clusters": [
                       { "category": "safety", "text": ""    },
                       { "category": "safety", "text": "   " },
                       { "category": "safety", "text": "real lesson" }
                     ]
                   }
                   """;
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);
        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("- real lesson");
        // Exactly one bullet — the two blank entries are skipped.
        var bullets = fragment.Split('\n').Count(l => l.StartsWith("- "));
        await Assert.That(bullets).IsEqualTo(1);
    }
}
```

> If Step 1 reported a number other than 7, add the additional migrated test methods here using the same pattern (read body via the existing fixture, call `BuildFragment`, assert on plain text — no JSON envelope assertions). Verify zero `hookSpecificOutput`/`hookEventName` references afterwards: `grep -E 'hookSpecificOutput|hookEventName' test/Capacitor.Cli.Tests.Unit/SessionGuidelinesEmitterTests.cs` must print nothing.

- [ ] **Step 3: Empty out the existing class in `HookForwardingTests.cs`**

In `test/Capacitor.Cli.Tests.Unit/HookForwardingTests.cs`, find the `public class SessionStartAdditionalContextTests` (around line 145) and replace its body with a single empty placeholder — it will be repopulated in Task 5:

```csharp
public class SessionStartAdditionalContextTests {
    // Repopulated in Task 5 with aggregator tests.
}
```

Confirm the `IDisposable` interface, the `_server` field, and the `Dispose` method are removed — the new aggregator tests are pure-function and need no WireMock fixture.

- [ ] **Step 4: Refactor `SessionGuidelinesEmitter` to return a fragment**

Replace the entire contents of `src/Capacitor.Cli/SessionGuidelinesEmitter.cs` with:

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli;

/// <summary>
/// Builds the "recurring lessons from prior sessions" text fragment from a
/// server <c>/hooks/session-start</c> response body. Returns plain text, not
/// a JSON envelope — the caller (see <c>SessionStartAdditionalContext</c>)
/// joins this fragment with other fragments (e.g. the version-upgrade nudge)
/// and serializes a single Claude Code <c>hookSpecificOutput</c> envelope.
/// </summary>
static class SessionGuidelinesEmitter {
    /// <summary>
    /// Returns the lessons block text, or <c>null</c> when there is nothing
    /// to emit (no <c>top_clusters</c>, all empty, user opted out, malformed
    /// response).
    /// </summary>
    /// <param name="responseNode">The hook response body parsed as a <see cref="JsonNode"/>.</param>
    /// <param name="disabled">True when the user has set <c>disable_session_guidelines</c> on their active profile.</param>
    public static string? BuildFragment(JsonNode? responseNode, bool disabled) {
        if (disabled) return null;
        if (responseNode is not JsonObject obj) return null;
        if (obj["top_clusters"] is not JsonArray topClusters || topClusters.Count == 0) return null;

        var lines = new List<string>(topClusters.Count);

        foreach (var node in topClusters) {
            string? text = null;

            try {
                text = node?["text"]?.GetValue<string>();
            } catch {
                // Tolerate non-string/missing text entries.
            }

            if (!string.IsNullOrWhiteSpace(text)) lines.Add($"- {text}");
        }

        if (lines.Count == 0) return null;

        return "Recurring lessons from prior sessions in this repo (no action required unless relevant):\n"
             + string.Join("\n", lines);
    }
}
```

This removes both `BuildAdditionalContext` overloads. The call site in `ClaudeHookCommand` is updated in Task 6.

- [ ] **Step 5: Verify the build is broken at the old call site**

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`

Expected: one compile error in `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs` at the `SessionGuidelinesEmitter.BuildAdditionalContext(...)` call. This is intentional — Task 6 wires it through the new aggregator. Leave it broken for now and continue.

- [ ] **Step 6: Run only the new emitter tests against the new code**

(The CLI project doesn't compile yet, so the full test suite won't run. The unit test project still builds because it only depends on the emitter, which now has `BuildFragment`. Filter to just the new file.)

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SessionGuidelinesEmitterTests/*"`

Expected: all migrated tests pass.

If the test project also fails to build because `HookForwardingTests.cs` still references types removed from the class body, double-check Step 3 — the class body must be just the empty placeholder.

- [ ] **Step 7: Commit (build is intentionally broken at one call site)**

```bash
git add src/Capacitor.Cli/SessionGuidelinesEmitter.cs test/Capacitor.Cli.Tests.Unit/SessionGuidelinesEmitterTests.cs test/Capacitor.Cli.Tests.Unit/HookForwardingTests.cs
git commit -m "refactor: SessionGuidelinesEmitter returns text fragment, not envelope (AI-768)

WIP: ClaudeHookCommand call site is intentionally broken — fixed in
the next commit when SessionStartAdditionalContext lands."
```

---

## Task 5: `SessionStartAdditionalContext` aggregator (TDD)

**Files:**
- Modify: `test/Capacitor.Cli.Tests.Unit/HookForwardingTests.cs` (repopulate the now-empty class)
- Create: `src/Capacitor.Cli/SessionStartAdditionalContext.cs`

Joins non-null fragments with a blank-line separator and wraps them in a single Claude Code SessionStart `hookSpecificOutput` envelope. Returns `null` when every fragment is null/empty/whitespace so the caller emits nothing at all. This is Claude-Code-specific (lives in `Capacitor.Cli`, not Core).

### Steps

- [ ] **Step 1: Write the aggregator tests in the now-empty class**

Replace the placeholder body of `public class SessionStartAdditionalContextTests` in `test/Capacitor.Cli.Tests.Unit/HookForwardingTests.cs` with:

```csharp
public class SessionStartAdditionalContextTests {
    [Test]
    public async Task BuildEnvelope_returns_null_when_no_fragments() {
        var result = SessionStartAdditionalContext.BuildEnvelope();
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task BuildEnvelope_returns_null_when_all_fragments_null() {
        var result = SessionStartAdditionalContext.BuildEnvelope(null, null, null);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task BuildEnvelope_returns_null_when_all_fragments_empty_or_whitespace() {
        var result = SessionStartAdditionalContext.BuildEnvelope("", "   ", "\n\t");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task BuildEnvelope_wraps_single_fragment_in_envelope() {
        var result = SessionStartAdditionalContext.BuildEnvelope("hello world");

        await Assert.That(result).IsNotNull();
        var json = JsonNode.Parse(result!);
        await Assert.That(json!["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>()).IsEqualTo("SessionStart");
        await Assert.That(json["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>()).IsEqualTo("hello world");
    }

    [Test]
    public async Task BuildEnvelope_joins_multiple_fragments_with_blank_line_in_order() {
        var result = SessionStartAdditionalContext.BuildEnvelope("first", "second");

        await Assert.That(result).IsNotNull();
        var ctx = JsonNode.Parse(result!)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).IsEqualTo("first\n\nsecond");
    }

    [Test]
    public async Task BuildEnvelope_skips_null_and_blank_when_mixed_with_real_fragments() {
        var result = SessionStartAdditionalContext.BuildEnvelope(null, "first", "   ", "second", null);

        await Assert.That(result).IsNotNull();
        var ctx = JsonNode.Parse(result!)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).IsEqualTo("first\n\nsecond");
    }

    [Test]
    public async Task BuildEnvelope_produces_single_top_level_json_object() {
        var result = SessionStartAdditionalContext.BuildEnvelope("first", "second")!;
        // No second `{` after the first object closes — exactly one top-level JSON value.
        var firstClose = result.LastIndexOf('}');
        var afterClose = result[(firstClose + 1)..].Trim();
        await Assert.That(afterClose).IsEqualTo("");
    }
}
```

Make sure the test file still has `using System.Text.Json.Nodes;` at the top (it does today). No new usings needed for `SessionStartAdditionalContext` — same namespace as `Capacitor.Cli`-prefixed types, which is already imported.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SessionStartAdditionalContextTests/*"`

Expected: compile error — `SessionStartAdditionalContext` doesn't exist yet.

- [ ] **Step 3: Implement the aggregator**

Create `src/Capacitor.Cli/SessionStartAdditionalContext.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli;

/// <summary>
/// Claude-Code-specific. Joins zero or more plain-text fragments (produced
/// by vendor-neutral or Claude-specific contributors — e.g. the recurring
/// lessons emitter, the upgrade-nudge emitter) with a blank-line separator
/// and wraps them in a single Claude Code SessionStart
/// <c>hookSpecificOutput</c> envelope. Returns <c>null</c> when every
/// fragment is null/empty/whitespace so the caller writes nothing at all
/// to stdout.
///
/// One call site emits one JSON object — Claude Code hooks parse stdout
/// as a single JSON value with plain-text fallback, so two top-level
/// envelopes would not be parsed.
/// </summary>
static class SessionStartAdditionalContext {
    public static string? BuildEnvelope(params string?[] fragments) {
        if (fragments.Length == 0) return null;

        var kept = new List<string>(fragments.Length);
        foreach (var f in fragments) {
            if (!string.IsNullOrWhiteSpace(f)) kept.Add(f);
        }

        if (kept.Count == 0) return null;

        var ctx = string.Join("\n\n", kept);

        var envelope = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"]     = "SessionStart",
                ["additionalContext"] = ctx
            }
        };

        return envelope.ToJsonString();
    }
}
```

- [ ] **Step 4: Run the aggregator tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SessionStartAdditionalContextTests/*"`

Expected: all 7 aggregator tests pass.

The full project still has the broken `ClaudeHookCommand` call site from Task 4 — that's still expected at this point.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/SessionStartAdditionalContext.cs test/Capacitor.Cli.Tests.Unit/HookForwardingTests.cs
git commit -m "feat: add SessionStartAdditionalContext aggregator for single-envelope output (AI-768)"
```

---

## Task 6: Wire up `ClaudeHookCommand` to use the aggregator

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs`

Replace the existing single `SessionGuidelinesEmitter.BuildAdditionalContext(...)` emit (around lines 271–282) with a block that builds both fragments and runs them through the new aggregator. This is the point where the build stops being intentionally broken.

### Steps

- [ ] **Step 1: Inspect the current emit block**

Read `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs` around lines 240–295 to confirm the structure of the `case "session-start":` branch and the location of the existing emit block. The current block (lines 271–282) reads roughly:

```csharp
if (responseNode is not null) {
    try {
        var disabled = AppConfig.ResolvedProfile?.Profile?.DisableSessionGuidelines is true;
        var emission = SessionGuidelinesEmitter.BuildAdditionalContext(responseNode, disabled);

        if (emission is not null) {
            Console.WriteLine(emission);
        }
    } catch {
        // Best effort
    }
}
```

- [ ] **Step 2: Replace the emit block**

Replace that block with:

```csharp
if (responseNode is not null) {
    try {
        var disabled        = AppConfig.ResolvedProfile?.Profile?.DisableSessionGuidelines is true;
        var lessonsFragment = SessionGuidelinesEmitter.BuildFragment(responseNode, disabled);
        var nudgeFragment   = VersionNudgeEmitter.BuildFragment(responseNode, CapacitorVersion.Current());

        var envelope = SessionStartAdditionalContext.BuildEnvelope(lessonsFragment, nudgeFragment);

        if (envelope is not null) {
            Console.WriteLine(envelope);
        }
    } catch {
        // Best effort — never break session capture for hook output emission.
    }
}
```

`Capacitor.Cli.Core` is already imported at the top of the file (used for `HttpClientExtensions`, `CapacitorVersion`, etc.), so `VersionNudgeEmitter` and `SessionStartAdditionalContext` resolve without new `using`s.

- [ ] **Step 3: Build the whole solution**

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`

Expected: clean build, no compile errors. The intentionally broken state from Task 4/5 is now closed.

- [ ] **Step 4: Run the full unit test suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`

Expected: every test passes. Pay particular attention to:
- `SemverCompareTests` (Task 1) — 18 tests, all pass.
- `VersionNudgeEmitterTests` (Task 3) — 14 tests, all pass.
- `SessionGuidelinesEmitterTests` (Task 4) — 7 tests, all pass.
- `SessionStartAdditionalContextTests` in `HookForwardingTests.cs` (Task 5) — 7 tests, all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/ClaudeHookCommand.cs
git commit -m "feat: wire ClaudeHookCommand session-start to emit upgrade nudge (AI-768)"
```

---

## Task 7: Integration test — `ClaudeHookCommand.Handle` stdout capture

**Files:**
- Create: `test/Capacitor.Cli.Tests.Integration/ClaudeHookStdoutTests.cs`

`HookRoundTripTests` posts via raw `HttpClient` and never invokes `ClaudeHookCommand.Handle`, so it cannot observe the stdout emission this feature is built on. Add a new fixture that:

- Boots a `WireMockServer` and passes its URL directly to `ClaudeHookCommand.Handle`.
- Redirects `Console.Out` to a `StringWriter` for the test, restoring in `finally`.
- Marks every test `[NotInParallel]` because `Console.Out` is process-global.
- **Critically**, omits `transcript_path` from the payload so `WatcherManager.EnsureWatcherRunning` is not called (`ClaudeHookCommand.cs:290-292`). The watcher's `Process.Start` corrupts TUnit's Console capture (see `test/Capacitor.Cli.Tests.Unit/Codex/CodexHookCommandTests.cs:47-53`).

### Steps

- [ ] **Step 1: Read the existing integration fixture to understand the pattern**

Open `test/Capacitor.Cli.Tests.Integration/HookRoundTripTests.cs`. Note the project structure (TUnit, WireMock fixture in `IDisposable`, snake_case JSON options). The new file follows the same conventions.

- [ ] **Step 2: Write the failing fixture**

Create `test/Capacitor.Cli.Tests.Integration/ClaudeHookStdoutTests.cs`:

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Exercises <see cref="ClaudeHookCommand.Handle"/> end-to-end against a
/// WireMock server and captures stdout to validate the SessionStart
/// <c>hookSpecificOutput</c> envelope shape — including the
/// single-envelope invariant when both <c>top_clusters</c> and
/// <c>version</c> are present (AI-768).
///
/// Test payloads deliberately OMIT <c>transcript_path</c> so the
/// session-start path short-circuits before
/// <c>WatcherManager.EnsureWatcherRunning</c>; spawning the watcher's
/// child process corrupts TUnit's <c>Console</c> capture (see
/// <c>test/Capacitor.Cli.Tests.Unit/Codex/CodexHookCommandTests.cs:47-53</c>).
/// </summary>
public class ClaudeHookStdoutTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    static string SessionStartPayloadWithoutTranscriptPath() =>
        // No transcript_path, no session_id → WatcherManager spawn is skipped.
        """
        {
          "cwd":             "/tmp/test",
          "model":           "claude-sonnet-4-6",
          "source":          "startup",
          "hook_event_name": "session-start"
        }
        """;

    static async Task<string> CaptureStdoutAsync(Func<Task> action) {
        var original = Console.Out;
        var sw       = new StringWriter();
        Console.SetOut(sw);
        try {
            await action();
        } finally {
            Console.SetOut(original);
        }
        return sw.ToString();
    }

    [Test, NotInParallel("Console_Out")]
    public async Task Emits_nudge_envelope_when_server_returns_newer_version_only() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{ "version": "999.0.0" }""")
            );

        var stdout = await CaptureStdoutAsync(() =>
            ClaudeHookCommand.Handle(_server.Url, new StringReader(SessionStartPayloadWithoutTranscriptPath()))
        );

        // Exactly one parseable JSON object on stdout.
        var trimmed = stdout.Trim();
        await Assert.That(trimmed).IsNotEmpty();
        var json = JsonNode.Parse(trimmed);
        await Assert.That(json!["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>()).IsEqualTo("SessionStart");

        var ctx = json["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).Contains("999.0.0");
        await Assert.That(ctx).Contains("npm install -g @kurrent/kcap");
        await Assert.That(ctx).DoesNotContain("Recurring lessons");
    }

    [Test, NotInParallel("Console_Out")]
    public async Task Emits_combined_envelope_when_server_returns_top_clusters_and_newer_version() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(
                        """
                        {
                          "version": "999.0.0",
                          "top_clusters": [
                            { "category": "safety", "text": "always close the writer" }
                          ]
                        }
                        """
                    )
            );

        var stdout = await CaptureStdoutAsync(() =>
            ClaudeHookCommand.Handle(_server.Url, new StringReader(SessionStartPayloadWithoutTranscriptPath()))
        );

        var trimmed = stdout.Trim();

        // Single-envelope invariant: exactly one top-level JSON object.
        await Assert.That(trimmed).IsNotEmpty();
        var firstClose = trimmed.LastIndexOf('}');
        var afterClose = trimmed[(firstClose + 1)..].Trim();
        await Assert.That(afterClose).IsEqualTo("");

        var json = JsonNode.Parse(trimmed);
        var ctx  = json!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).Contains("Recurring lessons");
        await Assert.That(ctx).Contains("- always close the writer");
        await Assert.That(ctx).Contains("999.0.0");
        await Assert.That(ctx).Contains("npm install -g @kurrent/kcap");
    }

    [Test, NotInParallel("Console_Out")]
    public async Task Emits_nothing_when_server_returns_empty_object() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var stdout = await CaptureStdoutAsync(() =>
            ClaudeHookCommand.Handle(_server.Url, new StringReader(SessionStartPayloadWithoutTranscriptPath()))
        );

        await Assert.That(stdout.Trim()).IsEqualTo("");
    }
}
```

- [ ] **Step 3: Run the integration tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj --treenode-filter "/*/*/ClaudeHookStdoutTests/*"`

Expected: all three tests pass.

If a test hangs or returns extra non-JSON stdout: re-check that the payload omits `transcript_path`, and confirm Step 1's understanding of the `case "session-start":` block. If `WatcherManager.EnsureWatcherRunning` runs, the test will spawn a child process and stdout capture will be corrupted.

- [ ] **Step 4: Commit**

```bash
git add test/Capacitor.Cli.Tests.Integration/ClaudeHookStdoutTests.cs
git commit -m "test: integration coverage for ClaudeHookCommand stdout envelope (AI-768)"
```

---

## Task 8: Update README

**Files:**
- Modify: `README.md`

Per `CLAUDE.md`, user-facing CLI behaviour changes must update the README in the same PR. This change has no new command or flag — but it does introduce a new user-visible behaviour (in-agent upgrade prompts in Claude Code sessions). Add one bullet to `## What it records` to document it. Phrase it as **"in Claude Code sessions"** so it doesn't imply equivalent behaviour for Cursor or Codex.

### Steps

- [ ] **Step 1: Add a bullet under "What it records"**

In `README.md`, find the bullet list under `## What it records` (currently ending with "Repository context — git repo, branch, and PR linkage" around line 113) and append one more bullet at the bottom of the list:

```markdown
- **In-agent upgrade prompts** — in Claude Code sessions, when the server is running a newer kcap release than the local CLI, additional context is injected into the session so the agent can offer the user an upgrade via `npm install -g @kurrent/kcap`. The stderr `kcap` update hint continues to fire for direct command-line use.
```

(Cursor and Codex sessions are intentionally not mentioned — those vendors have follow-up tickets.)

- [ ] **Step 2: Confirm no other README sections need touching**

Grep for surfaces that might describe SessionStart hook output today:

```bash
grep -n -i "additional context\|hookSpecificOutput\|SessionStart" README.md
```

Expected: no hits. The README does not currently describe hook output internals, so no other section needs updating.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: README — Claude Code sessions surface kcap upgrade prompts (AI-768)"
```

---

## Task 9: AOT publish sanity check

**Files:**
- (no file changes expected)

`CLAUDE.md` flags this as a recurring trap: `dotnet build` does not surface IL3050/IL2026 trimming/AOT warnings — only `dotnet publish -c Release` does. This feature uses only `JsonNode` (already used elsewhere) and pure value-type comparison, so we don't expect new warnings, but verify.

### Steps

- [ ] **Step 1: AOT-publish the CLI**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`

Expected: no output (no IL3050/IL2026 warnings). If any line is printed, identify the offending site — most likely a `JsonNode` collection-expression `[a, b]` or `JsonSerializer` reflective overload. Fix per `CLAUDE.md` guidance (`new JsonArray(a, b)` constructor; `JsonSerializerContext`-backed overloads) before continuing.

- [ ] **Step 2: macOS native-binary smoke test (only on macOS)**

If you're on macOS, copy the published binary somewhere and run it to confirm it isn't trapped by code-signing:

```bash
codesign --force --sign - src/Capacitor.Cli/bin/Release/net10.0/osx-arm64/publish/kcap
src/Capacitor.Cli/bin/Release/net10.0/osx-arm64/publish/kcap --version
```

Expected: prints `kcap <version>` and exits 0.

(On Linux/Windows, skip Step 2.)

- [ ] **Step 3: Final commit if anything changed**

If Step 1 surfaced a warning that required a code change, commit it:

```bash
git add -p
git commit -m "fix: AOT-safe JsonNode usage in <site> (AI-768)"
```

Otherwise, no commit needed.

---

## Done criteria

- [ ] All tasks above completed in order.
- [ ] `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` is green.
- [ ] `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj` is green.
- [ ] `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing.
- [ ] A fresh manual smoke (point `kcap` at a server returning `{"version": "999.0.0"}` from `/hooks/session-start`, fire a Claude SessionStart) shows the nudge appearing in the agent's context.
- [ ] One PR contains: code in `src/`, tests in `test/`, README update, and references AI-768 in the description.
