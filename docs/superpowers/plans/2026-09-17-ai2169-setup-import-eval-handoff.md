# Setup Import: Everything on Disk, Newest First, Background Remainder, Eval-Watch Handoff — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `kcap setup`'s terminal import step import every session on the machine, newest first, with ~5 in the foreground and the rest in a detached child, then hand the user to any of the nine coding-agent CLIs running a new eval-watch skill over the run's own cohort.

**Architecture:** Ordering and foreground selection are pure functions inside `ImportCommand` (`ImportOrdering`, `ForegroundSelection`) reported through two new callbacks (`onSelected`, and a `Partition` on `onFinished`). `SetupCommand` composes the step from injected collaborators — `ISetupImportRunner` (now returning a totalized `SetupImportRun`), a new `IBackgroundImportSpawner` and a new `IHandoffAgentLauncher` — plus a handoff decision table and a per-run handoff file. The skill is Markdown in the kcap plugin, reading the file and the analytics MCP under global scope over cohort ids only.

**Tech Stack:** .NET 10 (`dotnet` 10.0.302 on PATH), NativeAOT publish, Spectre.Console, System.Text.Json `JsonObject` (AOT-safe), TUnit on Microsoft Testing Platform, WireMock.Net, existing test helpers (`TempHome`, `TempConfigRoot`, `TempDir`, `EnvScope`, `ConsoleOutput`, `TestHarnesses`, `TestBinaries`, `FakeProcessStarter`, `FakeImportRunner`, `Resolutions`, `FixedCapacitorHttpClient`).

**Spec:** `docs/superpowers/specs/2026-09-17-ai2169-setup-import-eval-handoff-design.md` — the plan argues from the spec; read both. Issue: AI-2169 / GitHub #641. Work in the worktree `/Users/tony/dev/kcap-cli-wt/ai-2169`, branch `tonyyoung/ai-2169-setup-import-eval-handoff`. Commit locally; do not push.

## Global Constraints

- Build and test with the `dotnet` on PATH (10.0.302). Unit tests: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/<ClassName>/*"`; Core tests: the same against `test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj`. Never run the whole suite locally; CI does that.
- After every code task: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj` is warning-free for the files you touched. Before the last commit: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing. JSON is built with `JsonObject` and the `(JsonNode?)` cast, never `JsonValue.Create` over generics or reflection serialization.
- One type per file, named after the type (an enum plus its extension methods may share a file). New comments follow AGENTS.md: scarce, no history, no ticket ids, no review narration.
- No new public flags on `kcap import`; its behaviour changes only in dispatch order. `--no-prompt` setup: full synchronous import over `ImportScope.All`, no cap, no child, no handoff file, no picker.
- Pinned strings, verbatim everywhere including tests: `Import past sessions from this machine?` (prompt), `Follow my kcap import` (handoff prompt), `Start kcap guided tour` (existing tour prompt). Internal env vars: `KCAP_IMPORT_DETACHED_LOG`, `KCAP_IMPORT_DEFAULT_VISIBILITY`. Existing: `KCAP_CONFIG_DIR` (`ConfigRoot.ConfigDirEnvVar`), `KCAP_PROFILE` (`ProfileOverrides.ProfileVar`), `KCAP_URL` (`ProfileOverrides.UrlVar`).
- Within-chain order (ascending `ChainTimestamp`, then session id ordinal) must not change; it feeds `previous_session_id` links. `ImportWorkerCount = 4` and the TTY renderer stay in lockstep; the chain/routed two-phase split stays.
- Foreground cap: 5 sessions. Handoff cohort cap: 500 ids. Handoff-file retention: 7 days. Background readiness window: 1500 ms. Agent launch-failure window: 2000 ms. Skill poll cadence 30 s, deadline 10 min, budget 20 queries per poll, batch floor `ceil(N/20)`, initial batch 100.
- Session-id grammar: `^[A-Za-z0-9_-]{1,128}$`.
- Never assert that an env var is *absent* from a `ProcessStartInfo` unless the test seeds the parent environment with it and the removal is the behaviour under test (the repo's `.envrc` pollutes).
- User-facing text says "most recent sessions are prioritized", never "arrive newest-first".
- README.md is updated in the same branch (Task 15).

## File Structure

New (kcap-cli, `src/Capacitor.Cli/Commands/` unless noted):

- `ImportOrdering.cs` — `CandidateTimestamp`, chain dispatch comparator, routed comparator, candidate comparator. Pure.
- `RoutedUnit.cs` — a routed parent with its correlated children; `RoutedUnits.Build(routed)`; eligibility.
- `ForegroundSelection.cs` — `SelectForeground(chains, units, max)`; returns selected chains, selected units, candidate ids, remainder flag.
- `ImportRunSelection.cs`, `ImportRunPartition.cs` — the two report records.
- `DetachedImportLog.cs` — the child-side env contract (`KCAP_IMPORT_DETACHED_LOG`, `KCAP_IMPORT_DEFAULT_VISIBILITY`).
- `SetupImportRun.cs`, `SetupImportDiscovery.cs` — the runner's totalized results.
- `ForegroundImportOutcome.cs` — derived from `SetupImportRun`.
- `IBackgroundImportSpawner.cs`, `BackgroundImportSpawner.cs`, `BackgroundImportStatus.cs`, `BackgroundImportLaunch.cs`.
- `ImportHandoffFile.cs`, `HandoffSuppressedReason.cs`, `HandoffCohort.cs`.
- `HandoffDecision.cs` — the §4 precedence table.
- `HandoffLaunchRecipe.cs`, `HandoffVendorEligibility.cs`, `IHandoffAgentLauncher.cs`, `HandoffAgentLauncher.cs`, `HandoffLaunchResult.cs`.
- `src/Capacitor.Cli.Core/OwnerOnlyFile.cs` — `CreateNew` with owner-only mode.
- `kcap/skills/eval-watch/SKILL.md`.

Modified: `ImportCommand.cs` (ordering, selection, partition, `onSelected`, `maxSessions`), `ImportInvocation.cs`, `ISetupImportRunner.cs`, `SetupImportRunner.cs`, `SetupDecisions.cs`, `SetupCommand.cs` (step 6, constants, Next-steps item, constructor), `CommandServices.cs` (DI), `Program.cs` (import case reads the detached contract), `src/Capacitor.Cli.Core/AgentsSkillsInstaller.cs` (`SourceNames`), `src/Capacitor.Cli.Core/Resources/help-plugin.txt`, `README.md`.

Tests (`test/Capacitor.Cli.Tests.Unit/Commands/` unless noted): `ImportOrderingTests.cs`, `RoutedUnitTests.cs`, `ForegroundSelectionTests.cs`, `ImportSelectionReportingTests.cs`, `DetachedImportLogTests.cs`, `SetupImportRunnerTests.cs`, `ForegroundImportOutcomeTests.cs`, `BackgroundImportSpawnerTests.cs`, `ImportHandoffFileTests.cs`, `HandoffDecisionTests.cs`, `HandoffLaunchRecipeTests.cs`, `HandoffVendorEligibilityTests.cs`, `HandoffAgentLauncherTests.cs`, additions to `SetupCommandTests.cs`, `SetupDecisionsTests.cs`, `ImportChainTests.cs`, `FakeImportRunner.cs`; new fakes `FakeBackgroundImportSpawner.cs`, `FakeHandoffAgentLauncher.cs`; `test/Capacitor.Cli.Core.Tests.Unit/OwnerOnlyFileTests.cs`.

---

### Task 1: Newest-first ordering and the candidate comparator

**Files:**
- Create: `src/Capacitor.Cli/Commands/ImportOrdering.cs`
- Modify: `src/Capacitor.Cli/Commands/ImportCommand.cs` — `BuildImportChains` (~:2929), `ChainTimestamp` (~:2059), the routed sort before `Parallel.ForEachAsync` (~:1803 and ~:1853)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/ImportOrderingTests.cs`; extend `test/Capacitor.Cli.Tests.Unit/Commands/ImportChainTests.cs`

**Interfaces:**
- Consumes: `ImportCommand.SessionClassification` (`SessionId`, `FilePath`, `Meta.FirstTimestamp`, `Status`), `ImportCommand.ChainTimestamp(SessionClassification)` (make it `internal static`).
- Produces: `internal static class ImportOrdering` with
  `static DateTimeOffset CandidateTimestamp(SessionClassification c)` (= `ChainTimestamp`),
  `static IComparer<List<SessionClassification>> ChainDispatch` (descending max timestamp; ties descending max session id ordinal; `MinValue` last then same key),
  `static IComparer<SessionClassification> RoutedDispatch` (descending timestamp; ties descending id; `MinValue` last),
  `static IComparer<SessionClassification> Candidate` (same rule as `RoutedDispatch`, applied to any classification).

- [ ] **Step 1: Write the failing tests**

Append to `ImportChainTests.cs` (reuse its `Classify` helper):

```csharp
[Test]
public async Task BuildImportChains_dispatches_newest_chain_first_and_keeps_members_ascending() {
    var t = (string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
    var classifications = new List<ImportCommand.SessionClassification> {
        Classify("old1", ImportCommand.ClassificationStatus.New, slug: "old", ts: t("2026-01-01T00:00:00Z")),
        Classify("old2", ImportCommand.ClassificationStatus.New, slug: "old", ts: t("2026-01-02T00:00:00Z")),
        Classify("new1", ImportCommand.ClassificationStatus.New, slug: "new", ts: t("2026-03-01T00:00:00Z")),
        Classify("new2", ImportCommand.ClassificationStatus.New, slug: "new", ts: t("2026-03-02T00:00:00Z")),
        Classify("solo",  ImportCommand.ClassificationStatus.New, ts: t("2026-02-01T00:00:00Z")),
    };

    var chains = ImportCommand.BuildImportChains(classifications);

    await Assert.That(chains.Select(c => c[0].SessionId).ToList()).IsEquivalentTo(["new1", "solo", "old1"]);
    await Assert.That(chains[0].Select(c => c.SessionId).ToList()).IsEquivalentTo(["new1", "new2"]);
    await Assert.That(chains[0][0].SessionId).IsEqualTo("new1");
    await Assert.That(chains[2][0].SessionId).IsEqualTo("old1");
}

[Test]
public async Task BuildImportChains_equal_max_timestamps_break_by_max_session_id_descending() {
    var ts = DateTimeOffset.Parse("2026-03-01T00:00:00Z", CultureInfo.InvariantCulture);
    var classifications = new List<ImportCommand.SessionClassification> {
        Classify("a", ImportCommand.ClassificationStatus.New, slug: "x", ts: ts),
        Classify("z", ImportCommand.ClassificationStatus.New, slug: "y", ts: ts),
    };

    var chains = ImportCommand.BuildImportChains(classifications);

    await Assert.That(chains[0][0].SessionId).IsEqualTo("z");
    await Assert.That(chains[1][0].SessionId).IsEqualTo("a");
}

[Test]
public async Task BuildImportChains_same_corpus_twice_yields_identical_order() {
    var rnd = new Random(7);
    var classifications = Enumerable.Range(0, 40).Select(i => Classify(
        $"s{i:00}", ImportCommand.ClassificationStatus.New,
        slug: i % 5 == 0 ? null : $"slug{i % 7}",
        ts: DateTimeOffset.UnixEpoch.AddMinutes(rnd.Next(0, 5000)))).ToList();

    var first  = ImportCommand.BuildImportChains(classifications).SelectMany(c => c).Select(c => c.SessionId).ToList();
    var second = ImportCommand.BuildImportChains([.. classifications.AsEnumerable().Reverse()]).SelectMany(c => c).Select(c => c.SessionId).ToList();

    await Assert.That(first).IsEquivalentTo(second);
    for (var i = 0; i < first.Count; i++) await Assert.That(first[i]).IsEqualTo(second[i]);
}
```

Create `ImportOrderingTests.cs`:

```csharp
using System.Globalization;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportOrderingTests {
    static ImportCommand.SessionClassification Routed(string id, string? ts, ImportCommand.ClassificationStatus status = ImportCommand.ClassificationStatus.New) => new() {
        SessionId  = id,
        FilePath   = "",
        EncodedCwd = "",
        Meta       = new SessionMetadata { FirstTimestamp = ts is null ? null : DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture) },
        Status     = status,
    };

    [Test]
    public async Task Candidate_order_is_newest_first_with_unresolvable_timestamps_last() {
        var items = new List<ImportCommand.SessionClassification> {
            Routed("mid",   "2026-02-01T00:00:00Z"),
            Routed("none",  null),                    // no timestamp, no file → MinValue
            Routed("new",   "2026-03-01T00:00:00Z"),
            Routed("probe", "2026-02-15T00:00:00Z", ImportCommand.ClassificationStatus.ProbeError),
        };

        items.Sort(ImportOrdering.Candidate);

        await Assert.That(items.Select(c => c.SessionId).ToList()).IsEquivalentTo(["new", "probe", "mid", "none"]);
        await Assert.That(items[0].SessionId).IsEqualTo("new");
        await Assert.That(items[3].SessionId).IsEqualTo("none");
    }

    [Test]
    public async Task Candidate_order_breaks_equal_timestamps_by_session_id_descending() {
        var items = new List<ImportCommand.SessionClassification> {
            Routed("a", "2026-03-01T00:00:00Z"),
            Routed("b", "2026-03-01T00:00:00Z"),
        };

        items.Sort(ImportOrdering.Candidate);

        await Assert.That(items[0].SessionId).IsEqualTo("b");
    }

    [Test]
    public async Task Routed_dispatch_uses_the_same_rule_as_candidates() {
        var a = Routed("a", "2026-03-01T00:00:00Z");
        var b = Routed("b", "2026-01-01T00:00:00Z");

        await Assert.That(ImportOrdering.RoutedDispatch.Compare(a, b)).IsLessThan(0);
        await Assert.That(ImportOrdering.Candidate.Compare(a, b)).IsLessThan(0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ImportOrderingTests/*"` and the same with `ImportChainTests`.
Expected: compile error, `ImportOrdering` does not exist; the chain tests fail on order.

- [ ] **Step 3: Implement `ImportOrdering` and rewire `BuildImportChains`**

Create `src/Capacitor.Cli/Commands/ImportOrdering.cs`:

```csharp
namespace Capacitor.Cli.Commands;

/// <summary>Newest-first comparators. Dispatch order is a priority, not a landing order: chains
/// and routed sessions still run in two phases and four workers finish out of order.</summary>
internal static class ImportOrdering {
    public static DateTimeOffset CandidateTimestamp(ImportCommand.SessionClassification c) =>
        ImportCommand.ChainTimestamp(c);

    /// <summary>Descending timestamp, ties descending session id; an unresolvable timestamp
    /// (<see cref="DateTimeOffset.MinValue"/>) sorts last, then by the same key.</summary>
    public static IComparer<ImportCommand.SessionClassification> Candidate { get; } =
        Comparer<ImportCommand.SessionClassification>.Create((x, y) => CompareKeys(
            (CandidateTimestamp(x), x.SessionId), (CandidateTimestamp(y), y.SessionId)));

    public static IComparer<ImportCommand.SessionClassification> RoutedDispatch => Candidate;

    /// <summary>Chains compare by their max member timestamp, ties by their max session id.</summary>
    public static IComparer<List<ImportCommand.SessionClassification>> ChainDispatch { get; } =
        Comparer<List<ImportCommand.SessionClassification>>.Create((x, y) => CompareKeys(ChainKey(x), ChainKey(y)));

    static (DateTimeOffset, string) ChainKey(List<ImportCommand.SessionClassification> chain) =>
        (chain.Max(CandidateTimestamp), chain.Max(c => c.SessionId, StringComparer.Ordinal)!);

    static int CompareKeys((DateTimeOffset Ts, string Id) a, (DateTimeOffset Ts, string Id) b) {
        var aUnknown = a.Ts == DateTimeOffset.MinValue;
        var bUnknown = b.Ts == DateTimeOffset.MinValue;
        if (aUnknown != bUnknown) return aUnknown ? 1 : -1;

        var byTs = b.Ts.CompareTo(a.Ts);
        return byTs != 0 ? byTs : string.CompareOrdinal(b.Id, a.Id);
    }
}
```

In `ImportCommand.cs`: change `static DateTimeOffset ChainTimestamp(` to `internal static DateTimeOffset ChainTimestamp(`. Replace the body of `BuildImportChains` so the within-chain order is unchanged and the cross-chain order uses the comparator:

```csharp
internal static List<List<SessionClassification>> BuildImportChains(List<SessionClassification> classifications) {
    var importable = classifications
        .Where(c => c.Status is ClassificationStatus.New or ClassificationStatus.Partial)
        .ToList();

    var chains = importable
        .Where(c => c.Meta.Slug is not null)
        .GroupBy(c => c.Meta.Slug!, StringComparer.Ordinal)
        .Select(group => group.OrderBy(ChainTimestamp).ThenBy(c => c.SessionId, StringComparer.Ordinal).ToList())
        .ToList();
    chains.AddRange(importable.Where(c => c.Meta.Slug is null).Select(solo => (List<SessionClassification>)[solo]));

    chains.Sort(ImportOrdering.ChainDispatch);

    return chains;
}
```

At both routed `Parallel.ForEachAsync` sites (~:1803 TTY and ~:1853 non-TTY) sort the routed list first, once, right after `routed = ReconcileOrphanedCursorSubagentChildren(routed);` (~:1325):

```csharp
routed.Sort(ImportOrdering.RoutedDispatch);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run both filters from Step 2. Expected: PASS. Also run `--treenode-filter "/*/*/ImportChainsTests/*"` (existing chain tests must still pass).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/ImportOrdering.cs src/Capacitor.Cli/Commands/ImportCommand.cs test/Capacitor.Cli.Tests.Unit/Commands/ImportOrderingTests.cs test/Capacitor.Cli.Tests.Unit/Commands/ImportChainTests.cs
git commit -m "[AI-2169] Dispatch the newest import chains and routed sessions first"
```

---

### Task 2: Routed units and foreground selection

**Files:**
- Create: `src/Capacitor.Cli/Commands/RoutedUnit.cs`, `src/Capacitor.Cli/Commands/ForegroundSelection.cs`, `src/Capacitor.Cli/Commands/ImportRunSelection.cs`, `src/Capacitor.Cli/Commands/ImportRunPartition.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/RoutedUnitTests.cs`, `test/Capacitor.Cli.Tests.Unit/Commands/ForegroundSelectionTests.cs`

**Interfaces:**
- Consumes: `SessionClassification.SourceMeta` keys `IsSubagentChild` (bool), `ParentSessionId` (string), `SubagentChildren` (`List<CursorImportSource.CursorSubagentChild>`); `ImportOrdering` from Task 1.
- Produces:
  - `internal sealed record RoutedUnit(SessionClassification Parent, IReadOnlyList<SessionClassification> Children)` with `bool Eligible => Parent.Status is New or Partial`; `internal static class RoutedUnits { static List<RoutedUnit> Build(IReadOnlyList<SessionClassification> routed); }` — children are the routed rows whose `ParentSessionId` names another routed row; every other routed row is a unit of its own.
  - `internal sealed record ImportRunSelection(IReadOnlyList<string> RunCandidateIds, IReadOnlyList<string> SelectedIds, bool RemainderExists)`.
  - `internal sealed record ImportRunPartition(IReadOnlyList<string> SucceededIds, IReadOnlyList<string> SkippedIds, IReadOnlyList<string> FailedIds)`.
  - `internal sealed record ForegroundPlan(List<List<SessionClassification>> Chains, List<SessionClassification> Routed, ImportRunSelection Selection)`.
  - `internal static class ForegroundSelection { static ForegroundPlan Select(List<List<SessionClassification>> chains, List<SessionClassification> routed, IReadOnlyList<SessionClassification> all, int maxSessions); }` — `all` is every post-capture-scope classification (for `ProbeError` candidates and the remainder test).

- [ ] **Step 1: Write the failing tests**

`RoutedUnitTests.cs`:

```csharp
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class RoutedUnitTests {
    internal static ImportCommand.SessionClassification Row(
            string id, ImportCommand.ClassificationStatus status, string? parent = null, string? ts = null) => new() {
        SessionId  = id,
        FilePath   = "",
        EncodedCwd = "",
        Meta       = new SessionMetadata { FirstTimestamp = ts is null ? null : DateTimeOffset.Parse(ts, System.Globalization.CultureInfo.InvariantCulture) },
        Status     = status,
        SourceMeta = parent is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?> { ["IsSubagentChild"] = true, ["ParentSessionId"] = parent },
    };

    [Test]
    public async Task A_child_whose_parent_is_routed_joins_the_parents_unit() {
        var units = RoutedUnits.Build([
            Row("p", ImportCommand.ClassificationStatus.New),
            Row("c", ImportCommand.ClassificationStatus.New, parent: "p"),
            Row("solo", ImportCommand.ClassificationStatus.New),
        ]);

        await Assert.That(units.Count).IsEqualTo(2);
        var p = units.Single(u => u.Parent.SessionId == "p");
        await Assert.That(p.Children.Select(c => c.SessionId).ToList()).IsEquivalentTo(["c"]);
        await Assert.That(p.Eligible).IsTrue();
    }

    [Test]
    public async Task An_orphan_is_its_own_unit() {
        var units = RoutedUnits.Build([Row("c", ImportCommand.ClassificationStatus.New, parent: "missing")]);

        await Assert.That(units.Count).IsEqualTo(1);
        await Assert.That(units[0].Parent.SessionId).IsEqualTo("c");
    }

    [Test]
    public async Task A_unit_with_an_already_loaded_parent_is_not_eligible_whatever_its_children() {
        var units = RoutedUnits.Build([
            Row("p", ImportCommand.ClassificationStatus.AlreadyLoaded),
            Row("c", ImportCommand.ClassificationStatus.New, parent: "p"),
        ]);

        await Assert.That(units.Single().Eligible).IsFalse();
    }
}
```

`ForegroundSelectionTests.cs`:

```csharp
using System.Globalization;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ForegroundSelectionTests {
    static ImportCommand.SessionClassification File(string id, string slug, string ts, ImportCommand.ClassificationStatus status = ImportCommand.ClassificationStatus.New) => new() {
        SessionId = id, FilePath = $"/tmp/{id}.jsonl", EncodedCwd = "-tmp", Status = status,
        Meta = new SessionMetadata { Slug = slug, FirstTimestamp = DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture) },
    };
    static ImportCommand.SessionClassification Routed(string id, string ts, ImportCommand.ClassificationStatus status = ImportCommand.ClassificationStatus.New, string? parent = null) =>
        RoutedUnitTests.Row(id, status, parent, ts);

    static ForegroundPlan Select(IReadOnlyList<ImportCommand.SessionClassification> all, int max) {
        var fileBased = all.Where(c => !string.IsNullOrEmpty(c.FilePath)).ToList();
        var routed    = all.Where(c => string.IsNullOrEmpty(c.FilePath)
                             && c.Status is ImportCommand.ClassificationStatus.New or ImportCommand.ClassificationStatus.Partial or ImportCommand.ClassificationStatus.AlreadyLoaded).ToList();
        routed.Sort(ImportOrdering.RoutedDispatch);
        return ForegroundSelection.Select(ImportCommand.BuildImportChains(fileBased), routed, all, max);
    }

    [Test]
    public async Task Takes_whole_chains_newest_first_until_the_cap_and_overshoots_by_the_boundary_chain() {
        var all = new List<ImportCommand.SessionClassification> {
            File("n1", "new", "2026-03-01T00:00:00Z"), File("n2", "new", "2026-03-02T00:00:00Z"),
            File("m1", "mid", "2026-02-01T00:00:00Z"), File("m2", "mid", "2026-02-02T00:00:00Z"),
            File("m3", "mid", "2026-02-03T00:00:00Z"), File("m4", "mid", "2026-02-04T00:00:00Z"),
            File("o1", "old", "2026-01-01T00:00:00Z"),
        };

        var plan = Select(all, max: 5);

        await Assert.That(plan.Selection.SelectedIds).IsEquivalentTo(["n1", "n2", "m1", "m2", "m3", "m4"]);
        await Assert.That(plan.Selection.RemainderExists).IsTrue();
        await Assert.That(plan.Selection.RunCandidateIds).IsEquivalentTo(["n1", "n2", "m1", "m2", "m3", "m4", "o1"]);
        await Assert.That(plan.Selection.RunCandidateIds[0]).IsEqualTo("n2");
    }

    [Test]
    public async Task Tops_up_with_eligible_routed_units_counting_one_per_parent() {
        var all = new List<ImportCommand.SessionClassification> {
            File("n1", "new", "2026-03-01T00:00:00Z"),
            Routed("p", "2026-02-20T00:00:00Z"),
            Routed("c1", "2026-02-20T00:00:00Z", parent: "p"),
            Routed("c2", "2026-02-20T00:00:00Z", parent: "p", status: ImportCommand.ClassificationStatus.AlreadyLoaded),
            Routed("r2", "2026-02-10T00:00:00Z"),
        };

        var plan = Select(all, max: 2);

        await Assert.That(plan.Selection.SelectedIds).IsEquivalentTo(["n1", "p"]);
        await Assert.That(plan.Routed.Select(c => c.SessionId).ToList()).IsEquivalentTo(["p", "c1", "c2"]);
        await Assert.That(plan.Selection.RunCandidateIds).IsEquivalentTo(["n1", "p", "r2"]);
        await Assert.That(plan.Selection.RemainderExists).IsTrue();
    }

    [Test]
    public async Task An_already_loaded_parent_with_a_new_child_is_never_selected_and_contributes_no_candidate() {
        var all = new List<ImportCommand.SessionClassification> {
            Routed("p", "2026-03-01T00:00:00Z", status: ImportCommand.ClassificationStatus.AlreadyLoaded),
            Routed("c", "2026-03-01T00:00:00Z", parent: "p"),
        };

        var plan = Select(all, max: 5);

        await Assert.That(plan.Selection.SelectedIds).IsEmpty();
        await Assert.That(plan.Routed).IsEmpty();
        await Assert.That(plan.Selection.RunCandidateIds).IsEmpty();
        await Assert.That(plan.Selection.RemainderExists).IsTrue();
    }

    [Test]
    public async Task A_correlated_probe_error_child_is_not_a_candidate_but_an_orphaned_one_is() {
        var all = new List<ImportCommand.SessionClassification> {
            Routed("p", "2026-03-01T00:00:00Z"),
            Routed("cp", "2026-03-01T00:00:00Z", parent: "p", status: ImportCommand.ClassificationStatus.ProbeError),
            Routed("orphan", "2026-03-01T00:00:00Z", parent: "gone", status: ImportCommand.ClassificationStatus.ProbeError),
        };

        var plan = Select(all, max: 5);

        await Assert.That(plan.Selection.RunCandidateIds).IsEquivalentTo(["p", "orphan"]);
        await Assert.That(plan.Selection.RemainderExists).IsTrue();
    }

    [Test]
    public async Task Remainder_is_false_when_everything_actionable_was_selected() {
        var all = new List<ImportCommand.SessionClassification> {
            File("n1", "new", "2026-03-01T00:00:00Z"),
            File("done", "x", "2026-01-01T00:00:00Z", ImportCommand.ClassificationStatus.AlreadyLoaded),
            Routed("p", "2026-02-20T00:00:00Z"),
            Routed("c", "2026-02-20T00:00:00Z", parent: "p", status: ImportCommand.ClassificationStatus.AlreadyLoaded),
        };

        var plan = Select(all, max: 5);

        await Assert.That(plan.Selection.RemainderExists).IsFalse();
    }

    [Test]
    public async Task Routed_replay_rows_alone_are_remainder_but_file_based_already_loaded_rows_are_not() {
        var cursorOnly = Select([Routed("p", "2026-03-01T00:00:00Z", status: ImportCommand.ClassificationStatus.AlreadyLoaded)], max: 5);
        var claudeOnly = Select([File("a", "x", "2026-03-01T00:00:00Z", ImportCommand.ClassificationStatus.AlreadyLoaded)], max: 5);

        await Assert.That(cursorOnly.Selection.RemainderExists).IsTrue();
        await Assert.That(claudeOnly.Selection.RemainderExists).IsFalse();
    }

    [Test]
    public async Task Candidates_are_capped_at_500_newest_in_candidate_order() {
        var all = Enumerable.Range(0, 600).Select(i =>
            File($"s{i:000}", $"slug{i}", DateTimeOffset.UnixEpoch.AddDays(i).ToString("O"))).ToList();

        var plan = Select(all, max: 5);

        await Assert.That(plan.Selection.RunCandidateIds.Count).IsEqualTo(600);
        await Assert.That(plan.Selection.RunCandidateIds[0]).IsEqualTo("s599");
    }
}
```

(The 500 cut belongs to the handoff file, Task 8; the selection reports every candidate.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: filters `RoutedUnitTests` and `ForegroundSelectionTests`. Expected: compile errors for the new types.

- [ ] **Step 3: Implement the four types**

`ImportRunSelection.cs`:

```csharp
namespace Capacitor.Cli.Commands;

/// <summary>Published once selection is fixed and before any import work runs.</summary>
internal sealed record ImportRunSelection(
    IReadOnlyList<string> RunCandidateIds,
    IReadOnlyList<string> SelectedIds,
    bool                  RemainderExists) {
    public static ImportRunSelection Empty { get; } = new([], [], RemainderExists: false);
}
```

`ImportRunPartition.cs`:

```csharp
namespace Capacitor.Cli.Commands;

/// <summary>How each selected id's own import call ended. Succeeded includes a call that returned
/// Skipped after posting a carried child's content: the session landed work either way.</summary>
internal sealed record ImportRunPartition(
    IReadOnlyList<string> SucceededIds,
    IReadOnlyList<string> SkippedIds,
    IReadOnlyList<string> FailedIds) {
    public static ImportRunPartition Empty { get; } = new([], [], []);
}
```

`RoutedUnit.cs`:

```csharp
namespace Capacitor.Cli.Commands;

/// <summary>One routed server session: a parent plus the correlated children its own call imports
/// inline. Children never become top-level sessions, so a unit counts as one everywhere.</summary>
internal sealed record RoutedUnit(
    ImportCommand.SessionClassification                Parent,
    IReadOnlyList<ImportCommand.SessionClassification> Children) {
    public bool Eligible => Parent.Status is ImportCommand.ClassificationStatus.New or ImportCommand.ClassificationStatus.Partial;

    public IEnumerable<ImportCommand.SessionClassification> Members => Children.Prepend(Parent);
}

internal static class RoutedUnits {
    public static List<RoutedUnit> Build(IReadOnlyList<ImportCommand.SessionClassification> routed) {
        var ids      = routed.Select(c => c.SessionId).ToHashSet(StringComparer.Ordinal);
        var children = routed
            .Where(c => ParentOf(c) is { } p && ids.Contains(p))
            .ToLookup(c => ParentOf(c)!, StringComparer.Ordinal);

        return routed
            .Where(c => ParentOf(c) is not { } p || !ids.Contains(p))
            .Select(parent => new RoutedUnit(parent, [.. children[parent.SessionId]]))
            .ToList();
    }

    public static bool IsCorrelatedChild(ImportCommand.SessionClassification c, ISet<string> planIds) =>
        ParentOf(c) is { } p && planIds.Contains(p);

    static string? ParentOf(ImportCommand.SessionClassification c) =>
        c.SourceMeta is { } meta
     && meta.TryGetValue("IsSubagentChild", out var isChild) && isChild is true
     && meta.TryGetValue("ParentSessionId", out var parent) ? parent as string : null;
}
```

`ForegroundSelection.cs`:

```csharp
namespace Capacitor.Cli.Commands;

internal sealed record ForegroundPlan(
    List<List<ImportCommand.SessionClassification>> Chains,
    List<ImportCommand.SessionClassification>       Routed,
    ImportRunSelection                              Selection);

/// <summary>Decided before any import work: whole chains newest first to the cap, then eligible
/// routed units; the boundary unit is taken whole.</summary>
internal static class ForegroundSelection {
    public static ForegroundPlan Select(
            List<List<ImportCommand.SessionClassification>> chains,
            List<ImportCommand.SessionClassification>       routed,
            IReadOnlyList<ImportCommand.SessionClassification> all,
            int                                              maxSessions) {
        var units   = RoutedUnits.Build(routed);
        var planIds = routed.Select(c => c.SessionId).ToHashSet(StringComparer.Ordinal);

        var selectedChains = new List<List<ImportCommand.SessionClassification>>();
        var count = 0;
        foreach (var chain in chains) {
            if (count >= maxSessions) break;
            selectedChains.Add(chain);
            count += chain.Count;
        }

        var selectedUnits = new List<RoutedUnit>();
        foreach (var unit in units.Where(u => u.Eligible)) {
            if (count >= maxSessions) break;
            selectedUnits.Add(unit);
            count++;
        }

        var selectedIds = selectedChains.SelectMany(c => c).Select(c => c.SessionId)
            .Concat(selectedUnits.Select(u => u.Parent.SessionId))
            .ToList();
        var selectedSet = selectedIds.ToHashSet(StringComparer.Ordinal);

        var candidates = all
            .Where(c => c.Status is ImportCommand.ClassificationStatus.New
                                 or ImportCommand.ClassificationStatus.Partial
                                 or ImportCommand.ClassificationStatus.ProbeError)
            .Where(c => !RoutedUnits.IsCorrelatedChild(c, planIds))
            .OrderBy(c => c, ImportOrdering.Candidate)
            .Select(c => c.SessionId)
            .ToList();

        var carried = selectedUnits.SelectMany(u => u.Children).Select(c => c.SessionId).ToHashSet(StringComparer.Ordinal);
        var remainder = all.Any(c =>
            (c.Status is ImportCommand.ClassificationStatus.New or ImportCommand.ClassificationStatus.Partial
                && !selectedSet.Contains(c.SessionId) && !carried.Contains(c.SessionId))
         || (c.Status is ImportCommand.ClassificationStatus.AlreadyLoaded && string.IsNullOrEmpty(c.FilePath)
                && !carried.Contains(c.SessionId))
         || c.Status is ImportCommand.ClassificationStatus.ProbeError);

        return new ForegroundPlan(
            selectedChains,
            [.. selectedUnits.SelectMany(u => u.Members)],
            new ImportRunSelection(candidates, selectedIds, remainder));
    }
}
```

Note the two `RemainderExists` clauses match the spec's Terms: an unselected `New`/`Partial` that is not a carried child; a routed replay row that is not carried; any `ProbeError`. A correlated `New` child of an `AlreadyLoaded` parent is unselected and uncarried, so it is remainder — as the spec requires.

- [ ] **Step 4: Run the tests to verify they pass**

Run both filters. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/RoutedUnit.cs src/Capacitor.Cli/Commands/ForegroundSelection.cs src/Capacitor.Cli/Commands/ImportRunSelection.cs src/Capacitor.Cli/Commands/ImportRunPartition.cs test/Capacitor.Cli.Tests.Unit/Commands/RoutedUnitTests.cs test/Capacitor.Cli.Tests.Unit/Commands/ForegroundSelectionTests.cs
git commit -m "[AI-2169] Select the foreground import as whole units newest first"
```

---

### Task 3: Wire `maxSessions`, `onSelected` and the partition into `HandleImport`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ImportCommand.cs` — `HandleImport` signature (~:738-763), the three `ReportNothing` exits (~:802, ~:858, ~:1083) and `ReportNothing` itself (~:658), the plan slot after `routed = ReconcileOrphanedCursorSubagentChildren(routed)` (~:1325), `ChainWorkerEvents` handlers `OnSessionEnded` / `OnSessionErrored` (~:1360-1375), the routed loop (~:1745-1765), `ImportRunOutcome` (~:664) and its final construction (~:2042)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/ImportSelectionReportingTests.cs`

**Interfaces:**
- Consumes: Task 2's `ForegroundSelection.Select`, `ImportRunSelection`, `ImportRunPartition`; `ImportSessionResult.SentChildContent`.
- Produces: `HandleImport(..., int? maxSessions = null, Action<ImportRunSelection>? onSelected = null, ...)`; `ImportRunOutcome` gains `ImportRunPartition? Partition` (null unless `maxSessions` was set). Both callbacks fire on the calling thread of `HandleImport`, never from a worker.

- [ ] **Step 1: Write the failing end-to-end test**

Pattern: `ImportSkipTitleTests` (WireMock stubs + a temp Claude projects dir). Create `ImportSelectionReportingTests.cs`:

```csharp
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

[ParallelLimiter<SubprocessLimit>]
public class ImportSelectionReportingTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();

    public void Dispose() { _server.Dispose(); _tmp.Dispose(); }

    /// Seven solo Claude sessions with ascending timestamps; the newest five are selected.
    TempDir Projects() {
        var projects = _tmp.CreateDir("projects");
        var dir = projects.CreateDir("-tmp-sel-proj");
        for (var i = 0; i < 7; i++)
            dir.CreateFile($"sess{i}.jsonl",
                [.. Enumerable.Range(0, 20).Select(n =>
                    $$$"""{"type":"user","timestamp":"2026-03-{{{(i + 1):00}}}T10:00:00Z","cwd":"/tmp/sel-proj","message":{"content":"line {{{n}}}"}}""")]);
        return projects;
    }

    void StubHooks() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var path in new[] { "/hooks/transcript", "/hooks/session-start*", "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-title" })
            _server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
    }

    ImportCommand Import() => new(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home,
        TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System);

    [Test]
    public async Task Capped_run_reports_selection_before_the_first_upload_and_a_partition_at_the_end() {
        StubHooks();
        var projects = Projects();
        ImportRunSelection? selected = null;
        var uploadsAtSelection = -1;
        ImportRunOutcome? finished = null;

        var exit = await Import().HandleImport(
            filterCwd: null, minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projects.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
            scope: new ImportScope.All(), skipConfirmation: true, skipTitle: true,
            maxSessions: 5,
            onSelected: s => { selected = s; uploadsAtSelection = _server.LogEntries.Count(e => e.RequestMessage.Path.StartsWith("/hooks/session-start")); },
            onFinished: o => finished = o);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(selected).IsNotNull();
        await Assert.That(uploadsAtSelection).IsEqualTo(0);
        await Assert.That(selected!.SelectedIds.Count).IsEqualTo(5);
        await Assert.That(selected.RunCandidateIds.Count).IsEqualTo(7);
        await Assert.That(selected.RemainderExists).IsTrue();
        await Assert.That(finished!.Partition).IsNotNull();
        await Assert.That(finished.Partition!.SucceededIds).IsEquivalentTo(selected.SelectedIds);
        await Assert.That(finished.Partition.FailedIds).IsEmpty();
        await Assert.That(_server.LogEntries.Count(e => e.RequestMessage.Path.StartsWith("/hooks/session-start"))).IsEqualTo(5);
    }

    [Test]
    public async Task Uncapped_run_reports_no_selection_and_no_partition() {
        StubHooks();
        var projects = Projects();
        var selectedFired = false;
        ImportRunOutcome? finished = null;

        await Import().HandleImport(
            filterCwd: null, minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projects.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
            scope: new ImportScope.All(), skipConfirmation: true, skipTitle: true,
            onSelected: _ => selectedFired = true,
            onFinished: o => finished = o);

        await Assert.That(selectedFired).IsFalse();
        await Assert.That(finished!.Partition).IsNull();
    }

    [Test]
    public async Task Capped_run_over_an_empty_corpus_reports_an_empty_selection_and_partition() {
        StubHooks();
        var empty = _tmp.CreateDir("empty");
        ImportRunSelection? selected = null;
        ImportRunOutcome? finished = null;

        await Import().HandleImport(
            filterCwd: null, minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, empty.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
            scope: new ImportScope.All(), skipConfirmation: true, skipTitle: true,
            maxSessions: 5, onSelected: s => selected = s, onFinished: o => finished = o);

        await Assert.That(selected).IsEqualTo(ImportRunSelection.Empty);
        await Assert.That(finished!.Partition).IsEqualTo(ImportRunPartition.Empty);
    }

    [Test]
    public async Task A_failed_upload_lands_in_FailedIds() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/hooks/session-start*").UsingPost()).RespondWith(Response.Create().WithStatusCode(500));
        var projects = Projects();
        ImportRunOutcome? finished = null;

        await Import().HandleImport(
            filterCwd: null, minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projects.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
            scope: new ImportScope.All(), skipConfirmation: true, skipTitle: true,
            maxSessions: 2, onFinished: o => finished = o);

        await Assert.That(finished!.Partition!.FailedIds.Count).IsEqualTo(2);
        await Assert.That(finished.Partition.SucceededIds).IsEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: filter `ImportSelectionReportingTests`. Expected: compile error (`maxSessions`, `onSelected`, `Partition` unknown).

- [ ] **Step 3: Implement**

In `HandleImport`'s parameter list add, after `onDiscovered`:

```csharp
            int?                          maxSessions             = null,
            Action<ImportRunSelection>?   onSelected              = null,
```

Change `ImportRunOutcome`:

```csharp
internal sealed record ImportRunOutcome(FinalCounts Counts, int VisibilityFailures, ImportRunPartition? Partition = null) {
    internal bool AnythingFailed => Counts.Failed > 0 || VisibilityFailures > 0;
}
```

Change `ReportNothing` to take and publish the selection when capped:

```csharp
static void ReportNothing(Action<ImportRunOutcome>? onFinished, Action<ImportRunSelection>? onSelected, bool capped) {
    if (capped) onSelected?.Invoke(ImportRunSelection.Empty);
    onFinished?.Invoke(new ImportRunOutcome(
        new FinalCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, RanBackground: false, RequestedSummaries: false),
        VisibilityFailures: 0,
        Partition: capped ? ImportRunPartition.Empty : null));
}
```

and update the three call sites to `ReportNothing(onFinished, onSelected, maxSessions is not null);`.

At the plan slot, replace

```csharp
        routed = ReconcileOrphanedCursorSubagentChildren(routed);

        var chains = BuildImportChains(fileBased);
```

with

```csharp
        routed.Sort(ImportOrdering.RoutedDispatch);
        var chains = BuildImportChains(fileBased);

        // The cap narrows the plan before the reconcile, so the reconcile sees exactly the units
        // that will run; a child never reaches it without its parent.
        HashSet<string>? selectedIds = null;
        if (maxSessions is { } cap) {
            var plan = ForegroundSelection.Select(chains, routed, classifications, cap);
            chains      = plan.Chains;
            routed      = plan.Routed;
            selectedIds = plan.Selection.SelectedIds.ToHashSet(StringComparer.Ordinal);
            onSelected?.Invoke(plan.Selection);
        }

        routed = ReconcileOrphanedCursorSubagentChildren(routed);
```

(Task 1's `routed.Sort` line moves here; keep exactly one sort.) Declare three `ConcurrentBag<string>` next to `importedSessionIds`: `partitionSucceeded`, `partitionSkipped`, `partitionFailed`. Record the chain path inside the existing handlers:

```csharp
            OnSessionErrored = (_, sid, reason) => {
                if (selectedIds?.Contains(sid) == true) partitionFailed.Add(sid);
                display.Line($"Skipping {sid} [{reason}]", FormatSkippedReasonMarkup(sid, reason));
            },
            ...
            OnSessionEnded = (_, c, outcome, lines) => {
                importedSessionIds.Add(c.SessionId);
                if (selectedIds?.Contains(c.SessionId) == true) partitionSucceeded.Add(c.SessionId);
                ...existing body...
            },
```

`OnSessionErrored` can fire more than once for one session (a resume tail failure then a fallback); dedupe when building the partition (`Distinct()`), and a session in both `partitionFailed` and `partitionSucceeded` counts as succeeded (the retry landed it).

Record the routed path right after `var outcome = result.Outcome;` in the routed loop:

```csharp
                if (selectedIds?.Contains(c.SessionId) == true) {
                    switch (outcome) {
                        case ImportOutcome.Loaded or ImportOutcome.Resumed:            partitionSucceeded.Add(c.SessionId); break;
                        case ImportOutcome.Skipped when result.SentChildContent:        partitionSucceeded.Add(c.SessionId); break;
                        case ImportOutcome.Skipped:                                     partitionSkipped.Add(c.SessionId);   break;
                        case ImportOutcome.Failed:                                      partitionFailed.Add(c.SessionId);    break;
                    }
                }
```

Build the partition at the end, before the final `onFinished`:

```csharp
        ImportRunPartition? partition = null;
        if (selectedIds is not null) {
            var succeeded = partitionSucceeded.Distinct(StringComparer.Ordinal).ToList();
            var succeededSet = succeeded.ToHashSet(StringComparer.Ordinal);
            partition = new ImportRunPartition(
                succeeded,
                partitionSkipped.Distinct(StringComparer.Ordinal).Where(id => !succeededSet.Contains(id)).ToList(),
                partitionFailed.Distinct(StringComparer.Ordinal).Where(id => !succeededSet.Contains(id)).ToList());
        }
        onFinished?.Invoke(new ImportRunOutcome(final, visibilityFailures, partition));
```

- [ ] **Step 4: Run to verify it passes**

Run: filter `ImportSelectionReportingTests`, then `ImportChainTests`, `ImportOrderingTests`, `ImportVisibilityTests`, `ImportSkipTitleTests` (no regressions in the existing HandleImport paths). Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/ImportCommand.cs test/Capacitor.Cli.Tests.Unit/Commands/ImportSelectionReportingTests.cs
git commit -m "[AI-2169] Report a capped import's selection and per-id partition"
```

---

### Task 4: The detached child contract in `kcap import`

**Files:**
- Create: `src/Capacitor.Cli/Commands/DetachedImportLog.cs`
- Modify: `src/Capacitor.Cli/Program.cs` — the `case "import":` block (~:608-712)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/DetachedImportLogTests.cs`

**Interfaces:**
- Consumes: `ProcessHelpers.DetachFromControllingTerminal()` (returns bool), `HandleImport(defaultVisibility:)`.
- Produces: `internal sealed record DetachedImportLog(string LogPath, string? DefaultVisibility)` with `static DetachedImportLog? FromEnvironment(Func<string, string?> getEnv)` (null when `KCAP_IMPORT_DETACHED_LOG` is unset or blank) and `StreamWriter Open()` (opens the existing file, `FileMode.Open` + seek to end, `FileAccess.Write`, `FileShare.ReadWrite`, `AutoFlush = true`; throws when the file is missing). Constants `EnvVar = "KCAP_IMPORT_DETACHED_LOG"`, `VisibilityEnvVar = "KCAP_IMPORT_DEFAULT_VISIBILITY"`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class DetachedImportLogTests {
    [Test]
    public async Task Absent_variable_means_no_detached_contract() {
        await Assert.That(DetachedImportLog.FromEnvironment(_ => null)).IsNull();
        await Assert.That(DetachedImportLog.FromEnvironment(v => v == DetachedImportLog.EnvVar ? "  " : null)).IsNull();
    }

    [Test]
    public async Task Visibility_rides_only_with_the_log_variable() {
        var env = new Dictionary<string, string?> {
            [DetachedImportLog.EnvVar]           = "/tmp/x.log",
            [DetachedImportLog.VisibilityEnvVar] = "private",
        };

        var contract = DetachedImportLog.FromEnvironment(k => env.GetValueOrDefault(k));

        await Assert.That(contract!.LogPath).IsEqualTo("/tmp/x.log");
        await Assert.That(contract.DefaultVisibility).IsEqualTo("private");
        await Assert.That(DetachedImportLog.FromEnvironment(k => k == DetachedImportLog.VisibilityEnvVar ? "private" : null)).IsNull();
    }

    [Test]
    public async Task Open_appends_to_the_existing_file_and_refuses_a_missing_one() {
        using var tmp = new TempDir();
        var path = tmp.CreateFile("import-run.log", "first\n");
        var contract = new DetachedImportLog(path, null);

        await using (var w = contract.Open()) await w.WriteLineAsync("second");

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("first\nsecond\n");
        await Assert.That(() => new DetachedImportLog(tmp.PathTo("absent.log"), null).Open()).Throws<FileNotFoundException>();
    }
}
```

- [ ] **Step 2: Run to verify it fails** — filter `DetachedImportLogTests`; expected compile error.

- [ ] **Step 3: Implement**

`DetachedImportLog.cs`:

```csharp
namespace Capacitor.Cli.Commands;

/// <summary>The detached child's contract with setup: where to write, and which visibility to stamp.
/// Read only when the log variable is present; a plain <c>kcap import</c> never sees either.</summary>
internal sealed record DetachedImportLog(string LogPath, string? DefaultVisibility) {
    public const string EnvVar           = "KCAP_IMPORT_DETACHED_LOG";
    public const string VisibilityEnvVar = "KCAP_IMPORT_DEFAULT_VISIBILITY";

    public static DetachedImportLog? FromEnvironment(Func<string, string?> getEnv) {
        var log = getEnv(EnvVar);
        if (string.IsNullOrWhiteSpace(log)) return null;

        var visibility = getEnv(VisibilityEnvVar);
        return new DetachedImportLog(log, string.IsNullOrWhiteSpace(visibility) ? null : visibility);
    }

    /// <summary>Setup created the file; the child only ever appends to it.</summary>
    public StreamWriter Open() {
        var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.Seek(0, SeekOrigin.End);
        return new StreamWriter(stream) { AutoFlush = true };
    }
}
```

In `Program.cs` `case "import":`, before `Run<ImportCommand>()`:

```csharp
        var detached = DetachedImportLog.FromEnvironment(Environment.GetEnvironmentVariable);
        StreamWriter? detachedLog = null;
        if (detached is not null) {
            detachedLog = detached.Open();
            Console.SetOut(detachedLog);
            Console.SetError(detachedLog);
            ProcessHelpers.DetachFromControllingTerminal();
        }
```

and pass `defaultVisibility: detached?.DefaultVisibility` to `HandleImport`. Wrap the `HandleImport` call so `detachedLog` is disposed after it returns (`try { … } finally { detachedLog?.Dispose(); }`). Redirecting `Console.Out` makes `AnsiConsole` treat output as non-interactive (Spectre probes the console on first use, which is after this point in the import command); confirm by checking `display.Tty` is false in a detached run — the display constructor reads `Console.IsOutputRedirected`, which is true once `SetOut` has run.

- [ ] **Step 4: Run to verify it passes** — filter `DetachedImportLogTests`. Then a manual smoke: `KCAP_IMPORT_DETACHED_LOG=$(mktemp) dotnet run --project src/Capacitor.Cli -- import --discover` writes the discovery report into the file and nothing to the terminal.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/DetachedImportLog.cs src/Capacitor.Cli/Program.cs test/Capacitor.Cli.Tests.Unit/Commands/DetachedImportLogTests.cs
git commit -m "[AI-2169] Let a detached kcap import log to a file setup created"
```

---

### Task 5: The runner's totalized contract

**Files:**
- Create: `src/Capacitor.Cli/Commands/SetupImportRun.cs`, `src/Capacitor.Cli/Commands/SetupImportDiscovery.cs`
- Modify: `src/Capacitor.Cli/Commands/ImportInvocation.cs`, `src/Capacitor.Cli/Commands/ISetupImportRunner.cs`, `src/Capacitor.Cli/Commands/SetupImportRunner.cs`, `test/Capacitor.Cli.Tests.Unit/Commands/FakeImportRunner.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/SetupImportRunnerTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record ImportInvocation(
    ImportScope                  Scope,
    int?                         MaxSessions,
    (string Owner, string Name)? CurrentRepo,
    string?                      DefaultVisibility,
    bool                         AutoSkipExclusions,
    bool                         ForcePrivate,
    bool                         SkipTitle,
    ProfileContext               Profiles);

internal sealed record SetupImportRun(int ExitCode, ImportRunSelection? Selection, ImportRunOutcome? Outcome, Exception? Fault);
internal sealed record SetupImportDiscovery(ImportCommand.ImportDiscoveryResult? Result, Exception? Fault);

public interface ISetupImportRunner {
    Task<SetupImportDiscovery> DiscoverAsync(ProfileContext profiles);
    Task<SetupImportRun>       RunAsync(ImportInvocation invocation);
}
```

`ImportScope` is `public`, `ImportRunSelection`/`ImportRunOutcome`/`ImportDiscoveryResult` are `internal`, so `ISetupImportRunner` becomes `internal interface` (it is consumed only inside the assembly and by the test project through `InternalsVisibleTo`, which already exists for these tests). Neither method throws.

- [ ] **Step 1: Write the failing tests**

Replace `FakeImportRunner.cs`:

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

sealed class FakeImportRunner : ISetupImportRunner {
    readonly Func<ImportInvocation, SetupImportRun> _run;
    Func<ProfileContext, SetupImportDiscovery>      _discover = _ => new(null, null);

    FakeImportRunner(Func<ImportInvocation, SetupImportRun> run) => _run = run;

    public static FakeImportRunner Succeeding(int selected = 3, bool remainder = true) => new(_ => {
        var ids = Enumerable.Range(0, selected).Select(i => $"s{i}").ToList();
        return new SetupImportRun(0,
            new ImportRunSelection([.. ids, "rest"], ids, remainder),
            new ImportRunOutcome(ZeroCounts, 0, new ImportRunPartition(ids, [], [])),
            null);
    });
    public static FakeImportRunner Returning(int exitCode)  => new(_ => new SetupImportRun(exitCode, ImportRunSelection.Empty, new ImportRunOutcome(ZeroCounts, 0, ImportRunPartition.Empty), null));
    public static FakeImportRunner Faulting(Exception boom) => new(_ => new SetupImportRun(1, null, null, boom));
    public static FakeImportRunner Of(Func<ImportInvocation, SetupImportRun> run) => new(run);

    public FakeImportRunner Discovering(ImportCommand.ImportDiscoveryResult result) { _discover = _ => new(result, null); return this; }
    public FakeImportRunner DiscoveryFaulting(Exception boom)                      { _discover = _ => new(null, boom);   return this; }

    public ImportInvocation? Captured { get; private set; }
    public int Calls { get; private set; }
    public int DiscoverCalls { get; private set; }

    public Task<SetupImportDiscovery> DiscoverAsync(ProfileContext profiles) { DiscoverCalls++; return Task.FromResult(_discover(profiles)); }
    public Task<SetupImportRun> RunAsync(ImportInvocation invocation) { Captured = invocation; Calls++; return Task.FromResult(_run(invocation)); }

    internal static readonly ImportCommand.FinalCounts ZeroCounts = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, RanBackground: false, RequestedSummaries: false);
}
```

`SetupImportRunnerTests.cs` (uses a WireMock server that refuses everything, so the run faults inside `HandleImport`):

```csharp
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SetupImportRunnerTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    SetupImportRunner Runner(ProfileContext profiles) => new(
        Config.Root, Home, TestHarnesses.Under(Home),
        new ChosenServerHttp(Config.Root, profiles, ProfileOverrides.None, MachineAuth.None),
        new GitProviderRouter(), TimeProvider.System);

    [Test]
    public async Task Discovery_over_an_empty_home_reports_a_result_and_no_fault() {
        var profiles = Resolutions.At("http://127.0.0.1:1", Config.Root);

        var discovery = await Runner(profiles).DiscoverAsync(profiles);

        await Assert.That(discovery.Fault).IsNull();
        await Assert.That(discovery.Result).IsNotNull();
        await Assert.That(discovery.Result!.Summary.Repos).IsEmpty();
    }

    [Test]
    public async Task A_run_over_an_empty_home_is_complete_with_an_empty_selection() {
        var profiles = Resolutions.At("http://127.0.0.1:1", Config.Root);

        var run = await Runner(profiles).RunAsync(new ImportInvocation(
            new ImportScope.All(), MaxSessions: 5, CurrentRepo: null, DefaultVisibility: "private",
            AutoSkipExclusions: true, ForcePrivate: false, SkipTitle: false, Profiles: profiles));

        await Assert.That(run.Fault).IsNull();
        await Assert.That(run.Selection).IsEqualTo(ImportRunSelection.Empty);
        await Assert.That(run.Outcome!.Partition).IsEqualTo(ImportRunPartition.Empty);
    }
}
```

Add one fault test using a `ClaudeImportSource` pointed at a projects dir whose transcript file is made unreadable (`File.SetUnixFileMode(path, UnixFileMode.None)` on Unix) so discovery or classification throws; assert `Fault` is non-null and nothing propagates. Skip it on Windows with `[Test, Skip("Unix modes")]` semantics as the repo does elsewhere (`if (OperatingSystem.IsWindows()) return;`).

- [ ] **Step 2: Run to verify it fails** — filters `SetupImportRunnerTests`, `SetupCommandTests` (compile errors from the new contract).

- [ ] **Step 3: Implement**

`SetupImportRun.cs`, `SetupImportDiscovery.cs` as in Interfaces. `ImportInvocation.cs` as in Interfaces. `ISetupImportRunner.cs`:

```csharp
namespace Capacitor.Cli.Commands;

/// <summary>Runs the Import step's embedded <c>kcap import</c>. Neither call throws: setup reads the
/// fault out of the result, so the step can never abort the wizard.</summary>
internal interface ISetupImportRunner {
    Task<SetupImportDiscovery> DiscoverAsync(ProfileContext profiles);
    Task<SetupImportRun>       RunAsync(ImportInvocation invocation);
}
```

`SetupImportRunner.cs`:

```csharp
sealed class SetupImportRunner(
        ConfigRoot config, UserHome home, HarnessRegistry harnesses, ChosenServerHttp http,
        GitProviderRouter router, TimeProvider time) : ISetupImportRunner {

    public async Task<SetupImportDiscovery> DiscoverAsync(ProfileContext profiles) {
        ImportCommand.ImportDiscoveryResult? found = null;
        try {
            await using var scoped = http.For(profiles.Resolution.ServerUrl ?? "", profiles);
            await Command(profiles, scoped).HandleImport(
                filterCwd:    null,
                sources:      SetupCommand.BuildImportSources(config, harnesses, router, time),
                discoverOnly: true,
                onDiscovered: r => found = r,
                nested:       true);
            return new SetupImportDiscovery(found, null);
        } catch (Exception ex) {
            return new SetupImportDiscovery(null, ex);
        }
    }

    public async Task<SetupImportRun> RunAsync(ImportInvocation inv) {
        ImportRunSelection? selection = null;
        ImportRunOutcome?   outcome   = null;
        try {
            await using var scoped = http.For(inv.Profiles.Resolution.ServerUrl ?? "", inv.Profiles);
            var exit = await Command(inv.Profiles, scoped).HandleImport(
                filterCwd:               null,
                filterSession:           null,
                minLines:                15,
                generateSummaries:       false,
                sources:                 SetupCommand.BuildImportSources(config, harnesses, router, time),
                explicitVendorSelection: false,
                since:                   null,
                scope:                   inv.Scope,
                skipConfirmation:        true,
                forcePrivate:            inv.ForcePrivate,
                currentRepo:             inv.CurrentRepo,
                needOrgPick:             false,
                storedOrg:               null,
                autoSkipExclusions:      inv.AutoSkipExclusions,
                defaultVisibility:       inv.DefaultVisibility,
                skipTitle:               inv.SkipTitle,
                maxSessions:             inv.MaxSessions,
                onSelected:              s => selection = s,
                onFinished:              o => outcome = o,
                nested:                  true);
            return new SetupImportRun(exit, selection, outcome, null);
        } catch (Exception ex) {
            return new SetupImportRun(1, selection, outcome, ex);
        }
    }

    ImportCommand Command(ProfileContext profiles, ServiceProvider scoped) =>
        new(config, profiles, home, harnesses, scoped.GetRequiredService<ICapacitorHttpClient>(), router, time);
}
```

(The constructor loses `ProfileContext profiles` — each call carries its own; update `CommandServices.cs` registration if DI resolved it by constructor, and `SetupImportLane`'s `DiscoverAsync` can stay as is.) Fix `SetupCommandTests` compile errors minimally here — the step-6 tests are rewritten in Task 11; for now adjust `RunImportStepAsync_*` tests to the new `ImportInvocation` fields (`Scope`, `MaxSessions`, `SkipTitle`) so the project compiles, or mark them `[Skip("rewritten in the step-6 task")]`.

- [ ] **Step 4: Run to verify it passes** — filters `SetupImportRunnerTests`, `SetupChosenServerTests`, `SetupImportLaneTests`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/SetupImportRun.cs src/Capacitor.Cli/Commands/SetupImportDiscovery.cs src/Capacitor.Cli/Commands/ImportInvocation.cs src/Capacitor.Cli/Commands/ISetupImportRunner.cs src/Capacitor.Cli/Commands/SetupImportRunner.cs src/Capacitor.Cli/Commands/CommandServices.cs test/Capacitor.Cli.Tests.Unit/Commands/FakeImportRunner.cs test/Capacitor.Cli.Tests.Unit/Commands/SetupImportRunnerTests.cs test/Capacitor.Cli.Tests.Unit/Commands/SetupCommandTests.cs
git commit -m "[AI-2169] Return the import's selection, outcome and fault from the setup runner"
```

---

### Task 6: `ForegroundImportOutcome`

**Files:**
- Create: `src/Capacitor.Cli/Commands/ForegroundImportOutcome.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/ForegroundImportOutcomeTests.cs`

**Interfaces:**
- Produces:

```csharp
internal enum ForegroundCertainty { Complete, Incomplete }

internal sealed record ForegroundImportOutcome(
    ForegroundCertainty    Certainty,
    int                    Selected,
    int                    Succeeded,
    int                    Skipped,
    int                    Failed,
    bool                   RemainderExists,
    IReadOnlyList<string>? RunCandidateIds,
    IReadOnlyList<string>  SucceededIds) {
    public static ForegroundImportOutcome From(SetupImportRun run);
}
```

Rules (spec §2): `Complete` iff `Fault is null && Outcome is not null && Selection is not null`; `Selected = Selection?.SelectedIds.Count ?? 0`; counts from `Outcome.Partition` only when `Complete`, else zero; `RemainderExists = Selection?.RemainderExists ?? true`; `RunCandidateIds = Selection?.RunCandidateIds`; `SucceededIds = Complete ? Partition.SucceededIds : []`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ForegroundImportOutcomeTests {
    static readonly ImportRunSelection Sel = new(["a", "b", "c", "rest"], ["a", "b", "c"], RemainderExists: true);
    static ImportRunOutcome Outcome(ImportRunPartition? p) => new(FakeImportRunner.ZeroCounts, 0, p);

    [Test]
    public async Task Fault_before_selection_is_incomplete_with_no_candidates_and_a_remainder() {
        var o = ForegroundImportOutcome.From(new SetupImportRun(1, null, null, new InvalidOperationException("boom")));

        await Assert.That(o.Certainty).IsEqualTo(ForegroundCertainty.Incomplete);
        await Assert.That(o.RunCandidateIds).IsNull();
        await Assert.That(o.RemainderExists).IsTrue();
        await Assert.That(o.Selected + o.Succeeded + o.Skipped + o.Failed).IsEqualTo(0);
    }

    [Test]
    public async Task Fault_after_selection_keeps_the_candidates_and_zero_counts() {
        var o = ForegroundImportOutcome.From(new SetupImportRun(1, Sel, null, new InvalidOperationException("boom")));

        await Assert.That(o.Certainty).IsEqualTo(ForegroundCertainty.Incomplete);
        await Assert.That(o.RunCandidateIds).IsEquivalentTo(Sel.RunCandidateIds);
        await Assert.That(o.Selected).IsEqualTo(3);
        await Assert.That(o.Succeeded).IsEqualTo(0);
        await Assert.That(o.SucceededIds).IsEmpty();
    }

    [Test]
    public async Task Completed_pass_partitions_every_selected_id() {
        var o = ForegroundImportOutcome.From(new SetupImportRun(0, Sel, Outcome(new(["a"], ["b"], ["c"])), null));

        await Assert.That(o.Certainty).IsEqualTo(ForegroundCertainty.Complete);
        await Assert.That(o.Selected).IsEqualTo(o.Succeeded + o.Skipped + o.Failed);
        await Assert.That(o.SucceededIds).IsEquivalentTo(["a"]);
    }

    [Test]
    public async Task Outcome_without_selection_is_incomplete() {
        var o = ForegroundImportOutcome.From(new SetupImportRun(0, null, Outcome(null), null));

        await Assert.That(o.Certainty).IsEqualTo(ForegroundCertainty.Incomplete);
    }

    [Test]
    public async Task Empty_selection_and_partition_are_complete_with_nothing_to_do() {
        var o = ForegroundImportOutcome.From(new SetupImportRun(0, ImportRunSelection.Empty, Outcome(ImportRunPartition.Empty), null));

        await Assert.That(o.Certainty).IsEqualTo(ForegroundCertainty.Complete);
        await Assert.That(o.RunCandidateIds).IsEmpty();
        await Assert.That(o.RemainderExists).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails** — filter `ForegroundImportOutcomeTests`.

- [ ] **Step 3: Implement**

```csharp
namespace Capacitor.Cli.Commands;

internal enum ForegroundCertainty { Complete, Incomplete }

/// <summary>What setup knows after the foreground pass. Incomplete carries no counts: a pass that
/// did not finish reported no partition, and everything selected is treated as not landed.</summary>
internal sealed record ForegroundImportOutcome(
        ForegroundCertainty    Certainty,
        int                    Selected,
        int                    Succeeded,
        int                    Skipped,
        int                    Failed,
        bool                   RemainderExists,
        IReadOnlyList<string>? RunCandidateIds,
        IReadOnlyList<string>  SucceededIds) {

    public static ForegroundImportOutcome From(SetupImportRun run) {
        var complete  = run.Fault is null && run.Outcome is not null && run.Selection is not null;
        var partition = complete ? run.Outcome!.Partition ?? ImportRunPartition.Empty : ImportRunPartition.Empty;

        return new ForegroundImportOutcome(
            complete ? ForegroundCertainty.Complete : ForegroundCertainty.Incomplete,
            run.Selection?.SelectedIds.Count ?? 0,
            partition.SucceededIds.Count,
            partition.SkippedIds.Count,
            partition.FailedIds.Count,
            run.Selection?.RemainderExists ?? true,
            run.Selection?.RunCandidateIds,
            partition.SucceededIds);
    }
}
```

- [ ] **Step 4: Run to verify it passes** — filter `ForegroundImportOutcomeTests`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/ForegroundImportOutcome.cs test/Capacitor.Cli.Tests.Unit/Commands/ForegroundImportOutcomeTests.cs
git commit -m "[AI-2169] Totalize the foreground import into one outcome"
```

---

### Task 7: Owner-only file creation and the background spawner

**Files:**
- Create: `src/Capacitor.Cli.Core/OwnerOnlyFile.cs`, `src/Capacitor.Cli/Commands/BackgroundImportStatus.cs`, `src/Capacitor.Cli/Commands/BackgroundImportLaunch.cs`, `src/Capacitor.Cli/Commands/IBackgroundImportSpawner.cs`, `src/Capacitor.Cli/Commands/BackgroundImportSpawner.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/OwnerOnlyFileTests.cs`, `test/Capacitor.Cli.Tests.Unit/Commands/BackgroundImportSpawnerTests.cs`, `test/Capacitor.Cli.Tests.Unit/Commands/FakeBackgroundImportSpawner.cs`

**Interfaces:**
- Consumes: `IProcessStarter` (`Process? Start(ProcessStartInfo)`, `src/Capacitor.Cli/IProcessStarter.cs`) and its test double `FakeProcessStarter`; `ProcessHelpers.PreventInheritedHandles()`; `ConfigRoot.Directory`, `ConfigRoot.ConfigDirEnvVar`; `ProfileOverrides.ProfileVar`, `ProfileOverrides.UrlVar`; `DetachedImportLog.EnvVar`, `DetachedImportLog.VisibilityEnvVar`; `TimeProvider`.
- Produces:

```csharp
// Capacitor.Cli.Core
public static class OwnerOnlyFile {
    /// Creates a new file that must not exist yet, owner-only on Unix, and returns the open stream.
    public static FileStream CreateNew(string path);
}

// Capacitor.Cli.Commands
internal enum BackgroundImportStatus { NotNeeded, Running, ExitedZero, Failed }

internal sealed record BackgroundImportLaunch(BackgroundImportStatus Status, string? LogPath, int? ExitCode, string? Error) {
    public static BackgroundImportLaunch NotNeeded { get; } = new(BackgroundImportStatus.NotNeeded, null, null, null);
}

internal sealed record BackgroundImportRequest(string RunId, string ProfileName, string DefaultVisibility, string WorkingDirectory);

internal interface IBackgroundImportSpawner {
    BackgroundImportLaunch Spawn(BackgroundImportRequest request);
}
```

`BackgroundImportSpawner(ConfigRoot config, IProcessStarter starter, TimeProvider time)` implements `Spawn`: creates `<config dir>/import-{RunId}.log` via `OwnerOnlyFile.CreateNew` (failure → `Failed` with the message), builds the `ProcessStartInfo` (below), calls `ProcessHelpers.PreventInheritedHandles()`, starts, closes the three redirected streams, waits `1500 ms` (`WaitForExit(1500)`), maps to the status.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Cli.Core.Tests.Unit/OwnerOnlyFileTests.cs`:

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Core.Tests.Unit;

public class OwnerOnlyFileTests {
    [Test]
    public async Task Creates_the_file_owner_only_and_refuses_an_existing_path() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("new.log");

        await using (var s = OwnerOnlyFile.CreateNew(path)) await s.WriteAsync("x"u8.ToArray());

        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(() => OwnerOnlyFile.CreateNew(path)).Throws<IOException>();
    }

    [Test]
    public async Task Does_not_write_through_a_symlink_already_at_the_path() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var target = tmp.CreateFile("target.txt", "untouched");
        var link   = tmp.PathTo("link.log");
        File.CreateSymbolicLink(link, target);

        await Assert.That(() => OwnerOnlyFile.CreateNew(link)).Throws<IOException>();
        await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo("untouched");
    }
}
```

`FakeBackgroundImportSpawner.cs`:

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

sealed class FakeBackgroundImportSpawner(BackgroundImportLaunch result) : IBackgroundImportSpawner {
    public BackgroundImportRequest? Seen { get; private set; }
    public int Spawns { get; private set; }

    public static FakeBackgroundImportSpawner Running()   => new(new(BackgroundImportStatus.Running,    "/tmp/import-x.log", null, null));
    public static FakeBackgroundImportSpawner ExitedZero() => new(new(BackgroundImportStatus.ExitedZero, "/tmp/import-x.log", 0, null));
    public static FakeBackgroundImportSpawner Failing()   => new(new(BackgroundImportStatus.Failed,     "/tmp/import-x.log", 3, "exit 3"));

    public BackgroundImportLaunch Spawn(BackgroundImportRequest request) { Seen = request; Spawns++; return result; }
}
```

`BackgroundImportSpawnerTests.cs`:

```csharp
using System.Diagnostics;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class BackgroundImportSpawnerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static BackgroundImportRequest Request(string profile = "work") => new("0123456789abcdef0123456789abcdef", profile, "private", "/tmp/wd");

    /// A parent environment carrying KCAP_URL is what makes the removal observable.
    static IDisposable PollutedParent() => EnvScope.Exclusive(ProfileOverrides.UrlVar, "https://ambient.test");

    [Test]
    public async Task Argv_and_environment_pin_the_saved_profile_and_drop_the_url_override() {
        using var _ = PollutedParent();
        var starter = FakeProcessStarter.Running(psi => Process.Start(new ProcessStartInfo("sleep", "5") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }));

        var launch = new BackgroundImportSpawner(Config.Root, starter, TimeProvider.System).Spawn(Request());

        var psi = starter.Seen!;
        await Assert.That(psi.ArgumentList.ToList()).IsEquivalentTo(["import", "--all", "--yes", "--skip-title"]);
        await Assert.That(psi.Environment[ConfigRoot.ConfigDirEnvVar]).IsEqualTo(Config.Directory);
        await Assert.That(psi.Environment[ProfileOverrides.ProfileVar]).IsEqualTo("work");
        await Assert.That(psi.Environment.ContainsKey(ProfileOverrides.UrlVar)).IsFalse();
        await Assert.That(psi.Environment[DetachedImportLog.EnvVar]).IsEqualTo(Config.Root.Path("import-0123456789abcdef0123456789abcdef.log"));
        await Assert.That(psi.Environment[DetachedImportLog.VisibilityEnvVar]).IsEqualTo("private");
        await Assert.That(psi.WorkingDirectory).IsEqualTo("/tmp/wd");
        await Assert.That(psi.UseShellExecute).IsFalse();
        await Assert.That(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError).IsTrue();
        await Assert.That(launch.Status).IsEqualTo(BackgroundImportStatus.Running);
        await Assert.That(File.Exists(launch.LogPath!)).IsTrue();
    }

    [Test]
    public async Task Early_zero_exit_is_ExitedZero_and_early_nonzero_is_Failed() {
        var zero = FakeProcessStarter.Running(_ => Process.Start(new ProcessStartInfo("true") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }));
        var fail = FakeProcessStarter.Running(_ => Process.Start(new ProcessStartInfo("false") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }));

        var a = new BackgroundImportSpawner(Config.Root, zero, TimeProvider.System).Spawn(Request());
        var b = new BackgroundImportSpawner(Config.Root, fail, TimeProvider.System).Spawn(Request("other"));

        await Assert.That(a.Status).IsEqualTo(BackgroundImportStatus.ExitedZero);
        await Assert.That(b.Status).IsEqualTo(BackgroundImportStatus.Failed);
        await Assert.That(b.ExitCode).IsEqualTo(1);
    }

    [Test]
    public async Task A_refused_start_or_a_throw_is_Failed() {
        var refused = new BackgroundImportSpawner(Config.Root, FakeProcessStarter.Refusing(), TimeProvider.System).Spawn(Request());
        var thrown  = new BackgroundImportSpawner(Config.Root, FakeProcessStarter.Throwing(new InvalidOperationException("no exec")), TimeProvider.System).Spawn(Request());

        await Assert.That(refused.Status).IsEqualTo(BackgroundImportStatus.Failed);
        await Assert.That(thrown.Error).Contains("no exec");
    }

    [Test]
    public async Task A_pre_existing_path_at_the_log_name_fails_the_spawn_and_is_left_untouched() {
        var path = Config.Root.Path("import-0123456789abcdef0123456789abcdef.log");
        await File.WriteAllTextAsync(path, "someone else's");
        var starter = FakeProcessStarter.Refusing();

        var launch = new BackgroundImportSpawner(Config.Root, starter, TimeProvider.System).Spawn(Request());

        await Assert.That(launch.Status).IsEqualTo(BackgroundImportStatus.Failed);
        await Assert.That(starter.Starts).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("someone else's");
    }
}
```

(The `sleep`/`true`/`false` processes are Unix; guard those two tests with `if (OperatingSystem.IsWindows()) return;` as the repo does.)

- [ ] **Step 2: Run to verify they fail** — filters `OwnerOnlyFileTests` (Core project) and `BackgroundImportSpawnerTests`.

- [ ] **Step 3: Implement**

`OwnerOnlyFile.cs`:

```csharp
namespace Capacitor.Cli.Core;

/// <summary>A file that must not exist yet, readable by its owner only. CreateNew refuses a path
/// that already exists — including a symlink — so nothing is ever written through one.</summary>
public static class OwnerOnlyFile {
    public static FileStream CreateNew(string path) {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        return new FileStream(path, options);
    }
}
```

`BackgroundImportSpawner.cs`:

```csharp
using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>Spawns the detached remainder import the way <c>DaemonCommands.StartDetached</c> spawns
/// the daemon: streams redirected and closed by the parent, handles not inherited, a short readiness
/// wait deciding between running and dead on arrival.</summary>
sealed class BackgroundImportSpawner(ConfigRoot config, IProcessStarter starter, TimeProvider time) : IBackgroundImportSpawner {
    static readonly TimeSpan Readiness = TimeSpan.FromMilliseconds(1500);

    public BackgroundImportLaunch Spawn(BackgroundImportRequest request) {
        var logPath = config.Path($"import-{request.RunId}.log");
        try {
            using (OwnerOnlyFile.CreateNew(logPath)) { }
        } catch (IOException ex) {
            return new BackgroundImportLaunch(BackgroundImportStatus.Failed, logPath, null, $"could not create {logPath}: {ex.Message}");
        }

        var psi = new ProcessStartInfo {
            FileName               = Environment.ProcessPath ?? "kcap",
            WorkingDirectory       = request.WorkingDirectory,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var arg in new[] { "import", "--all", "--yes", "--skip-title" }) psi.ArgumentList.Add(arg);

        // A URL override outranks the profile pin and resolves to no profile, so it must not travel:
        // the child's capture lists and visibility come from the profile setup just saved.
        psi.Environment[ConfigRoot.ConfigDirEnvVar]        = config.Directory;
        psi.Environment[ProfileOverrides.ProfileVar]       = request.ProfileName;
        psi.Environment.Remove(ProfileOverrides.UrlVar);
        psi.Environment[DetachedImportLog.EnvVar]           = logPath;
        psi.Environment[DetachedImportLog.VisibilityEnvVar] = request.DefaultVisibility;

        Process? process;
        try {
            ProcessHelpers.PreventInheritedHandles();
            process = starter.Start(psi);
        } catch (Exception ex) {
            return new BackgroundImportLaunch(BackgroundImportStatus.Failed, logPath, null, ex.Message);
        }
        if (process is null) return new BackgroundImportLaunch(BackgroundImportStatus.Failed, logPath, null, "the process did not start");

        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();

        if (!process.WaitForExit((int)Readiness.TotalMilliseconds))
            return new BackgroundImportLaunch(BackgroundImportStatus.Running, logPath, null, null);

        return process.ExitCode == 0
            ? new BackgroundImportLaunch(BackgroundImportStatus.ExitedZero, logPath, 0, null)
            : new BackgroundImportLaunch(BackgroundImportStatus.Failed, logPath, process.ExitCode, $"exit {process.ExitCode}");
    }
}
```

`time` is kept on the constructor for the DI shape shared with the other collaborators; it is unused here and may be dropped if the analyzer objects. Register in `CommandServices.cs` next to `ISetupImportRunner`: `services.AddSingleton<IBackgroundImportSpawner, BackgroundImportSpawner>();` (an `IProcessStarter` registration already exists for the watcher spawner; reuse it).

- [ ] **Step 4: Run to verify they pass** — both filters.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/OwnerOnlyFile.cs src/Capacitor.Cli/Commands/BackgroundImportStatus.cs src/Capacitor.Cli/Commands/BackgroundImportLaunch.cs src/Capacitor.Cli/Commands/IBackgroundImportSpawner.cs src/Capacitor.Cli/Commands/BackgroundImportSpawner.cs src/Capacitor.Cli/Commands/CommandServices.cs test/Capacitor.Cli.Core.Tests.Unit/OwnerOnlyFileTests.cs test/Capacitor.Cli.Tests.Unit/Commands/BackgroundImportSpawnerTests.cs test/Capacitor.Cli.Tests.Unit/Commands/FakeBackgroundImportSpawner.cs
git commit -m "[AI-2169] Spawn the remainder import detached with the saved profile pinned"
```

---

### Task 8: The handoff file

**Files:**
- Create: `src/Capacitor.Cli/Commands/HandoffSuppressedReason.cs`, `src/Capacitor.Cli/Commands/HandoffCohort.cs`, `src/Capacitor.Cli/Commands/ImportHandoffFile.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/ImportHandoffFileTests.cs`

**Interfaces:**
- Consumes: `OwnerOnlyFile.CreateNew`, `ConfigRoot.Path`, `ForegroundImportOutcome`, `BackgroundImportLaunch`, `TimeProvider`.
- Produces:

```csharp
internal enum HandoffSuppressedReason { ImportFailed, NoNewSessions, NothingLanded, AnalyticsNotInPlan, SkillNotInstalled, NoAgentDetected }
// wire names via HandoffSuppressedReasonExtensions.Wire(): import_failed, no_new_sessions, nothing_landed, analytics_not_in_plan, skill_not_installed, no_agent_detected

internal enum HandoffCohort { Exact, PartialExact, Unknown }

internal sealed record ImportHandoffFile(
    string RunId, DateTimeOffset WrittenAt, bool HandoffOffered, HandoffSuppressedReason? HandoffSuppressed,
    ForegroundCertainty Certainty, string ServerUrl, string Profile, HandoffCohort Cohort,
    IReadOnlyList<string> SessionIds, IReadOnlyList<string> ForegroundSucceededIds, int UnattributedOnDisk,
    BackgroundImportStatus Background, string? BackgroundLog) {
    public const int    SchemaVersion = 1;
    public const int    CohortCap     = 500;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static ImportHandoffFile Compose(string runId, DateTimeOffset now, bool offered, HandoffSuppressedReason? reason,
        ForegroundImportOutcome outcome, BackgroundImportLaunch background, string serverUrl, string profile, int unattributedOnDisk);
    public string ToJson();                                    // JsonObject + (JsonNode?) casts, indented
    public static string PathFor(ConfigRoot config, string runId);   // <config>/import-handoff-{runId}.json
    /// Temp CreateNew → write → File.Move without overwrite; prunes files older than Retention; throws IOException on any failure.
    public void Write(ConfigRoot config, TimeProvider time);
}
```

`Compose` rules: `Cohort = RunCandidateIds is null ? Unknown : (Count > 500 ? PartialExact : Exact)`; `SessionIds = candidates.Take(500)` (candidate order is already newest first); `ForegroundSucceededIds = outcome.SucceededIds`; `ServerUrl` trailing slash trimmed.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportHandoffFileTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    const string RunId = "0123456789abcdef0123456789abcdef";

    static ForegroundImportOutcome Outcome(int candidates = 7, params string[] succeeded) => new(
        ForegroundCertainty.Complete, 5, succeeded.Length, 0, 0, RemainderExists: true,
        Enumerable.Range(0, candidates).Select(i => $"c{i:000}").ToList(), succeeded);

    static ImportHandoffFile Compose(ForegroundImportOutcome o, bool offered = true, HandoffSuppressedReason? reason = null) =>
        ImportHandoffFile.Compose(RunId, Now, offered, reason, o, new(BackgroundImportStatus.Running, "/tmp/import-x.log", null, null),
            "https://acme.kcap.ai/", "work", unattributedOnDisk: 1552);

    [Test]
    public async Task Json_has_the_pinned_shape() {
        var json = JsonNode.Parse(Compose(Outcome(3, "c000")).ToJson())!.AsObject();

        await Assert.That(json["schema_version"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(json["run_id"]!.GetValue<string>()).IsEqualTo(RunId);
        await Assert.That(json["written_at"]!.GetValue<string>()).IsEqualTo("2026-09-17T12:00:00+00:00");
        await Assert.That(json["handoff_offered"]!.GetValue<bool>()).IsTrue();
        await Assert.That(json["handoff_suppressed"]).IsNull();
        await Assert.That(json["foreground_certainty"]!.GetValue<string>()).IsEqualTo("complete");
        await Assert.That(json["server_url"]!.GetValue<string>()).IsEqualTo("https://acme.kcap.ai");
        await Assert.That(json["profile"]!.GetValue<string>()).IsEqualTo("work");
        await Assert.That(json["scope"]!.GetValue<string>()).IsEqualTo("all");
        await Assert.That(json["cohort"]!.GetValue<string>()).IsEqualTo("exact");
        await Assert.That(json["session_ids"]!.AsArray().Count).IsEqualTo(3);
        await Assert.That(json["foreground_succeeded_ids"]!.AsArray()[0]!.GetValue<string>()).IsEqualTo("c000");
        await Assert.That(json["unattributed_on_disk"]!.GetValue<int>()).IsEqualTo(1552);
        await Assert.That(json["background"]!.GetValue<string>()).IsEqualTo("running");
        await Assert.That(json["background_log"]!.GetValue<string>()).IsEqualTo("/tmp/import-x.log");
    }

    [Test]
    public async Task More_than_500_candidates_keeps_the_first_500_as_partial_exact() {
        var file = Compose(Outcome(candidates: 600));

        await Assert.That(file.Cohort).IsEqualTo(HandoffCohort.PartialExact);
        await Assert.That(file.SessionIds.Count).IsEqualTo(500);
        await Assert.That(file.SessionIds[0]).IsEqualTo("c000");
    }

    [Test]
    public async Task Unknown_candidates_write_an_unknown_cohort_with_no_ids() {
        var o = new ForegroundImportOutcome(ForegroundCertainty.Incomplete, 0, 0, 0, 0, true, null, []);

        var file = Compose(o, offered: false, reason: HandoffSuppressedReason.ImportFailed);
        var json = JsonNode.Parse(file.ToJson())!.AsObject();

        await Assert.That(json["cohort"]!.GetValue<string>()).IsEqualTo("unknown");
        await Assert.That(json["session_ids"]!.AsArray()).IsEmpty();
        await Assert.That(json["handoff_suppressed"]!.GetValue<string>()).IsEqualTo("import_failed");
    }

    [Test]
    public async Task Write_publishes_owner_only_and_prunes_files_older_than_seven_days() {
        var stale = Config.Root.Path("import-handoff-ffffffffffffffffffffffffffffffff.json");
        await File.WriteAllTextAsync(stale, "{}");
        File.SetLastWriteTimeUtc(stale, Now.AddDays(-8).UtcDateTime);
        var fresh = Config.Root.Path("import-handoff-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee.json");
        await File.WriteAllTextAsync(fresh, "{}");
        File.SetLastWriteTimeUtc(fresh, Now.AddDays(-1).UtcDateTime);

        Compose(Outcome()).Write(Config.Root, new FixedTime(Now));

        var path = ImportHandoffFile.PathFor(Config.Root, RunId);
        await Assert.That(File.Exists(path)).IsTrue();
        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(File.Exists(stale)).IsFalse();
        await Assert.That(File.Exists(fresh)).IsTrue();
        await Assert.That(Directory.GetFiles(Config.Directory, "*.tmp")).IsEmpty();
    }

    [Test]
    public async Task Write_refuses_a_pre_existing_final_path_and_leaves_it_untouched() {
        var path = ImportHandoffFile.PathFor(Config.Root, RunId);
        await File.WriteAllTextAsync(path, "someone else's");

        await Assert.That(() => Compose(Outcome()).Write(Config.Root, new FixedTime(Now))).Throws<IOException>();
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("someone else's");
        await Assert.That(Directory.GetFiles(Config.Directory, "*.tmp")).IsEmpty();
    }

    sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
```

- [ ] **Step 2: Run to verify they fail** — filter `ImportHandoffFileTests`.

- [ ] **Step 3: Implement**

`HandoffSuppressedReason.cs` (enum plus its extension method in one file, the permitted exception):

```csharp
namespace Capacitor.Cli.Commands;

internal enum HandoffSuppressedReason { ImportFailed, NoNewSessions, NothingLanded, AnalyticsNotInPlan, SkillNotInstalled, NoAgentDetected }

internal static class HandoffSuppressedReasonExtensions {
    public static string Wire(this HandoffSuppressedReason reason) => reason switch {
        HandoffSuppressedReason.ImportFailed       => "import_failed",
        HandoffSuppressedReason.NoNewSessions      => "no_new_sessions",
        HandoffSuppressedReason.NothingLanded      => "nothing_landed",
        HandoffSuppressedReason.AnalyticsNotInPlan => "analytics_not_in_plan",
        HandoffSuppressedReason.SkillNotInstalled  => "skill_not_installed",
        HandoffSuppressedReason.NoAgentDetected    => "no_agent_detected",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
```

`HandoffCohort.cs`: the enum. `ImportHandoffFile.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

internal sealed record ImportHandoffFile(
        string RunId, DateTimeOffset WrittenAt, bool HandoffOffered, HandoffSuppressedReason? HandoffSuppressed,
        ForegroundCertainty Certainty, string ServerUrl, string Profile, HandoffCohort Cohort,
        IReadOnlyList<string> SessionIds, IReadOnlyList<string> ForegroundSucceededIds, int UnattributedOnDisk,
        BackgroundImportStatus Background, string? BackgroundLog) {
    public const int SchemaVersion = 1;
    public const int CohortCap     = 500;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static ImportHandoffFile Compose(
            string runId, DateTimeOffset now, bool offered, HandoffSuppressedReason? reason,
            ForegroundImportOutcome outcome, BackgroundImportLaunch background, string serverUrl, string profile, int unattributedOnDisk) {
        var candidates = outcome.RunCandidateIds;
        var cohort = candidates is null ? HandoffCohort.Unknown
                   : candidates.Count > CohortCap ? HandoffCohort.PartialExact
                   : HandoffCohort.Exact;

        return new ImportHandoffFile(
            runId, now, offered, reason, outcome.Certainty, serverUrl.TrimEnd('/'), profile, cohort,
            candidates is null ? [] : [.. candidates.Take(CohortCap)],
            outcome.SucceededIds, unattributedOnDisk, background.Status, background.LogPath);
    }

    public static string PathFor(ConfigRoot config, string runId) => config.Path($"import-handoff-{runId}.json");

    public string ToJson() {
        var ids = new JsonArray();
        foreach (var id in SessionIds) ids.Add((JsonNode?)id);
        var succeeded = new JsonArray();
        foreach (var id in ForegroundSucceededIds) succeeded.Add((JsonNode?)id);

        var json = new JsonObject {
            ["schema_version"]           = SchemaVersion,
            ["run_id"]                   = RunId,
            ["written_at"]               = WrittenAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["handoff_offered"]          = HandoffOffered,
            ["handoff_suppressed"]       = HandoffSuppressed is { } r ? (JsonNode?)r.Wire() : null,
            ["foreground_certainty"]     = Certainty == ForegroundCertainty.Complete ? "complete" : "incomplete",
            ["server_url"]               = ServerUrl,
            ["profile"]                  = Profile,
            ["scope"]                    = "all",
            ["cohort"]                   = Cohort switch { HandoffCohort.Exact => "exact", HandoffCohort.PartialExact => "partial_exact", _ => "unknown" },
            ["session_ids"]              = ids,
            ["foreground_succeeded_ids"] = succeeded,
            ["unattributed_on_disk"]     = UnattributedOnDisk,
            ["background"]               = Background switch {
                BackgroundImportStatus.NotNeeded  => "not_needed",
                BackgroundImportStatus.Running    => "running",
                BackgroundImportStatus.ExitedZero => "exited_zero",
                _                                 => "failed" },
            ["background_log"]           = BackgroundLog is { } log ? (JsonNode?)log : null,
        };

        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Unique temp, created new and owner-only; published with a move that refuses an
    /// existing final name; the temp never survives, whichever step failed.</summary>
    public void Write(ConfigRoot config, TimeProvider time) {
        var final = PathFor(config, RunId);
        var temp  = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = OwnerOnlyFile.CreateNew(temp))
            using (var writer = new StreamWriter(stream)) {
                writer.Write(ToJson());
            }
            File.Move(temp, final);
        } finally {
            if (File.Exists(temp)) { try { File.Delete(temp); } catch { /* best effort */ } }
        }

        Prune(config, time.GetUtcNow());
    }

    static void Prune(ConfigRoot config, DateTimeOffset now) {
        foreach (var path in Directory.EnumerateFiles(config.Directory, "import-handoff-*.json")) {
            try {
                if (now - File.GetLastWriteTimeUtc(path) > Retention) File.Delete(path);
            } catch { /* another process may own it; best effort */ }
        }
    }
}
```

`File.Move(temp, final)` without the `overwrite` argument throws `IOException` when `final` exists. `written_at` renders as `2026-09-17T12:00:00+00:00` for a UTC value.

- [ ] **Step 4: Run to verify they pass** — filter `ImportHandoffFileTests`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/HandoffSuppressedReason.cs src/Capacitor.Cli/Commands/HandoffCohort.cs src/Capacitor.Cli/Commands/ImportHandoffFile.cs test/Capacitor.Cli.Tests.Unit/Commands/ImportHandoffFileTests.cs
git commit -m "[AI-2169] Write a per-run handoff file the eval-watch skill can read"
```

---

### Task 9: The handoff decision table

**Files:**
- Create: `src/Capacitor.Cli/Commands/HandoffDecision.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/HandoffDecisionTests.cs`

**Interfaces:**
- Produces:

```csharp
internal sealed record HandoffDecision(bool Offered, HandoffSuppressedReason? Reason) {
    /// The §4 table, top to bottom, first match wins.
    public static HandoffDecision Decide(
        ForegroundImportOutcome outcome, BackgroundImportStatus background,
        bool analyticsAllowed, int eligibleVendors, int detectedVendors);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class HandoffDecisionTests {
    static ForegroundImportOutcome O(ForegroundCertainty c = ForegroundCertainty.Complete, int selected = 5, int succeeded = 5, int skipped = 0, int failed = 0,
                                      bool remainder = true, bool knownCandidates = true, int candidates = 10) =>
        new(c, selected, succeeded, skipped, failed, remainder,
            knownCandidates ? Enumerable.Range(0, candidates).Select(i => $"c{i}").ToList() : null,
            Enumerable.Range(0, succeeded).Select(i => $"c{i}").ToList());

    static HandoffDecision Decide(ForegroundImportOutcome o, BackgroundImportStatus bg = BackgroundImportStatus.Running,
                                  bool analytics = true, int eligible = 2, int detected = 2) =>
        HandoffDecision.Decide(o, bg, analytics, eligible, detected);

    [Test] public async Task Row1_incomplete_with_nothing_landed_is_import_failed() =>
        await Assert.That(Decide(O(ForegroundCertainty.Incomplete, succeeded: 0)).Reason).IsEqualTo(HandoffSuppressedReason.ImportFailed);

    [Test] public async Task Row2_failed_background_with_nothing_landed_is_import_failed() =>
        await Assert.That(Decide(O(succeeded: 0, failed: 5), bg: BackgroundImportStatus.Failed).Reason).IsEqualTo(HandoffSuppressedReason.ImportFailed);

    [Test] public async Task Row3_empty_known_candidates_is_no_new_sessions_whatever_the_background() =>
        await Assert.That(Decide(O(selected: 0, succeeded: 0, candidates: 0), bg: BackgroundImportStatus.Running).Reason).IsEqualTo(HandoffSuppressedReason.NoNewSessions);

    [Test] public async Task Row4_all_skipped_with_nothing_left_is_nothing_landed() =>
        await Assert.That(Decide(O(succeeded: 0, skipped: 5, remainder: false), bg: BackgroundImportStatus.NotNeeded).Reason).IsEqualTo(HandoffSuppressedReason.NothingLanded);

    [Test] public async Task Row5_cached_denial_over_a_good_pass_is_analytics_not_in_plan() =>
        await Assert.That(Decide(O(), analytics: false).Reason).IsEqualTo(HandoffSuppressedReason.AnalyticsNotInPlan);

    [Test] public async Task Row6_no_eligible_vendor_with_one_detected_is_skill_not_installed() =>
        await Assert.That(Decide(O(), eligible: 0, detected: 1).Reason).IsEqualTo(HandoffSuppressedReason.SkillNotInstalled);

    [Test] public async Task Row7_no_vendor_detected_is_no_agent_detected() =>
        await Assert.That(Decide(O(), eligible: 0, detected: 0).Reason).IsEqualTo(HandoffSuppressedReason.NoAgentDetected);

    [Test] public async Task Row8_offers_on_a_success_or_a_running_background() {
        await Assert.That(Decide(O()).Offered).IsTrue();
        await Assert.That(Decide(O(succeeded: 0, skipped: 5), bg: BackgroundImportStatus.Running).Offered).IsTrue();
        await Assert.That(Decide(O(succeeded: 0, skipped: 5), bg: BackgroundImportStatus.ExitedZero).Offered).IsTrue();
    }

    [Test] public async Task Import_outcome_outranks_the_plan_gate() {
        await Assert.That(Decide(O(ForegroundCertainty.Incomplete, succeeded: 0), analytics: false).Reason).IsEqualTo(HandoffSuppressedReason.ImportFailed);
        await Assert.That(Decide(O(selected: 0, succeeded: 0, candidates: 0), analytics: false).Reason).IsEqualTo(HandoffSuppressedReason.NoNewSessions);
    }

    [Test] public async Task Unknown_candidates_with_a_running_background_are_offered() =>
        await Assert.That(Decide(O(ForegroundCertainty.Incomplete, succeeded: 2, knownCandidates: false)).Offered).IsTrue();
}
```

- [ ] **Step 2: Run to verify they fail** — filter `HandoffDecisionTests`.

- [ ] **Step 3: Implement**

```csharp
namespace Capacitor.Cli.Commands;

/// <summary>Whether setup offers the eval-watch handoff, and why not. Rows are evaluated in order
/// and the first match wins; the import's own outcome outranks the plan gate so retry advice is only
/// ever given when a retry is warranted.</summary>
internal sealed record HandoffDecision(bool Offered, HandoffSuppressedReason? Reason) {
    public static HandoffDecision Decide(
            ForegroundImportOutcome outcome, BackgroundImportStatus background,
            bool analyticsAllowed, int eligibleVendors, int detectedVendors) {
        if (outcome.Certainty == ForegroundCertainty.Incomplete && outcome.Succeeded == 0)
            return new(false, HandoffSuppressedReason.ImportFailed);
        if (background == BackgroundImportStatus.Failed && outcome.Succeeded == 0)
            return new(false, HandoffSuppressedReason.ImportFailed);
        if (outcome.RunCandidateIds is { Count: 0 })
            return new(false, HandoffSuppressedReason.NoNewSessions);
        if (outcome.Succeeded == 0 && background == BackgroundImportStatus.NotNeeded)
            return new(false, HandoffSuppressedReason.NothingLanded);
        if (!analyticsAllowed)
            return new(false, HandoffSuppressedReason.AnalyticsNotInPlan);
        if (eligibleVendors == 0 && detectedVendors > 0)
            return new(false, HandoffSuppressedReason.SkillNotInstalled);
        if (detectedVendors == 0)
            return new(false, HandoffSuppressedReason.NoAgentDetected);

        return new(true, null);
    }
}
```

- [ ] **Step 4: Run to verify they pass** — filter `HandoffDecisionTests`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/HandoffDecision.cs test/Capacitor.Cli.Tests.Unit/Commands/HandoffDecisionTests.cs
git commit -m "[AI-2169] Decide the eval-watch handoff from one precedence table"
```

---

### Task 10: Launch recipes, vendor eligibility and the agent launcher

**Files:**
- Create: `src/Capacitor.Cli/Commands/HandoffLaunchRecipe.cs`, `src/Capacitor.Cli/Commands/HandoffVendorEligibility.cs`, `src/Capacitor.Cli/Commands/HandoffLaunchResult.cs`, `src/Capacitor.Cli/Commands/IHandoffAgentLauncher.cs`, `src/Capacitor.Cli/Commands/HandoffAgentLauncher.cs`
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs` — generalize `ClaudeCarriesGuidedTour(settingsPath, pluginDir)` (~:1128) into `internal static bool ClaudeCarriesSkill(string claudeSettingsPath, string? pluginDir, string skillName)` and make `ClaudeCarriesGuidedTour` call it with `GuidedTourSkillName`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/HandoffLaunchRecipeTests.cs`, `HandoffVendorEligibilityTests.cs`, `HandoffAgentLauncherTests.cs`, `FakeHandoffAgentLauncher.cs`

**Interfaces:**
- Consumes: `HarnessRegistry.Identities` (`HarnessIdentity(HarnessId Id, string Label)`), `HarnessRegistry.Detected(HarnessId)`, `HarnessRegistry.ResolveExecutable(HarnessId)`, `AgentsSkillsInstaller.HasSkill(targetDir, sourceName)`, `CodingAgentsStep.Paths` (`ClaudeSettingsPath`, `PluginDir`, `AgentsSkillsDir`, `KiroSkillsDir`, `AntigravitySkillsDir`), `IProcessStarter`, `ConfigRoot`, `ProfileOverrides`.
- Produces:

```csharp
internal sealed record HandoffLaunchRecipe(HarnessId Vendor, IReadOnlyList<string> LeadingArgs, bool PromptLast = true) {
    public static readonly IReadOnlyDictionary<HarnessId, HandoffLaunchRecipe> All;   // nine entries
    public IReadOnlyList<string> Argv(string prompt);                                  // LeadingArgs + [prompt]
}

internal sealed record HandoffVendor(HarnessId Id, string Label, string? Executable) { public bool Launchable => Executable is not null; }

internal static class HandoffVendorEligibility {
    public const string SkillName = "eval-watch";
    /// Detected AND the eval-watch skill is on disk where this vendor reads skills. Registry order.
    public static IReadOnlyList<HandoffVendor> Eligible(HarnessRegistry harnesses, CodingAgentsStep.Paths paths);
    public static int Detected(HarnessRegistry harnesses);
}

internal enum HandoffLaunchStatus { Ran, LaunchFailed }
internal sealed record HandoffLaunchResult(HandoffLaunchStatus Status, int? ExitCode, string? Error);

internal sealed record HandoffLaunchRequest(HandoffVendor Vendor, string Prompt, string ProfileName, string WorkingDirectory);

internal interface IHandoffAgentLauncher {
    HandoffLaunchResult Launch(HandoffLaunchRequest request);   // blocks until the agent exits
}
```

Skill location per vendor: Claude → `ClaudeCarriesSkill(paths.ClaudeSettingsPath, paths.PluginDir, SkillName)`; Kiro → `HasSkill(paths.KiroSkillsDir, SkillName)`; Antigravity → `HasSkill(paths.AntigravitySkillsDir, SkillName)`; every other vendor → `HasSkill(paths.AgentsSkillsDir, SkillName)`.

- [ ] **Step 1: Write the failing tests**

`HandoffLaunchRecipeTests.cs`:

```csharp
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class HandoffLaunchRecipeTests {
    const string P = "Follow my kcap import\n(run: 0123456789abcdef0123456789abcdef)";

    [Test]
    [Arguments(HarnessId.Claude,      new[] { P })]
    [Arguments(HarnessId.Codex,       new[] { P })]
    [Arguments(HarnessId.Cursor,      new[] { P })]
    [Arguments(HarnessId.Copilot,     new[] { "-i", P })]
    [Arguments(HarnessId.Gemini,      new[] { "-i", P })]
    [Arguments(HarnessId.Kiro,        new[] { "chat", P })]
    [Arguments(HarnessId.Pi,          new[] { P })]
    [Arguments(HarnessId.OpenCode,    new[] { "--prompt", P })]
    [Arguments(HarnessId.Antigravity, new[] { "-i", P })]
    public async Task Every_vendor_has_a_recipe_and_the_prompt_is_one_argument(HarnessId vendor, string[] expected) {
        var argv = HandoffLaunchRecipe.All[vendor].Argv(P).ToList();

        await Assert.That(argv).IsEquivalentTo(expected);
        await Assert.That(argv.Last()).IsEqualTo(P);
    }

    [Test]
    public async Task The_registry_covers_every_harness() =>
        await Assert.That(HandoffLaunchRecipe.All.Keys.ToList()).IsEquivalentTo(HarnessRegistry.Identities.Select(i => i.Id).ToList());
}
```

`HandoffVendorEligibilityTests.cs` (build the registry with `TestHarnesses.All(detected: [...])` and a `BinaryProbe` over a temp dir holding executables via `TestBinaries`; build `CodingAgentsStep.Paths` pointing at temp skills dirs):

```csharp
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class HandoffVendorEligibilityTests {
    static CodingAgentsStep.Paths Paths(TempDir root) => new(
        ClaudeSettingsPath: root.PathTo("claude-settings.json"), ClaudeScopeLabel: "user", PluginDir: root.PathTo("plugin"),
        CodexHooksPath: root.PathTo("codex-hooks"), CursorHooksPath: root.PathTo("cursor-hooks"), CopilotHooksPath: root.PathTo("copilot-hooks"),
        GeminiSettingsPath: root.PathTo("gemini-settings.json"), AgentsSkillsDir: root.PathTo("agents-skills"), LegacyCodexSkillsDir: root.PathTo("legacy"),
        KiroSkillsDir: root.PathTo("kiro-skills"), AntigravitySkillsDir: root.PathTo("antigravity-skills"));

    static void InstallShared(TempDir root)      => root.CreateFile(["agents-skills", "kcap-eval-watch", "SKILL.md"], "skill");
    static void InstallKiro(TempDir root)        => root.CreateFile(["kiro-skills", "kcap-eval-watch", "SKILL.md"], "skill");

    [Test]
    public async Task A_detected_vendor_with_the_skill_is_eligible_and_launchable_only_when_its_binary_resolves() {
        using var root = new TempDir();
        InstallShared(root);
        var harnesses = TestHarnesses.All(detected: [HarnessId.Codex, HarnessId.Pi]);   // TestBinaries.None: nothing resolves

        var eligible = HandoffVendorEligibility.Eligible(harnesses, Paths(root));

        await Assert.That(eligible.Select(v => v.Id).ToList()).IsEquivalentTo([HarnessId.Codex, HarnessId.Pi]);
        await Assert.That(eligible.All(v => !v.Launchable)).IsTrue();
        await Assert.That(HandoffVendorEligibility.Detected(harnesses)).IsEqualTo(2);
    }

    [Test]
    public async Task Kiro_reads_its_own_skills_directory_not_the_shared_tree() {
        using var root = new TempDir();
        InstallShared(root);
        var harnesses = TestHarnesses.All(detected: [HarnessId.Kiro]);

        await Assert.That(HandoffVendorEligibility.Eligible(harnesses, Paths(root))).IsEmpty();

        InstallKiro(root);
        await Assert.That(HandoffVendorEligibility.Eligible(harnesses, Paths(root)).Select(v => v.Id).ToList()).IsEquivalentTo([HarnessId.Kiro]);
    }

    [Test]
    public async Task An_undetected_vendor_is_never_eligible_even_with_the_skill_installed() {
        using var root = new TempDir();
        InstallShared(root);

        await Assert.That(HandoffVendorEligibility.Eligible(TestHarnesses.All(detected: []), Paths(root))).IsEmpty();
    }
}
```

`FakeHandoffAgentLauncher.cs`:

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

sealed class FakeHandoffAgentLauncher(HandoffLaunchResult result) : IHandoffAgentLauncher {
    public HandoffLaunchRequest? Seen { get; private set; }
    public int Launches { get; private set; }

    public static FakeHandoffAgentLauncher Ran()     => new(new(HandoffLaunchStatus.Ran, 0, null));
    public static FakeHandoffAgentLauncher Failing() => new(new(HandoffLaunchStatus.LaunchFailed, 127, "not found"));

    public HandoffLaunchResult Launch(HandoffLaunchRequest request) { Seen = request; Launches++; return result; }
}
```

`HandoffAgentLauncherTests.cs`:

```csharp
using System.Diagnostics;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class HandoffAgentLauncherTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static HandoffLaunchRequest Request(string exe) => new(new HandoffVendor(HarnessId.Gemini, "Gemini", exe), "Follow my kcap import\n(run: x)", "work", "/tmp/wd");

    [Test]
    public async Task Psi_is_interactive_pinned_to_the_saved_profile_and_uses_the_recipe() {
        using var _ = EnvScope.Exclusive(ProfileOverrides.UrlVar, "https://ambient.test");
        var starter = FakeProcessStarter.Running(_ => Process.Start(new ProcessStartInfo("sleep", "3") { UseShellExecute = false }));

        var result = new HandoffAgentLauncher(Config.Root, starter).Launch(Request("/opt/bin/gemini"));

        var psi = starter.Seen!;
        await Assert.That(psi.FileName).IsEqualTo("/opt/bin/gemini");
        await Assert.That(psi.ArgumentList.ToList()).IsEquivalentTo(["-i", "Follow my kcap import\n(run: x)"]);
        await Assert.That(psi.UseShellExecute).IsFalse();
        await Assert.That(psi.RedirectStandardInput || psi.RedirectStandardOutput || psi.RedirectStandardError).IsFalse();
        await Assert.That(psi.WorkingDirectory).IsEqualTo("/tmp/wd");
        await Assert.That(psi.Environment[ConfigRoot.ConfigDirEnvVar]).IsEqualTo(Config.Directory);
        await Assert.That(psi.Environment[ProfileOverrides.ProfileVar]).IsEqualTo("work");
        await Assert.That(psi.Environment.ContainsKey(ProfileOverrides.UrlVar)).IsFalse();
        await Assert.That(result.Status).IsEqualTo(HandoffLaunchStatus.Ran);
    }

    [Test]
    public async Task Nonzero_exit_inside_two_seconds_is_a_launch_failure_but_a_zero_exit_is_not() {
        var fail = FakeProcessStarter.Running(_ => Process.Start(new ProcessStartInfo("false") { UseShellExecute = false }));
        var ok   = FakeProcessStarter.Running(_ => Process.Start(new ProcessStartInfo("true")  { UseShellExecute = false }));

        await Assert.That(new HandoffAgentLauncher(Config.Root, fail).Launch(Request("/bin/false")).Status).IsEqualTo(HandoffLaunchStatus.LaunchFailed);
        await Assert.That(new HandoffAgentLauncher(Config.Root, ok).Launch(Request("/bin/true")).Status).IsEqualTo(HandoffLaunchStatus.Ran);
    }

    [Test]
    public async Task Nonzero_exit_after_two_seconds_counts_as_ran() {
        var late = FakeProcessStarter.Running(_ => Process.Start(new ProcessStartInfo("sh", "-c 'sleep 2.5; exit 3'") { UseShellExecute = false }));

        var result = new HandoffAgentLauncher(Config.Root, late).Launch(Request("/bin/sh"));

        await Assert.That(result.Status).IsEqualTo(HandoffLaunchStatus.Ran);
        await Assert.That(result.ExitCode).IsEqualTo(3);
    }

    [Test]
    public async Task A_refused_or_throwing_start_is_a_launch_failure() {
        await Assert.That(new HandoffAgentLauncher(Config.Root, FakeProcessStarter.Refusing()).Launch(Request("/x")).Status).IsEqualTo(HandoffLaunchStatus.LaunchFailed);
        await Assert.That(new HandoffAgentLauncher(Config.Root, FakeProcessStarter.Throwing(new InvalidOperationException("boom"))).Launch(Request("/x")).Error).Contains("boom");
    }
}
```

(Guard the Unix-process tests with `if (OperatingSystem.IsWindows()) return;`.)

- [ ] **Step 2: Run to verify they fail** — filters `HandoffLaunchRecipeTests`, `HandoffVendorEligibilityTests`, `HandoffAgentLauncherTests`.

- [ ] **Step 3: Implement**

`HandoffLaunchRecipe.cs`:

```csharp
using System.Collections.Frozen;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

/// <summary>How each vendor's CLI starts an interactive session with an initial prompt, as its own
/// --help documents it. The prompt is always one argv element.</summary>
internal sealed record HandoffLaunchRecipe(HarnessId Vendor, IReadOnlyList<string> LeadingArgs) {
    public static readonly IReadOnlyDictionary<HarnessId, HandoffLaunchRecipe> All = new Dictionary<HarnessId, HandoffLaunchRecipe> {
        [HarnessId.Claude]      = new(HarnessId.Claude,      []),
        [HarnessId.Codex]       = new(HarnessId.Codex,       []),
        [HarnessId.Cursor]      = new(HarnessId.Cursor,      []),
        [HarnessId.Copilot]     = new(HarnessId.Copilot,     ["-i"]),
        [HarnessId.Gemini]      = new(HarnessId.Gemini,      ["-i"]),
        [HarnessId.Kiro]        = new(HarnessId.Kiro,        ["chat"]),
        [HarnessId.Pi]          = new(HarnessId.Pi,          []),
        [HarnessId.OpenCode]    = new(HarnessId.OpenCode,    ["--prompt"]),
        [HarnessId.Antigravity] = new(HarnessId.Antigravity, ["-i"]),
    }.ToFrozenDictionary();

    public IReadOnlyList<string> Argv(string prompt) => [.. LeadingArgs, prompt];
}
```

`HandoffVendorEligibility.cs`:

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

internal sealed record HandoffVendor(HarnessId Id, string Label, string? Executable) {
    public bool Launchable => Executable is not null;
}

/// <summary>Which detected vendors can answer the eval-watch prompt: the skill must be where that
/// vendor reads skills, the same oracle the guided-tour offer uses.</summary>
internal static class HandoffVendorEligibility {
    public const string SkillName = "eval-watch";

    public static IReadOnlyList<HandoffVendor> Eligible(HarnessRegistry harnesses, CodingAgentsStep.Paths paths) => [
        .. HarnessRegistry.Identities
            .Where(i => harnesses.Detected(i.Id) && HasSkill(i.Id, paths))
            .Select(i => new HandoffVendor(i.Id, i.Label, harnesses.ResolveExecutable(i.Id)))
    ];

    public static int Detected(HarnessRegistry harnesses) => HarnessRegistry.Identities.Count(i => harnesses.Detected(i.Id));

    static bool HasSkill(HarnessId id, CodingAgentsStep.Paths paths) => id switch {
        HarnessId.Claude      => SetupCommand.ClaudeCarriesSkill(paths.ClaudeSettingsPath, paths.PluginDir, SkillName),
        HarnessId.Kiro        => AgentsSkillsInstaller.HasSkill(paths.KiroSkillsDir, SkillName),
        HarnessId.Antigravity => AgentsSkillsInstaller.HasSkill(paths.AntigravitySkillsDir, SkillName),
        _                     => AgentsSkillsInstaller.HasSkill(paths.AgentsSkillsDir, SkillName),
    };
}
```

`HandoffAgentLauncher.cs`:

```csharp
using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>Runs the chosen agent in the foreground with the handoff prompt, pinned to the profile
/// setup saved so every kcap MCP server it spawns talks to the server the import ran against.</summary>
sealed class HandoffAgentLauncher(ConfigRoot config, IProcessStarter starter) : IHandoffAgentLauncher {
    static readonly TimeSpan FailureWindow = TimeSpan.FromMilliseconds(2000);

    public HandoffLaunchResult Launch(HandoffLaunchRequest request) {
        if (request.Vendor.Executable is not { } exe)
            return new HandoffLaunchResult(HandoffLaunchStatus.LaunchFailed, null, $"{request.Vendor.Label} is not on the search path");

        var psi = new ProcessStartInfo { FileName = exe, WorkingDirectory = request.WorkingDirectory, UseShellExecute = false };
        foreach (var arg in HandoffLaunchRecipe.All[request.Vendor.Id].Argv(request.Prompt)) psi.ArgumentList.Add(arg);
        psi.Environment[ConfigRoot.ConfigDirEnvVar]  = config.Directory;
        psi.Environment[ProfileOverrides.ProfileVar] = request.ProfileName;
        psi.Environment.Remove(ProfileOverrides.UrlVar);

        Process? process;
        var started = Stopwatch.StartNew();
        try {
            process = starter.Start(psi);
        } catch (Exception ex) {
            return new HandoffLaunchResult(HandoffLaunchStatus.LaunchFailed, null, ex.Message);
        }
        if (process is null) return new HandoffLaunchResult(HandoffLaunchStatus.LaunchFailed, null, "the process did not start");

        process.WaitForExit();
        var failedFast = process.ExitCode != 0 && started.Elapsed < FailureWindow;

        return new HandoffLaunchResult(failedFast ? HandoffLaunchStatus.LaunchFailed : HandoffLaunchStatus.Ran, process.ExitCode,
            failedFast ? $"exit {process.ExitCode}" : null);
    }
}
```

In `SetupCommand.cs`, replace `ClaudeCarriesGuidedTour` with a two-method shape: `static bool ClaudeCarriesGuidedTour(string claudeSettingsPath, string? pluginDir) => ClaudeCarriesSkill(claudeSettingsPath, pluginDir, GuidedTourSkillName);` and `internal static bool ClaudeCarriesSkill(string claudeSettingsPath, string? pluginDir, string skillName)` with the existing body parameterized on `skillName`. Register in `CommandServices.cs`: `services.AddSingleton<IHandoffAgentLauncher, HandoffAgentLauncher>();`.

- [ ] **Step 4: Run to verify they pass** — the three filters plus `SetupCommandTests` (guided-tour oracle tests still pass).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/HandoffLaunchRecipe.cs src/Capacitor.Cli/Commands/HandoffVendorEligibility.cs src/Capacitor.Cli/Commands/HandoffLaunchResult.cs src/Capacitor.Cli/Commands/IHandoffAgentLauncher.cs src/Capacitor.Cli/Commands/HandoffAgentLauncher.cs src/Capacitor.Cli/Commands/SetupCommand.cs src/Capacitor.Cli/Commands/CommandServices.cs test/Capacitor.Cli.Tests.Unit/Commands/HandoffLaunchRecipeTests.cs test/Capacitor.Cli.Tests.Unit/Commands/HandoffVendorEligibilityTests.cs test/Capacitor.Cli.Tests.Unit/Commands/HandoffAgentLauncherTests.cs test/Capacitor.Cli.Tests.Unit/Commands/FakeHandoffAgentLauncher.cs
git commit -m "[AI-2169] Launch any detected vendor's CLI with the eval-watch prompt"
```

---

### Task 11: `SetupDecisions.DecideImport` loses the repository gate

**Files:**
- Modify: `src/Capacitor.Cli/Commands/SetupDecisions.cs` (~:118-128)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/SetupDecisionsTests.cs`

**Interfaces:**
- Produces: `public static ImportDecision DecideImport(bool authSatisfied, bool skipImport, bool noPrompt, Func<bool> promptYesNo)` — guard order: not authenticated → `Skip("not authenticated — skipping import")`; `--skip-import` → `Skip("--skip-import")`; `--no-prompt` → `Run`; else the prompt decides (`Skip(null)` on decline).

- [ ] **Step 1: Write the failing tests** (replace the existing `DecideImport_*` tests):

```csharp
[Test]
public async Task DecideImport_runs_outside_a_repository_when_authenticated() {
    var d = SetupDecisions.DecideImport(authSatisfied: true, skipImport: false, noPrompt: true, promptYesNo: () => throw new InvalidOperationException("must not prompt"));
    await Assert.That(d.Outcome).IsEqualTo(SetupDecisions.ImportOutcome.Run);
}

[Test]
public async Task DecideImport_skips_with_a_reason_when_not_authenticated_or_opted_out() {
    await Assert.That(SetupDecisions.DecideImport(false, false, false, () => true).SkipReason).Contains("not authenticated");
    await Assert.That(SetupDecisions.DecideImport(true, true, false, () => true).SkipReason).IsEqualTo("--skip-import");
}

[Test]
public async Task DecideImport_declined_prompt_skips_without_a_reason() {
    var d = SetupDecisions.DecideImport(true, false, false, () => false);
    await Assert.That(d.Outcome).IsEqualTo(SetupDecisions.ImportOutcome.Skip);
    await Assert.That(d.SkipReason).IsNull();
}
```

- [ ] **Step 2: Run to verify they fail** — filter `SetupDecisionsTests` (compile error on the arity).

- [ ] **Step 3: Implement**

```csharp
public static ImportDecision DecideImport(bool authSatisfied, bool skipImport, bool noPrompt, Func<bool> promptYesNo) {
    if (!authSatisfied) return new ImportDecision(ImportOutcome.Skip, "not authenticated — skipping import");
    if (skipImport)     return new ImportDecision(ImportOutcome.Skip, "--skip-import");
    if (noPrompt)       return new ImportDecision(ImportOutcome.Run, null);

    return promptYesNo()
        ? new ImportDecision(ImportOutcome.Run, null)
        : new ImportDecision(ImportOutcome.Skip, null);
}
```

Rewrite the doc comment above it for the machine-wide step (no repository guard). Update the single call site in `SetupCommand.RunImportStepAsync` to the new arity (Task 12 rewrites that method anyway; keep it compiling here).

- [ ] **Step 4: Run to verify they pass** — filter `SetupDecisionsTests`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/SetupDecisions.cs src/Capacitor.Cli/Commands/SetupCommand.cs test/Capacitor.Cli.Tests.Unit/Commands/SetupDecisionsTests.cs
git commit -m "[AI-2169] Let the import step run from any directory"
```

---

### Task 12: Step 6 — discovery, prompt, foreground, child, handoff, picker

**Files:**
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs` — constructor (~:420-426), step 6 block (~:973-1004), `RunImportStepAsync` (~:1194-1243), `WriteNextSteps`/`NextStepItems` (~:1067-1100), constants near `GuidedTourPrompt` (~:1145)
- Modify: `test/Capacitor.Cli.Tests.Unit/Commands/SetupCommandTests.cs` (`Command(...)` helper and the step-6 tests), `test/Capacitor.Cli.Tests.Unit/Commands/SetupChosenServerTests.cs` (constructor arity)
- Test: additions to `SetupCommandTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 5–10; `PlanEntitlementStore.Get(serverUrl, config, now).Allows(PlanEntitlements.Analytics)`; `HandoffVendorEligibility`; `CodingAgentsStep.Paths stepPaths` (~:730) and `detected` (~:645) already in scope at step 6.
- Produces on `SetupCommand`:

```csharp
internal const string ImportPrompt        = "Import past sessions from this machine?";
internal const string EvalWatchPrompt     = "Follow my kcap import";
internal const string EvalWatchSkillName  = HandoffVendorEligibility.SkillName;
internal static string HandoffPromptText(string runId) => $"{EvalWatchPrompt}\n(run: {runId})";

internal sealed record ImportStepInputs(
    bool AuthSatisfied, bool SkipImport, bool NoPrompt, Func<bool> PromptYesNo,
    ProfileContext Profiles, string ProfileName, string ServerUrl, string DefaultVisibility,
    (string Owner, string Name)? CurrentRepo, string WorkingDirectory,
    CodingAgentsStep.Paths Paths, FirstRunImportAnswer? BrowserImport, bool BrowserImportFailed);

internal sealed record ImportStepResult(bool Ran, string? RunId, HandoffDecision? Handoff, string? PasteBlock);

internal Task<ImportStepResult> RunImportStepAsync(ImportStepInputs inputs);
internal static List<(string Question, string Answer)> NextStepItems(bool offerGuidedTour, string? handoffPaste);
```

Constructor gains `IBackgroundImportSpawner spawner, IHandoffAgentLauncher launcher` after `ISetupImportRunner imports`.

**Behaviour of `RunImportStepAsync` (spec §2–§4), in order:**
1. Browser answered → `BrowserImportSummary` lines as today; return `Ran: false`.
2. `SetupDecisions.DecideImport(inputs.AuthSatisfied, inputs.SkipImport, inputs.NoPrompt, …)` — but the prompt needs the figures first, so: if the decision would prompt (authenticated, not skipped, not no-prompt) run discovery first: `imports.DiscoverAsync(inputs.Profiles)`; `Fault` → print `  ! Could not scan for past sessions: <message>. Run kcap import --all to try again.` and return `Ran: false`; print the three figures (`repositories`, `sessions attributed`, `sessions on disk with no repository match`); zero total → print `  No past sessions found on this machine.` and return. Then call `DecideImport` with `promptYesNo` = `() => AnsiConsole.Prompt(new ConfirmationPrompt(ImportPrompt) { DefaultValue = true })`. `Skip` → print the reason (or the `kcap import --all` hint on a decline) and return `Ran: false`.
3. `--no-prompt`: `imports.RunAsync(new ImportInvocation(new ImportScope.All(), MaxSessions: null, inputs.CurrentRepo, inputs.DefaultVisibility, AutoSkipExclusions: true, ForcePrivate: false, SkipTitle: false, inputs.Profiles))`; warn on `Fault`/non-zero as today; return `Ran: true` with no run id, no handoff, no file.
4. Interactive accept: `runId = Guid.NewGuid().ToString("N")`; `run = imports.RunAsync(… MaxSessions: 5, SkipTitle: false …)`; `outcome = ForegroundImportOutcome.From(run)`; on `Fault` print the existing "Import of past sessions failed" warning.
5. `launch = outcome.RemainderExists || outcome.Failed > 0 || outcome.Certainty == Incomplete ? spawner.Spawn(new(runId, inputs.ProfileName, inputs.DefaultVisibility, inputs.WorkingDirectory)) : BackgroundImportLaunch.NotNeeded`; print the pinned status line (spec §2 table).
6. `analyticsAllowed = PlanEntitlementStore.Get(inputs.ServerUrl, config, time.GetUtcNow()).Allows(PlanEntitlements.Analytics)`; `eligible = HandoffVendorEligibility.Eligible(harnesses, inputs.Paths)`; `decision = HandoffDecision.Decide(outcome, launch.Status, analyticsAllowed, eligible.Count, HandoffVendorEligibility.Detected(harnesses))`.
7. Write the file: `ImportHandoffFile.Compose(runId, time.GetUtcNow(), decision.Offered, decision.Reason, outcome, launch, inputs.ServerUrl, inputs.ProfileName, unattributedOnDisk).Write(config, time)` inside try/catch → warn on `IOException`.
8. Rows 5–7 print their one line (`analytics_not_in_plan`: "Insights isn't in this workspace's plan, so the eval-watch handoff is skipped."; `skill_not_installed`: "No detected agent has the kcap eval-watch skill — run `kcap plugin install` (with the agent's flag) to add it."; `no_agent_detected`: "No coding agent detected to hand off to."). Rows 1–4 print nothing more. Return `Ran: true, Handoff: decision, PasteBlock: null`.
9. Row 8: `prompt = HandoffPromptText(runId)`. `SelectionPrompt<string>` over `eligible.Select(v => v.Label)` plus `"Skip"`, title `"Open a coding agent to follow the import and its evals? (setup will finish after you close the agent)"`. Skip/cancel (`Esc` surfaces as an exception from Spectre; catch and treat as Skip) → paste block. Launchable pick → `launcher.Launch(new(vendor, prompt, inputs.ProfileName, inputs.WorkingDirectory))`; `LaunchFailed` → warn and paste block; `Ran` → nothing more. Non-launchable pick → paste block. Return `PasteBlock` = the two-line prompt when it must be shown, else null.

`NextStepItems(offerGuidedTour, handoffPaste)` adds, **above** the guided-tour item, `("Want to watch your import and its evals from your agent?", "Paste this into your coding agent:\n[cyan]<paste block>[/]")` when `handoffPaste` is non-null. Step 6's caller passes `result.PasteBlock` through to `WriteNextSteps`.

- [ ] **Step 1: Write the failing tests**

Update `Command(...)` in `SetupCommandTests` to take the two new fakes (default `FakeBackgroundImportSpawner.Running()` / `FakeHandoffAgentLauncher.Ran()`), and `SetupChosenServerTests.Started` to pass them. Replace the four `RunImportStepAsync_*` tests with:

```csharp
ImportStepInputs Inputs(bool noPrompt = false, Func<bool>? prompt = null, FirstRunImportAnswer? browser = null, bool auth = true, bool skip = false, string visibility = "org_public") => new(
    AuthSatisfied: auth, SkipImport: skip, NoPrompt: noPrompt, PromptYesNo: prompt ?? (() => true),
    Profiles: Resolutions.At("https://example.test", Config.Root), ProfileName: "work", ServerUrl: "https://example.test",
    DefaultVisibility: visibility, CurrentRepo: null, WorkingDirectory: Config.Directory,
    Paths: PathsWithEvalWatchFor(HarnessId.Codex), BrowserImport: browser, BrowserImportFailed: false);

static ImportCommand.ImportDiscoveryResult Discovery(int repos, int attributed, int unmatched) { /* build via ImportDiscoverySummary.Build over synthetic rows, as ImportDiscoverySummaryTests does */ }

[Test]
public async Task Interactive_accept_runs_a_capped_machine_wide_import_and_offers_the_handoff() {
    var runner  = FakeImportRunner.Succeeding().Discovering(Discovery(3, 20, 5));
    var spawner = FakeBackgroundImportSpawner.Running();
    var launcher = FakeHandoffAgentLauncher.Ran();

    var result = await Command(runner, spawner, launcher, Config.Directory).RunImportStepAsync(Inputs());

    var inv = runner.Captured!;
    await Assert.That(inv.Scope).IsEqualTo(new ImportScope.All());
    await Assert.That(inv.MaxSessions).IsEqualTo(5);
    await Assert.That(inv.SkipTitle).IsFalse();
    await Assert.That(inv.DefaultVisibility).IsEqualTo("org_public");
    await Assert.That(spawner.Spawns).IsEqualTo(1);
    await Assert.That(spawner.Seen!.ProfileName).IsEqualTo("work");
    await Assert.That(spawner.Seen.DefaultVisibility).IsEqualTo("org_public");
    await Assert.That(result.Handoff!.Offered).IsTrue();
    var file = Directory.GetFiles(Config.Directory, "import-handoff-*.json").Single();
    await Assert.That(await File.ReadAllTextAsync(file)).Contains("\"handoff_offered\": true");
}

[Test]
public async Task No_prompt_imports_everything_uncapped_with_no_child_no_file_no_handoff() {
    var runner = FakeImportRunner.Succeeding(); var spawner = FakeBackgroundImportSpawner.Running();

    var result = await Command(runner, spawner, FakeHandoffAgentLauncher.Ran(), Config.Directory).RunImportStepAsync(Inputs(noPrompt: true, prompt: () => throw new InvalidOperationException("must not prompt")));

    await Assert.That(runner.DiscoverCalls).IsEqualTo(0);
    await Assert.That(runner.Captured!.MaxSessions).IsNull();
    await Assert.That(spawner.Spawns).IsEqualTo(0);
    await Assert.That(result.Handoff).IsNull();
    await Assert.That(Directory.GetFiles(Config.Directory, "import-handoff-*.json")).IsEmpty();
}

[Test]
public async Task Declined_prompt_and_skip_flag_run_nothing_and_write_nothing() {
    var runner = FakeImportRunner.Succeeding().Discovering(Discovery(1, 2, 0));

    await Command(runner, FakeBackgroundImportSpawner.Running(), FakeHandoffAgentLauncher.Ran(), Config.Directory).RunImportStepAsync(Inputs(prompt: () => false));
    await Command(runner, FakeBackgroundImportSpawner.Running(), FakeHandoffAgentLauncher.Ran(), Config.Directory).RunImportStepAsync(Inputs(skip: true));

    await Assert.That(runner.Calls).IsEqualTo(0);
    await Assert.That(Directory.GetFiles(Config.Directory, "import-handoff-*.json")).IsEmpty();
}

[Test]
public async Task Discovery_fault_prints_the_hint_and_asks_nothing() {
    var runner = FakeImportRunner.Succeeding().DiscoveryFaulting(new IOException("corrupt db"));
    using var console = new ConsoleOutput();

    var result = await Command(runner, FakeBackgroundImportSpawner.Running(), FakeHandoffAgentLauncher.Ran(), Config.Directory)
        .RunImportStepAsync(Inputs(prompt: () => throw new InvalidOperationException("must not prompt")));

    await Assert.That(result.Ran).IsFalse();
    await Assert.That(runner.Calls).IsEqualTo(0);
    await Assert.That(console.Text).Contains("corrupt db").And.Contains("kcap import --all");
}

[Test]
public async Task Browser_answered_import_is_reported_and_nothing_else_runs() {
    var runner = FakeImportRunner.Succeeding();
    var answer = new FirstRunImportAnswer([], FirstRunImportWindows.Everything, FirstRunImportTitles.Server, null, DateTimeOffset.UtcNow, 0);

    var result = await Command(runner, FakeBackgroundImportSpawner.Running(), FakeHandoffAgentLauncher.Ran(), Config.Directory).RunImportStepAsync(Inputs(browser: answer));

    await Assert.That(result.Ran).IsFalse();
    await Assert.That(runner.Calls + runner.DiscoverCalls).IsEqualTo(0);
}

[Test]
public async Task Failed_run_with_nothing_landed_spawns_the_child_but_suppresses_the_handoff_as_import_failed() {
    var runner = FakeImportRunner.Faulting(new InvalidOperationException("boom")).Discovering(Discovery(1, 2, 0));
    var spawner = FakeBackgroundImportSpawner.Running();

    var result = await Command(runner, spawner, FakeHandoffAgentLauncher.Ran(), Config.Directory).RunImportStepAsync(Inputs());

    await Assert.That(spawner.Spawns).IsEqualTo(1);
    await Assert.That(result.Handoff!.Reason).IsEqualTo(HandoffSuppressedReason.ImportFailed);
    await Assert.That(await File.ReadAllTextAsync(Directory.GetFiles(Config.Directory, "import-handoff-*.json").Single())).Contains("\"cohort\": \"unknown\"");
}

[Test]
public async Task Vanished_corpus_is_complete_no_new_sessions_and_spawns_nothing() {
    var runner = FakeImportRunner.Of(_ => new SetupImportRun(0, ImportRunSelection.Empty, new ImportRunOutcome(FakeImportRunner.ZeroCounts, 0, ImportRunPartition.Empty), null)).Discovering(Discovery(1, 2, 0));
    var spawner = FakeBackgroundImportSpawner.Running();

    var result = await Command(runner, spawner, FakeHandoffAgentLauncher.Ran(), Config.Directory).RunImportStepAsync(Inputs());

    await Assert.That(spawner.Spawns).IsEqualTo(0);
    await Assert.That(result.Handoff!.Reason).IsEqualTo(HandoffSuppressedReason.NoNewSessions);
}

[Test]
public async Task Launch_failure_falls_back_to_the_paste_block_with_the_run_id() {
    var runner = FakeImportRunner.Succeeding().Discovering(Discovery(1, 2, 0));
    var launcher = FakeHandoffAgentLauncher.Failing();

    var result = await Command(runner, FakeBackgroundImportSpawner.Running(), launcher, Config.Directory, pick: labels => labels.First()).RunImportStepAsync(Inputs());

    await Assert.That(launcher.Launches).IsEqualTo(1);
    await Assert.That(result.PasteBlock).StartsWith(SetupCommand.EvalWatchPrompt + "\n(run: ");
    await Assert.That(result.PasteBlock).Contains(result.RunId!);
}

[Test]
public async Task Pinned_prompts() {
    await Assert.That(SetupCommand.ImportPrompt).IsEqualTo("Import past sessions from this machine?");
    await Assert.That(SetupCommand.EvalWatchPrompt).IsEqualTo("Follow my kcap import");
    await Assert.That(SetupCommand.HandoffPromptText("abc")).IsEqualTo("Follow my kcap import\n(run: abc)");
}

[Test]
public async Task NextStepItems_puts_the_handoff_item_above_the_tour_and_omits_it_when_null() {
    var with = SetupCommand.NextStepItems(offerGuidedTour: true, handoffPaste: "Follow my kcap import\n(run: x)");
    var without = SetupCommand.NextStepItems(offerGuidedTour: true, handoffPaste: null);

    await Assert.That(with.Count).IsEqualTo(3);
    await Assert.That(with[1].Answer).Contains("Follow my kcap import");
    await Assert.That(with[2].Question).IsEqualTo(SetupCommand.GuidedTourQuestion);
    await Assert.That(without.Count).IsEqualTo(2);
}
```

The picker needs a seam so tests never open a Spectre prompt: give `SetupCommand` an `internal Func<IReadOnlyList<string>, string?>? PickHandoffVendor` property (null → the real `SelectionPrompt`; tests set it through a `pick:` argument on the `Command(...)` helper). The default test pick returns `"Skip"`.

- [ ] **Step 2: Run to verify they fail** — filter `SetupCommandTests`.

- [ ] **Step 3: Implement** — the behaviour list above, as one `RunImportStepAsync(ImportStepInputs)` plus small private helpers: `PrintDiscovery(ImportDiscoveryResult)`, `PrintBackground(BackgroundImportLaunch)`, `PrintSuppressed(HandoffSuppressedReason)`, `OfferHandoff(...)`. Pinned background lines:

```csharp
BackgroundImportStatus.Running    => $"  Importing the remaining sessions in the background · log: {log}",
BackgroundImportStatus.ExitedZero => $"  Background import exited immediately (exit 0) — details in {log}",
BackgroundImportStatus.Failed     => $"  [yellow]![/] Background import did not start{(code is { } c ? $" (exit {c})" : "")}: {error}. Run [cyan]kcap import --all --yes[/] to import the rest.",
```

Step 6's caller (~:973-1004) builds `ImportStepInputs` from `authSatisfied`, `skipImport`, `noPrompt`, `saved`, `activeName`, `serverUrl`, `defaultVisibility`, `currentRepo`, `workdir.Path`, `stepPaths`, `browserAnswers.Import`, `browserAnswers.ImportFailed`; the `RepositoryDetection` call stays (it feeds `CurrentRepo` as the evidence hint) but no longer gates anything. Pass `result.PasteBlock` to `WriteNextSteps(ShouldOfferGuidedTour(...), result.PasteBlock)`. Delete the `Optional: import past sessions with kcap import --org` line — the step now covers the machine.

- [ ] **Step 4: Run to verify they pass** — filters `SetupCommandTests`, `SetupChosenServerTests`, `SetupImportLaneTests`, `SetupDecisionsTests`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/SetupCommand.cs test/Capacitor.Cli.Tests.Unit/Commands/SetupCommandTests.cs test/Capacitor.Cli.Tests.Unit/Commands/SetupChosenServerTests.cs
git commit -m "[AI-2169] Import the whole machine from setup and hand off to an agent"
```

---

### Task 13: The eval-watch skill and its registration

**Files:**
- Create: `kcap/skills/eval-watch/SKILL.md`
- Modify: `src/Capacitor.Cli.Core/AgentsSkillsInstaller.cs` (`SourceNames`, ~:29-40), `src/Capacitor.Cli.Core/Resources/help-plugin.txt` (~:157-158 skill list)
- Test: additions to `test/Capacitor.Cli.Tests.Unit/Commands/SetupCommandTests.cs` (frontmatter pin, mirroring the guided-tour pin at ~:610-620) and `test/Capacitor.Cli.Tests.Unit/Commands/PluginCommandSkillsTests.cs` (already iterates `SourceNames`; add an assertion that `eval-watch` is in it)

**Interfaces:**
- Consumes: the handoff file (§3), the `kcap-analytics` MCP (`query_analytics` with `sql`, `scope`, `max_rows`), `kcap whoami`, `SetupCommand.EvalWatchPrompt`.
- Produces: a skill whose frontmatter `description` contains `Follow my kcap import` verbatim.

- [ ] **Step 1: Write the failing tests**

In `SetupCommandTests.cs`, beside the guided-tour frontmatter test:

```csharp
[Test]
public async Task Eval_watch_skill_frontmatter_carries_the_pinned_handoff_prompt() {
    var skill = Path.Combine(RepoTree.SkillsSource(), SetupCommand.EvalWatchSkillName, "SKILL.md");
    var description = FrontmatterDescription(await File.ReadAllTextAsync(skill));

    await Assert.That(description).Contains(SetupCommand.EvalWatchPrompt);
}
```

In `PluginCommandSkillsTests.cs`:

```csharp
[Test]
public async Task Eval_watch_is_an_owned_skill() =>
    await Assert.That(AgentsSkillsInstaller.SourceNames).Contains("eval-watch");
```

- [ ] **Step 2: Run to verify they fail** — filters `SetupCommandTests`, `PluginCommandSkillsTests`; the help-plugin pin test (find it with `grep -rn 'help-plugin' test/`) fails once `SourceNames` changes until the text is updated.

- [ ] **Step 3: Write the skill and register it**

Add `"eval-watch"` to `SourceNames` after `"suggest-review-flow"`, and to the `help-plugin.txt` list. Write `kcap/skills/eval-watch/SKILL.md` with this frontmatter and these sections — the body is the spec's §5 turned into instructions the agent follows literally:

```markdown
---
name: eval-watch
description: >-
  Follow a kcap import that setup just started and the evals landing on it —
  "Follow my kcap import" (the prompt `kcap setup` hands to your agent), "watch my
  import", "how is my import going", "are my evals done yet". Reads the handoff file
  setup wrote, polls Capacitor analytics for exactly the sessions that import
  created, summarizes the first three completed evals, links to the results and
  offers the guided tour. Not for browsing evals in general — that is the analytics
  or guided-tour skill.
---

# Eval watch

You follow ONE import: the sessions listed in a handoff file `kcap setup` wrote. You never query
anything that is not in that list.

## 1. Find the run

- If the prompt carries a line `(run: <id>)`, `<id>` must match `^[0-9a-f]{32}$`. If it does not,
  treat the prompt as carrying no run id. If it does, the run is bound: read ONLY
  `<config>/import-handoff-<id>.json`, where `<config>` is `$KCAP_CONFIG_DIR` when set, else
  `~/.config/kcap`. Missing or unreadable → say so, and CLOSE (section 7) with no query. Never
  open another run's file.
- Otherwise list `<config>/import-handoff-*.json`, keep the locator-valid ones (section 2) that are
  watchable (`handoff_offered` true, or `handoff_suppressed` is `skill_not_installed` or
  `no_agent_detected`), pick the newest `written_at`, and say which others you passed over.
- Nothing qualifies → say you found no import to follow and CLOSE with no query.

## 2. Validate the file — three layers

Layer A (may this file be selected): JSON parses; `schema_version` is 1; `run_id` matches
`^[0-9a-f]{32}$`; `written_at` parses as ISO-8601 and is within the last 24 hours;
`handoff_offered` is a boolean. Fail → not selectable.

Layer B (may its ids drive data): `cohort` ∈ {exact, partial_exact, unknown}; `background` ∈
{not_needed, running, exited_zero, failed}; `foreground_certainty` ∈ {complete, incomplete};
`handoff_suppressed` is null or one of import_failed, no_new_sessions, nothing_landed,
analytics_not_in_plan, skill_not_installed, no_agent_detected; every entry of `session_ids` and
`foreground_succeeded_ids` matches `^[A-Za-z0-9_-]{1,128}$`; `unattributed_on_disk` is a
non-negative integer. Fail → NO data: say the record is unreadable and CLOSE with links only.

Layer C (may it supply links): `server_url` starts with `http://` or `https://`. Fail → no links
and no query. `background_log` and `profile` are shown as plain text only when they contain no
control characters and are under 512 characters; otherwise omit them.

## 3. No-handoff files

If `handoff_offered` is false, branch on `handoff_suppressed` and CLOSE:
- `no_new_sessions` → "nothing to watch — that import found no new sessions".
- `nothing_landed` → "the sessions that import selected were skipped at import time"; point at
  `kcap import --all` for per-session reasons; no retry.
- `analytics_not_in_plan` → the plan sentence (section 6); the import ran; no retry.
- `import_failed` → "that import did not get running"; name `kcap import --all --yes` and the
  `background_log` when displayable.
- `skill_not_installed` / `no_agent_detected` → the import ran and you evidently have the skill
  now: continue as if offered.

## 4. Bind to the server — fail closed

Run `kcap whoami` and compare its server URL to `server_url`: lowercase scheme and host, drop a
default port (80 for http, 443 for https only), trim a trailing slash, compare the path as-is. Match
→ continue. Mismatch or `whoami` failure → issue NO analytics query. CLOSE with the file's links and
this remediation: start your agent from a shell where `kcap whoami` reports `<server_url>` — set
`KCAP_PROFILE=<profile>`, unset `KCAP_URL`, and set `KCAP_CONFIG_DIR` if kcap uses a custom config
directory — then prompt again.

## 5. Watch the cohort

Cohort = `session_ids` (`cohort: unknown` or empty → CLOSE with links, no query). Open with:
"Watching N sessions from this import (K landed before the handoff)" — K is
`foreground_succeeded_ids.length`; for `partial_exact` add "the 500 most recent; older ones may
land and evaluate unobserved"; if `unattributed_on_disk` > 0 add the sentence from section 7.

Every query uses `query_analytics` with `scope: 'global'`, ONE call at a time (never two in flight),
and names only cohort ids as single-quoted literals in an `IN (...)` list or `session_id = '...'`.

Request budget: at most 20 cohort queries per poll. `floor = ceil(N / 20)`. The FIRST poll uses
batch size `floor` exactly. Every batch passes `max_rows` equal to its size:

    SELECT s.session_id, s.repo_hash, e.eval_run_id, e.evaluated_at, e.overall_score, e.judge_model
    FROM v_an_sessions s LEFT JOIN v_an_eval_summaries e ON e.session_id = s.session_id
    WHERE s.session_id IN ('<id>', '<id>', ...)

A row = arrived; a non-null `eval_run_id` = completed. Each response body carries `max_rows`, the
server's cap. After the first poll: if any body reported `max_rows < floor`, or any batch came back
truncated, FAIL CLOSED — say the server's row cap is below what watching this many sessions needs,
and CLOSE. Otherwise raise the batch to `min(100, max_rows)` for later polls.

One poll = every batch, each non-truncated. A failed or truncated batch discards the whole poll and
counts one failure. A 429 fails the poll; if its text contains `retry after Ns`, wait N seconds
before the next poll, else 60; never less than the 30-second cadence. HTTP 403 with
`analytics_not_in_plan` → CLOSE immediately with the plan sentence, no polling.

Progress = cohort ids that arrived / cohort size, from the batch rows. Cadence: first snapshot
immediately, then every 30 seconds.

Stop, evaluated after each successful poll in this order:
1. Three distinct cohort sessions have a completed eval → summarize the first three ordered by
   `evaluated_at` then `session_id`.
2. `cohort: exact`, non-empty, and every id has a completed eval → summarize what completed.
3. Two consecutive failed polls → stop.
4. 10 minutes since the first snapshot → stop; a poll already in flight finishes and commits first.

Per-session detail (one session per query, at most 6 queries per run):

    SELECT category, AVG(score) AS mean FROM v_an_eval_scores WHERE session_id = '<id>' GROUP BY category
    SELECT question_id, score FROM v_an_eval_scores WHERE session_id = '<id>' ORDER BY score ASC, question_id ASC LIMIT 2

Strongest = highest mean, weakest = lowest, ties alphabetical. A truncated or failed detail query →
summarize that session by `overall_score` alone and say so.

## 6. When little or nothing completes

Explain the real gates plainly: auto-eval may be off for a repository; the server needs an eval
agent configured; sessions under the minimum event count are skipped; already-evaluated sessions are
not re-run; a recovery sweep runs hourly. Plan sentence: "Insights isn't in this workspace's plan,
so evals can't be followed from here; your import continues and evals appear in the Capacitor UI."

## 7. CLOSE — always

1. How to keep watching: prompt `Follow my kcap import` again (with the same `(run: <id>)` line).
2. Links: with a repo hash `<server_url>/repo/<repo_hash>/sessions/<session_id>?tab=evaluation`;
   without one `<server_url>/sessions/<session_id>?tab=evaluation`; all results
   `<server_url>/sessions`. No valid file → `server_url` from `kcap whoami`; if that fails too,
   say the results live in the Capacitor server UI and that `kcap whoami` prints the URL once
   logged in.
3. When `unattributed_on_disk` > 0: "N sessions on disk had no repository match; any of them that
   imported show without one — `kcap remap` places them."
4. Offer the guided tour with the exact prompt `Start kcap guided tour`.
```

- [ ] **Step 4: Run to verify they pass** — filters `SetupCommandTests`, `PluginCommandSkillsTests`, and the help-plugin pin test class.

- [ ] **Step 5: Commit**

```bash
git add kcap/skills/eval-watch/SKILL.md src/Capacitor.Cli.Core/AgentsSkillsInstaller.cs src/Capacitor.Cli.Core/Resources/help-plugin.txt test/Capacitor.Cli.Tests.Unit/Commands/SetupCommandTests.cs test/Capacitor.Cli.Tests.Unit/Commands/PluginCommandSkillsTests.cs
git commit -m "[AI-2169] Ship the eval-watch skill setup hands to the agent"
```

**Acceptance of the skill itself** is not unit-testable: run the spec's §5 scenarios by hand against a
fixture tenant after the CLI is built, driving the agent with recorded analytics responses where the
live tenant cannot produce the shape (a 429 with `retry after`, a row cap of 10, an
`analytics_not_in_plan` 403). Record the outcomes in the PR description.

---

### Task 14: README

**Files:**
- Modify: `README.md` — step 6 (~:143), the `--no-prompt` callout (~:192), the "Import existing sessions" intro (~:203-231)

- [ ] **Step 1: Rewrite step 6**

Replace the step-6 bullet with: setup scans every detected agent's history on this machine and prints how many repositories and sessions it found and how many sessions matched no repository; it then asks `Import past sessions from this machine?` (default yes), equivalent to `kcap import --all` under your profile's default visibility (shared only where the repository owner is your workspace's org); the most recent sessions are prioritized, about five import while you watch and the rest continue in a detached background process that logs to `~/.config/kcap/import-<run>.log`; setup then offers to open one of your detected coding agents with the prompt `Follow my kcap import` so you can watch the import and its evals from there (or prints the prompt to paste), and finishes when you close it. Keep the sentence that a browser-answered Import screen makes this step report rather than prompt. Note `--skip-import`.

- [ ] **Step 2: Rewrite the `--no-prompt` callout**

> **Behavior change: `--no-prompt` imports this machine's history.** The step 6 import defaults to yes like every other prompt, so `kcap setup --no-prompt` now uploads every session on this machine (not only the current repository's), synchronously and without the agent handoff, when authentication requirements are satisfied. Add `--skip-import` to opt out.

- [ ] **Step 3: One sentence in "Import existing sessions"**

After the command block: "Sessions are imported most-recent-first, so your latest work appears in the dashboard earliest."

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "[AI-2169] Document the machine-wide setup import and the agent handoff"
```

---

### Task 15: Publish check, comment sweep, final verification

**Files:** every file touched above.

- [ ] **Step 1: AOT publish is clean**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output. If `ToFrozenDictionary` or `JsonObject` use trips a warning, replace with a plain `Dictionary` and the `(JsonNode?)` cast respectively.

- [ ] **Step 2: Targeted test sweep**

Run each of these filters once and paste the summary lines into the PR description: `ImportOrderingTests`, `ImportChainTests`, `ImportChainsTests`, `RoutedUnitTests`, `ForegroundSelectionTests`, `ImportSelectionReportingTests`, `ImportVisibilityTests`, `ImportSkipTitleTests`, `DetachedImportLogTests`, `SetupImportRunnerTests`, `ForegroundImportOutcomeTests`, `BackgroundImportSpawnerTests`, `ImportHandoffFileTests`, `HandoffDecisionTests`, `HandoffLaunchRecipeTests`, `HandoffVendorEligibilityTests`, `HandoffAgentLauncherTests`, `SetupCommandTests`, `SetupDecisionsTests`, `SetupChosenServerTests`, `SetupImportLaneTests`, `PluginCommandSkillsTests`; and in the Core project `OwnerOnlyFileTests`. Expected: all PASS.

- [ ] **Step 3: Comment sweep**

`git diff main...HEAD -- '*.cs' '*.md' '*.txt' | grep -nE '^\+.*(AI-[0-9]+|round [0-9]|previously|no longer|used to|§|Task [0-9]|reviewer)'` prints nothing in source or the skill; the plan and spec are exempt.

- [ ] **Step 4: Manual smoke on this machine**

From a scratch directory with a throwaway profile: `kcap setup --server-url <tenant>` → accept the prompt → confirm the figures, the five foreground sessions with titles, the background line and log file (owner-only: `ls -l ~/.config/kcap/import-*.log`), the handoff file, the picker listing every detected vendor with the skill, and a launched agent that follows the import. Then `kcap import --all --yes --skip-title` by hand to confirm the child's argv is accepted.

- [ ] **Step 5: Final commit if the sweep changed anything**

```bash
git add -A src test kcap README.md
git commit -m "[AI-2169] Tidy comments and pins after the setup import rework"
```

---

## Self-review

**Spec coverage.** §1 ordering + candidate comparator → Task 1. §2 entry gates → Task 11; discovery-first and its fault → Tasks 5, 12; prompt and `--no-prompt` → Task 12; invocation and runner result → Task 5; selection, units, carried children, candidates, partition (incl. `SentChildContent`), `onSelected`, `ReportNothing` → Tasks 2, 3; totalized outcome → Task 6; background child, env pins, visibility parity, child-side contract, statuses → Tasks 4, 7; rendering lines → Task 12. §3 file shape, `handoff_suppressed`, `profile`, `unattributed_on_disk`, owner-only no-clobber publication, pruning → Task 8 (+ Task 7's `OwnerOnlyFile`). §4 decision table → Task 9; plan gate, eligibility, recipes, launch semantics, env pin, pinned prompt, Next-steps item → Tasks 10, 12. §5 skill → Task 13. README → Task 14. Publish/AOT → Task 15. Out-of-scope items are not planned, by design.

**Placeholder scan.** Task 12's `Discovery(...)` test helper says "build via `ImportDiscoverySummary.Build`" — that is a pointer to an existing, tested factory, not a placeholder; the implementer copies the two-line construction from `ImportDiscoverySummaryTests`. Task 13's acceptance is explicitly manual and says so.

**Type consistency.** `ImportRunSelection(RunCandidateIds, SelectedIds, RemainderExists)` and `ImportRunPartition(SucceededIds, SkippedIds, FailedIds)` are used identically in Tasks 2, 3, 5, 6, 8, 12. `SetupImportRun(ExitCode, Selection, Outcome, Fault)` in Tasks 5, 6, 12. `BackgroundImportLaunch(Status, LogPath, ExitCode, Error)` in Tasks 7, 8, 12. `HandoffVendor(Id, Label, Executable)` and `HandoffLaunchRequest(Vendor, Prompt, ProfileName, WorkingDirectory)` in Tasks 10, 12. `ForegroundImportOutcome.SucceededIds` feeds `ImportHandoffFile.ForegroundSucceededIds` (Task 8) as the spec requires.
