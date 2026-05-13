# [AI-613] History Import Scope Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the default-import behaviour of `kapacitor history` with an explicit scope choice (flag or picker) and a mandatory confirmation prompt, so users can't accidentally upload sessions from personal/private repos.

**Architecture:** Add a small `ImportScope` record hierarchy and three pure helpers — flag parser, scope filter, picker/confirmation formatter — then wire two new pipeline steps into `HandleHistory` (resolve scope → confirm) plus a post-import visibility loop for `--private`. The pure helpers carry the logic; Spectre.Console code is a thin glue layer.

**Tech Stack:** .NET 10 (NativeAOT), Spectre.Console for prompts, TUnit on Microsoft Testing Platform for tests, WireMock.Net for the visibility-PUT integration test.

**Spec:** `docs/superpowers/specs/2026-05-13-ai-613-history-import-scope-design.md`

---

## File Map

**Create:**
- `src/kapacitor/Commands/ImportScope.cs` — record hierarchy (`All` / `Org(orgLogin)` / `Repo(owner, name)`).
- `src/kapacitor/Commands/ImportScopeArgs.cs` — pure flag parser + resolver returning either a resolved `ImportScope` or an error message.
- `src/kapacitor/Commands/HistoryScopeFilter.cs` — pure async filter `Apply(transcripts, scope, resolveRepo)` returning the kept subset.
- `src/kapacitor/Commands/HistoryScopePrompt.cs` — Spectre pickers + confirmation prompt. Logic lives in pure helpers (`BuildRepoChoices`, `FormatSummary`); the Spectre layer is thin.
- `test/kapacitor.Tests.Unit/ImportScopeArgsTests.cs` — parser + resolver TDD.
- `test/kapacitor.Tests.Unit/HistoryScopeFilterTests.cs` — filter TDD.
- `test/kapacitor.Tests.Unit/HistoryScopePromptTests.cs` — formatter + repo-choices TDD.
- `test/kapacitor.Tests.Integration/HistoryPrivateImportTests.cs` — WireMock for `--private` post-import flow.

**Modify:**
- `src/kapacitor/Commands/HistoryCommand.cs` — extend `HandleHistory(...)` with `(ImportScope scope, bool yes, bool forcePrivate)`; insert scope-filter step after pre-filters; insert confirmation step; collect imported session ids; add post-import visibility loop.
- `src/kapacitor/Program.cs` — wire `ImportScopeArgs.ParseFlags` + `Resolve` into the `case "history"` block; pass results into `HandleHistory`.
- `src/kapacitor/Commands/SetupCommand.cs` — append a one-line tip after the final summary.
- `src/Kapacitor.Core/Resources/help-history.txt` — document the new flags.

---

## Task 1: ImportScope record hierarchy

**Files:**
- Create: `src/kapacitor/Commands/ImportScope.cs`

- [ ] **Step 1: Create the file**

```csharp
namespace kapacitor.Commands;

/// <summary>
/// Selected history import scope, resolved from CLI flags or the interactive picker.
/// </summary>
public abstract record ImportScope {
    public sealed record All  : ImportScope;
    public sealed record Org  (string OrgLogin) : ImportScope;
    public sealed record Repo (string Owner, string Name) : ImportScope;

    private ImportScope() { }
}
```

The private constructor + sealed record subtypes make the hierarchy closed — exhaustive `switch` over `ImportScope` won't need a default arm.

- [ ] **Step 2: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: build succeeds with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/kapacitor/Commands/ImportScope.cs
git commit -m "[AI-613] add ImportScope record hierarchy"
```

---

## Task 2: Flag parser + scope resolver (pure)

**Files:**
- Create: `src/kapacitor/Commands/ImportScopeArgs.cs`
- Create: `test/kapacitor.Tests.Unit/ImportScopeArgsTests.cs`

- [ ] **Step 1: Write the failing tests**

`test/kapacitor.Tests.Unit/ImportScopeArgsTests.cs`:

```csharp
using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class ImportScopeArgsTests {
    [Test]
    public async Task ParseFlags_reads_all() {
        var f = ImportScopeArgs.ParseFlags(["history", "--all"]);
        await Assert.That(f.All).IsTrue();
        await Assert.That(f.Org).IsFalse();
        await Assert.That(f.RepoArg).IsNull();
        await Assert.That(f.Yes).IsFalse();
        await Assert.That(f.Private).IsFalse();
    }

    [Test]
    public async Task ParseFlags_reads_repo_value() {
        var f = ImportScopeArgs.ParseFlags(["history", "--repo", "EventStore/kapacitor"]);
        await Assert.That(f.RepoArg).IsEqualTo("EventStore/kapacitor");
    }

    [Test]
    public async Task ParseFlags_reads_yes_short_form() {
        var f = ImportScopeArgs.ParseFlags(["history", "--all", "-y"]);
        await Assert.That(f.Yes).IsTrue();
    }

    [Test]
    public async Task ParseFlags_reads_private() {
        var f = ImportScopeArgs.ParseFlags(["history", "--all", "--private"]);
        await Assert.That(f.Private).IsTrue();
    }

    [Test]
    public async Task Resolve_errors_when_two_scope_flags_set() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: true, Org: true, RepoArg: null, Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error).IsNotNull();
        await Assert.That(r.Error!).Contains("mutually exclusive");
    }

    [Test]
    public async Task Resolve_returns_All_for_all_flag() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: true, Org: false, RepoArg: null, Yes: false, Private: false),
            ActiveProfile: "default",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsTypeOf<ImportScope.All>();
        await Assert.That(r.Error).IsNull();
    }

    [Test]
    public async Task Resolve_returns_Org_with_active_profile() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: true, RepoArg: null, Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsTypeOf<ImportScope.Org>();
        await Assert.That(((ImportScope.Org)r.Scope!).OrgLogin).IsEqualTo("EventStore");
    }

    [Test]
    public async Task Resolve_errors_on_Org_when_profile_is_default() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: true, RepoArg: null, Yes: false, Private: false),
            ActiveProfile: "default",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error!).Contains("tenant-bound profile");
    }

    [Test]
    public async Task Resolve_returns_Repo_for_owner_slash_name() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArg: "EventStore/kapacitor", Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        var repo = (ImportScope.Repo)r.Scope!;
        await Assert.That(repo.Owner).IsEqualTo("EventStore");
        await Assert.That(repo.Name).IsEqualTo("kapacitor");
    }

    [Test]
    public async Task Resolve_repo_dot_uses_current_repo() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArg: ".", Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: ("EventStore", "kapacitor"));

        var r = ImportScopeArgs.Resolve(input);

        var repo = (ImportScope.Repo)r.Scope!;
        await Assert.That(repo.Owner).IsEqualTo("EventStore");
        await Assert.That(repo.Name).IsEqualTo("kapacitor");
    }

    [Test]
    public async Task Resolve_repo_current_alias_uses_current_repo() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArg: "current", Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: ("EventStore", "kapacitor"));

        var r = ImportScopeArgs.Resolve(input);

        var repo = (ImportScope.Repo)r.Scope!;
        await Assert.That(repo.Owner).IsEqualTo("EventStore");
        await Assert.That(repo.Name).IsEqualTo("kapacitor");
    }

    [Test]
    public async Task Resolve_repo_dot_errors_when_cwd_has_no_repo() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArg: ".", Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error!).Contains("not a git repo");
    }

    [Test]
    public async Task Resolve_errors_on_malformed_repo_arg() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArg: "no-slash", Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error!).Contains("owner/name");
    }

    [Test]
    public async Task Resolve_returns_NeedsPicker_when_no_flag_and_interactive() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArg: null, Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error).IsNull();
        await Assert.That(r.NeedsPicker).IsTrue();
    }

    [Test]
    public async Task Resolve_errors_when_no_flag_and_non_interactive() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArg: null, Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: false,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.NeedsPicker).IsFalse();
        await Assert.That(r.Error!).Contains("--all, --org, or --repo");
    }

    [Test]
    public async Task Resolve_errors_when_flag_set_and_non_interactive_without_yes() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: true, Org: false, RepoArg: null, Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: false,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error!).Contains("--yes");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/ImportScopeArgsTests/*"
```

Expected: every test FAILS — `ImportScopeArgs` does not exist yet.

- [ ] **Step 3: Implement `ImportScopeArgs`**

`src/kapacitor/Commands/ImportScopeArgs.cs`:

```csharp
namespace kapacitor.Commands;

/// <summary>
/// Pure flag parser and resolver for `kapacitor history` scope selection.
/// Performs no I/O — current-repo lookup is the caller's job; the resolved
/// result is passed in via <see cref="ResolveInput.CurrentRepo"/>.
/// </summary>
public static class ImportScopeArgs {
    public sealed record ParsedFlags(
        bool    All,
        bool    Org,
        string? RepoArg,
        bool    Yes,
        bool    Private);

    public sealed record ResolveInput(
        ParsedFlags                         Flags,
        string                              ActiveProfile,
        bool                                IsInteractive,
        (string Owner, string Name)?        CurrentRepo);

    public sealed record ResolveResult(
        ImportScope? Scope,        // null => either picker needed or error
        bool         Yes,
        bool         Private,
        bool         NeedsPicker,
        string?      Error);

    public static ParsedFlags ParseFlags(string[] args) {
        string? repo = null;
        var idx = Array.IndexOf(args, "--repo");
        if (idx >= 0 && idx + 1 < args.Length) repo = args[idx + 1];

        return new(
            All:     args.Contains("--all"),
            Org:     args.Contains("--org"),
            RepoArg: repo,
            Yes:     args.Contains("--yes") || args.Contains("-y"),
            Private: args.Contains("--private"));
    }

    public static ResolveResult Resolve(ResolveInput input) {
        var f = input.Flags;
        var count = (f.All ? 1 : 0) + (f.Org ? 1 : 0) + (f.RepoArg is null ? 0 : 1);

        if (count > 1) {
            return new(null, f.Yes, f.Private, false,
                "--all, --org, and --repo are mutually exclusive.");
        }

        if (count == 0) {
            if (input.IsInteractive) {
                return new(null, f.Yes, f.Private, NeedsPicker: true, Error: null);
            }
            return new(null, f.Yes, f.Private, false,
                "--all, --org, or --repo <owner/name> is required for non-interactive use.");
        }

        // A scope flag is set: enforce --yes for non-interactive runs.
        if (!input.IsInteractive && !f.Yes) {
            return new(null, f.Yes, f.Private, false,
                "--yes is required for non-interactive use.");
        }

        if (f.All) {
            return new(new ImportScope.All(), f.Yes, f.Private, false, null);
        }

        if (f.Org) {
            if (string.IsNullOrEmpty(input.ActiveProfile) || input.ActiveProfile == "default") {
                return new(null, f.Yes, f.Private, false,
                    "--org requires a tenant-bound profile. Run `kapacitor setup` first, or use --all / --repo <owner/name>.");
            }
            return new(new ImportScope.Org(input.ActiveProfile), f.Yes, f.Private, false, null);
        }

        // --repo <value>
        var value = f.RepoArg!;
        if (value is "." or "current") {
            if (input.CurrentRepo is null) {
                return new(null, f.Yes, f.Private, false,
                    "--repo . requires the current directory to be a git repo with an origin remote.");
            }
            var (owner, name) = input.CurrentRepo.Value;
            return new(new ImportScope.Repo(owner, name), f.Yes, f.Private, false, null);
        }

        var parts = value.Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) {
            return new(null, f.Yes, f.Private, false,
                $"--repo expects owner/name (got '{value}').");
        }

        return new(new ImportScope.Repo(parts[0], parts[1]), f.Yes, f.Private, false, null);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/ImportScopeArgsTests/*"
```

Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/ImportScopeArgs.cs test/kapacitor.Tests.Unit/ImportScopeArgsTests.cs
git commit -m "[AI-613] add ImportScopeArgs parser + resolver"
```

---

## Task 3: Pure scope filter

**Files:**
- Create: `src/kapacitor/Commands/HistoryScopeFilter.cs`
- Create: `test/kapacitor.Tests.Unit/HistoryScopeFilterTests.cs`

- [ ] **Step 1: Write the failing tests**

`test/kapacitor.Tests.Unit/HistoryScopeFilterTests.cs`:

```csharp
using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class HistoryScopeFilterTests {
    static (string SessionId, string FilePath, string EncodedCwd) T(string id) =>
        (id, $"/tmp/{id}.jsonl", $"-tmp-proj-{id}");

    static Func<(string SessionId, string FilePath, string EncodedCwd), CancellationToken, ValueTask<(string? Owner, string? Name)>>
        Resolver(Dictionary<string, (string Owner, string Name)?> map) =>
        (t, _) => new ValueTask<(string?, string?)>(
            map.TryGetValue(t.SessionId, out var v) && v is { } x ? (x.Owner, x.Name) : (null, null));

    [Test]
    public async Task Apply_All_returns_every_transcript_including_unresolved() {
        var transcripts = new[] { T("a"), T("b"), T("c") };
        var resolver = Resolver(new() { ["a"] = ("EventStore", "kapacitor"), ["b"] = null });

        var kept = await HistoryScopeFilter.Apply(transcripts, new ImportScope.All(), resolver);

        await Assert.That(kept.Select(x => x.SessionId).ToArray()).IsEquivalentTo(new[] { "a", "b", "c" });
    }

    [Test]
    public async Task Apply_Org_keeps_only_matching_owner() {
        var transcripts = new[] { T("a"), T("b"), T("c") };
        var resolver = Resolver(new() {
            ["a"] = ("EventStore", "kapacitor"),
            ["b"] = ("kurrent-io", "secret"),
            ["c"] = ("EventStore", "kurrentdb"),
        });

        var kept = await HistoryScopeFilter.Apply(transcripts, new ImportScope.Org("EventStore"), resolver);

        await Assert.That(kept.Select(x => x.SessionId).ToArray()).IsEquivalentTo(new[] { "a", "c" });
    }

    [Test]
    public async Task Apply_Org_matches_case_insensitively() {
        var transcripts = new[] { T("a") };
        var resolver = Resolver(new() { ["a"] = ("eventstore", "kapacitor") });

        var kept = await HistoryScopeFilter.Apply(transcripts, new ImportScope.Org("EventStore"), resolver);

        await Assert.That(kept).HasCount(1);
    }

    [Test]
    public async Task Apply_Org_drops_unresolved_repos() {
        var transcripts = new[] { T("a") };
        var resolver = Resolver(new() { ["a"] = null });

        var kept = await HistoryScopeFilter.Apply(transcripts, new ImportScope.Org("EventStore"), resolver);

        await Assert.That(kept).IsEmpty();
    }

    [Test]
    public async Task Apply_Repo_keeps_only_exact_match() {
        var transcripts = new[] { T("a"), T("b"), T("c") };
        var resolver = Resolver(new() {
            ["a"] = ("EventStore", "kapacitor"),
            ["b"] = ("EventStore", "kurrentdb"),
            ["c"] = ("EventStore", "kapacitor"),
        });

        var kept = await HistoryScopeFilter.Apply(
            transcripts, new ImportScope.Repo("EventStore", "kapacitor"), resolver);

        await Assert.That(kept.Select(x => x.SessionId).ToArray()).IsEquivalentTo(new[] { "a", "c" });
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryScopeFilterTests/*"
```

Expected: tests FAIL — `HistoryScopeFilter` does not exist.

- [ ] **Step 3: Implement the filter**

`src/kapacitor/Commands/HistoryScopeFilter.cs`:

```csharp
namespace kapacitor.Commands;

/// <summary>
/// Pure scope filter for the history pipeline. The repo resolver is injected
/// so unit tests can stub out repository detection; production wires it to
/// the cwd-extractor + RepositoryDetection.DetectRepositoryAsync.
/// </summary>
public static class HistoryScopeFilter {
    public static async Task<List<(string SessionId, string FilePath, string EncodedCwd)>> Apply(
        IReadOnlyList<(string SessionId, string FilePath, string EncodedCwd)>                       transcripts,
        ImportScope                                                                                  scope,
        Func<(string SessionId, string FilePath, string EncodedCwd), CancellationToken,
             ValueTask<(string? Owner, string? Name)>>                                               resolveRepo,
        CancellationToken                                                                            ct = default) {
        if (scope is ImportScope.All) return [..transcripts];

        var kept = new List<(string, string, string)>(transcripts.Count);

        foreach (var t in transcripts) {
            ct.ThrowIfCancellationRequested();
            var (owner, name) = await resolveRepo(t, ct);

            var match = (scope, owner, name) switch {
                (ImportScope.Org o,  not null, _) => string.Equals(owner, o.OrgLogin, StringComparison.OrdinalIgnoreCase),
                (ImportScope.Repo r, not null, not null) =>
                    string.Equals(owner, r.Owner, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(name,  r.Name,  StringComparison.OrdinalIgnoreCase),
                _ => false,
            };

            if (match) kept.Add(t);
        }

        return kept;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryScopeFilterTests/*"
```

Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/HistoryScopeFilter.cs test/kapacitor.Tests.Unit/HistoryScopeFilterTests.cs
git commit -m "[AI-613] add HistoryScopeFilter (pure scope filter)"
```

---

## Task 4: Picker helpers + summary formatter (pure)

**Files:**
- Create: `src/kapacitor/Commands/HistoryScopePrompt.cs`
- Create: `test/kapacitor.Tests.Unit/HistoryScopePromptTests.cs`

This task builds **only** the pure helpers — `BuildRepoChoices` and `FormatSummary`. The Spectre.Console wrappers come in the next task on top of these.

- [ ] **Step 1: Write the failing tests**

`test/kapacitor.Tests.Unit/HistoryScopePromptTests.cs`:

```csharp
using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class HistoryScopePromptTests {
    // --- BuildRepoChoices ---

    [Test]
    public async Task BuildRepoChoices_orders_current_first_then_alphabetical() {
        var choices = HistoryScopePrompt.BuildRepoChoices(
            currentRepo: ("EventStore", "kapacitor"),
            discoveredRepos: [
                ("EventStore", "kurrentdb"),
                ("EventStore", "kapacitor"),  // dup with current
                ("alexeyzimarev", "scratchpad"),
            ]);

        await Assert.That(choices).IsEquivalentTo(new[] {
            "EventStore/kapacitor (current)",
            "alexeyzimarev/scratchpad",
            "EventStore/kurrentdb",
        });
    }

    [Test]
    public async Task BuildRepoChoices_no_current_repo_just_sorts_alphabetically() {
        var choices = HistoryScopePrompt.BuildRepoChoices(
            currentRepo: null,
            discoveredRepos: [("Z-org", "z"), ("A-org", "a"), ("M-org", "m")]);

        await Assert.That(choices).IsEquivalentTo(new[] {
            "A-org/a",
            "M-org/m",
            "Z-org/z",
        });
    }

    [Test]
    public async Task BuildRepoChoices_deduplicates_discovered_set() {
        var choices = HistoryScopePrompt.BuildRepoChoices(
            currentRepo: null,
            discoveredRepos: [("A", "x"), ("A", "x"), ("A", "y")]);

        await Assert.That(choices).IsEquivalentTo(new[] { "A/x", "A/y" });
    }

    // --- FormatSummary ---

    [Test]
    public async Task FormatSummary_All_scope() {
        var s = HistoryScopePrompt.FormatSummary(
            scope: new ImportScope.All(),
            matchedCount: 47,
            repoSamples: ["EventStore/kapacitor", "EventStore/kurrentdb"],
            visibilityDescription: "org_public (from profile)");

        await Assert.That(s).Contains("scope:   everything");
        await Assert.That(s).Contains("matched: 47 sessions");
        await Assert.That(s).Contains("visibility: org_public (from profile)");
    }

    [Test]
    public async Task FormatSummary_Org_includes_org_name() {
        var s = HistoryScopePrompt.FormatSummary(
            scope: new ImportScope.Org("EventStore"),
            matchedCount: 5,
            repoSamples: ["EventStore/kapacitor"],
            visibilityDescription: "private (--private)");

        await Assert.That(s).Contains("org repos only (EventStore)");
        await Assert.That(s).Contains("visibility: private (--private)");
    }

    [Test]
    public async Task FormatSummary_Repo_includes_owner_name() {
        var s = HistoryScopePrompt.FormatSummary(
            scope: new ImportScope.Repo("EventStore", "kapacitor"),
            matchedCount: 3,
            repoSamples: ["EventStore/kapacitor"],
            visibilityDescription: "org_public (from profile)");

        await Assert.That(s).Contains("repository EventStore/kapacitor");
    }

    [Test]
    public async Task FormatSummary_caps_repo_samples_at_5() {
        var samples = Enumerable.Range(1, 9).Select(i => $"EventStore/r{i}").ToArray();
        var s = HistoryScopePrompt.FormatSummary(
            scope: new ImportScope.All(),
            matchedCount: 50,
            repoSamples: samples,
            visibilityDescription: "org_public (from profile)");

        await Assert.That(s).Contains("EventStore/r1");
        await Assert.That(s).Contains("EventStore/r5");
        await Assert.That(s).DoesNotContain("EventStore/r6");
        await Assert.That(s).Contains("+4 more");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryScopePromptTests/*"
```

Expected: FAIL — `HistoryScopePrompt` does not exist.

- [ ] **Step 3: Implement the pure helpers**

`src/kapacitor/Commands/HistoryScopePrompt.cs`:

```csharp
using System.Text;

namespace kapacitor.Commands;

/// <summary>
/// Spectre.Console-backed pickers for history import scope, plus the pure
/// helpers that drive their content. Pure helpers are public for unit testing;
/// Spectre wrappers are added in a separate task.
/// </summary>
public static partial class HistoryScopePrompt {
    /// <summary>
    /// Build the option strings for the "specific repository" sub-picker.
    /// The current cwd's repo (if any) is pinned to the top with a "(current)"
    /// marker; the rest are alphabetized and deduplicated against the current.
    /// </summary>
    public static string[] BuildRepoChoices(
        (string Owner, string Name)?              currentRepo,
        IReadOnlyList<(string Owner, string Name)> discoveredRepos) {
        var distinct = discoveredRepos
            .Select(r => $"{r.Owner}/{r.Name}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (currentRepo is { } current) {
            var currentKey = $"{current.Owner}/{current.Name}";
            distinct.RemoveAll(s => s.Equals(currentKey, StringComparison.OrdinalIgnoreCase));
            distinct.Sort(StringComparer.OrdinalIgnoreCase);
            return [$"{currentKey} (current)", .. distinct];
        }

        distinct.Sort(StringComparer.OrdinalIgnoreCase);
        return [.. distinct];
    }

    /// <summary>
    /// Build the confirmation summary block printed before the y/N prompt.
    /// The same text is printed in non-TTY runs (with --yes) so the imported
    /// scope is recorded in CI logs.
    /// </summary>
    public static string FormatSummary(
        ImportScope            scope,
        int                    matchedCount,
        IReadOnlyList<string>  repoSamples,
        string                 visibilityDescription) {
        var scopeLabel = scope switch {
            ImportScope.All      => "everything",
            ImportScope.Org o    => $"org repos only ({o.OrgLogin})",
            ImportScope.Repo r   => $"repository {r.Owner}/{r.Name}",
            _                    => "?"
        };

        const int sampleLimit = 5;
        var samples = repoSamples.Take(sampleLimit).ToArray();
        var more    = repoSamples.Count - samples.Length;
        var repoLine = samples.Length == 0
            ? "(none)"
            : string.Join(", ", samples) + (more > 0 ? $", +{more} more" : "");

        var sb = new StringBuilder();
        sb.AppendLine("About to import:");
        sb.AppendLine($"  scope:   {scopeLabel}");
        sb.AppendLine($"  matched: {matchedCount} session{(matchedCount == 1 ? "" : "s")} across {repoSamples.Count} repo{(repoSamples.Count == 1 ? "" : "s")}");
        sb.AppendLine($"  repos:   {repoLine}");
        sb.Append   ($"  visibility: {visibilityDescription}");
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryScopePromptTests/*"
```

Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/HistoryScopePrompt.cs test/kapacitor.Tests.Unit/HistoryScopePromptTests.cs
git commit -m "[AI-613] add picker/summary pure helpers"
```

---

## Task 5: Spectre wrappers for picker + confirmation

**Files:**
- Modify: `src/kapacitor/Commands/HistoryScopePrompt.cs`

This adds the Spectre.Console glue on top of Task 4's pure helpers. No unit tests — Spectre prompts read from `Console.In` and are exercised by the manual smoke test (Task 9).

- [ ] **Step 1: Add the Spectre wrappers (partial class extension)**

Append to `src/kapacitor/Commands/HistoryScopePrompt.cs`:

```csharp
using Spectre.Console;

namespace kapacitor.Commands;

public static partial class HistoryScopePrompt {
    /// <summary>
    /// Run the top-level scope picker. Returns the resolved scope, or null
    /// when the user picks "specific repository" but the sub-picker has no
    /// options (no current repo + no detected repos).
    /// </summary>
    public static ImportScope? RunPicker(
        string                                     activeProfile,
        (string Owner, string Name)?               currentRepo,
        IReadOnlyList<(string Owner, string Name)> discoveredRepos) {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What would you like to import?")
                .AddChoices("all", "org", "repo")
                .UseConverter(c => c switch {
                    "all"  => "Everything",
                    "org"  => $"Org repos only ({activeProfile})",
                    "repo" => "Specific repository",
                    _      => c,
                }));

        if (choice == "all")  return new ImportScope.All();
        if (choice == "org") {
            if (string.IsNullOrEmpty(activeProfile) || activeProfile == "default") {
                AnsiConsole.MarkupLine("[red]Active profile has no org. Run `kapacitor setup`.[/]");
                return null;
            }
            return new ImportScope.Org(activeProfile);
        }

        var repoChoices = BuildRepoChoices(currentRepo, discoveredRepos);
        if (repoChoices.Length == 0) {
            AnsiConsole.MarkupLine("[red]No repositories detected in discovered sessions.[/]");
            return null;
        }

        var picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Which repository?")
                .PageSize(15)
                .AddChoices(repoChoices));

        // Strip the trailing " (current)" marker before splitting on '/'.
        var clean = picked.EndsWith(" (current)") ? picked[..^" (current)".Length] : picked;
        var parts = clean.Split('/');
        return new ImportScope.Repo(parts[0], parts[1]);
    }

    /// <summary>
    /// Print the summary block to stderr (visible even when stdout is
    /// redirected) and prompt y/N if <paramref name="skip"/> is false.
    /// Returns true to proceed with the import.
    /// </summary>
    public static bool PromptConfirm(
        ImportScope            scope,
        int                    matchedCount,
        IReadOnlyList<string>  repoSamples,
        string                 visibilityDescription,
        bool                   skip) {
        var summary = FormatSummary(scope, matchedCount, repoSamples, visibilityDescription);
        Console.Error.WriteLine(summary);

        if (skip) return true;

        return AnsiConsole.Prompt(
            new ConfirmationPrompt("Continue?") { DefaultValue = false });
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: 0 errors.

- [ ] **Step 3: Re-run the unit tests for the file to confirm nothing broke**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryScopePromptTests/*"
```

Expected: all PASS.

- [ ] **Step 4: Commit**

```bash
git add src/kapacitor/Commands/HistoryScopePrompt.cs
git commit -m "[AI-613] add Spectre picker + confirmation wrappers"
```

---

## Task 6: Integrate scope resolution + filter + confirmation into `HandleHistory`

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`

This wires the new pure helpers into the pipeline. Three insertion points:
- **Before classify**: resolve scope (picker if needed), apply filter.
- **Before classify**: print confirmation; bail if user says no.
- **Track imported session ids** for the post-import visibility loop (Task 7 uses them).

- [ ] **Step 1: Extend the `HandleHistory` signature**

Modify `src/kapacitor/Commands/HistoryCommand.cs` around line 179. Change:

```csharp
public static async Task<int> HandleHistory(
        string     baseUrl,
        string?    filterCwd,
        string?    filterSession    = null,
        int        minLines         = 15,
        bool       generateSummaries = false,
        bool       codex            = false,
        DateOnly?  since            = null
    ) {
```

to:

```csharp
public static async Task<int> HandleHistory(
        string       baseUrl,
        string?      filterCwd,
        string?      filterSession     = null,
        int          minLines          = 15,
        bool         generateSummaries = false,
        bool         codex             = false,
        DateOnly?    since             = null,
        ImportScope? scope             = null,
        bool         skipConfirmation  = false,
        bool         forcePrivate      = false,
        string       activeProfile     = "default",
        (string Owner, string Name)? currentRepo = null
    ) {
```

- [ ] **Step 2: Insert the scope step after pre-filters and before classify**

In `HandleHistory`, locate the block ending with:

```csharp
display.Line($"Found {transcriptFiles.Count} {vendor} session{...");
```

(around line 247). Immediately after that line, insert:

```csharp
// --- Scope: pre-detect repos for the filter and (if needed) the picker ---
async ValueTask<(string? Owner, string? Name)> ResolveRepoAsync(
    (string SessionId, string FilePath, string EncodedCwd) t,
    CancellationToken                                       ctInner) {
    var cwd = ExtractCwdFromTranscript(t.FilePath, codex);
    if (cwd is null) return (null, null);
    var repo = await RepositoryDetection.DetectRepositoryAsync(cwd);
    return (repo?.Owner, repo?.RepoName);
}

// If we landed without a resolved scope, run the picker. The picker needs
// the set of distinct repos to drive its "specific repository" sub-picker,
// so detect ahead of time and reuse the results in the filter pass.
if (scope is null) {
    var resolved = new Dictionary<string, (string Owner, string Name)?>(StringComparer.Ordinal);
    foreach (var t in transcriptFiles) {
        var (o, n) = await ResolveRepoAsync(t, CancellationToken.None);
        resolved[t.SessionId] = (o is not null && n is not null) ? (o, n) : null;
    }
    var distinct = resolved.Values
        .Where(v => v is not null)
        .Select(v => v!.Value)
        .ToList();

    scope = HistoryScopePrompt.RunPicker(activeProfile, currentRepo, distinct);
    if (scope is null) {
        await Console.Error.WriteLineAsync("Scope selection cancelled.");
        return 1;
    }

    // Reuse the cached lookup in the filter pass below.
    transcriptFiles = await HistoryScopeFilter.Apply(
        transcriptFiles,
        scope,
        (t, _) => new ValueTask<(string?, string?)>(
            resolved.TryGetValue(t.SessionId, out var v) && v is { } x ? (x.Owner, x.Name) : (null, null)));
} else {
    transcriptFiles = await HistoryScopeFilter.Apply(transcriptFiles, scope, ResolveRepoAsync);
}

if (transcriptFiles.Count == 0) {
    display.Line("No sessions match the selected scope.");
    return 0;
}

// --- Confirmation ---
var sampleRepos = new List<string>();
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var t in transcriptFiles) {
        var (o, n) = await ResolveRepoAsync(t, CancellationToken.None);
        if (o is null || n is null) continue;
        if (seen.Add($"{o}/{n}")) sampleRepos.Add($"{o}/{n}");
    }
}

var visibilityDesc = forcePrivate
    ? "private (--private)"
    : $"{(await AppConfig.Load())?.DefaultVisibility ?? "org_public"} (from profile)";

if (!HistoryScopePrompt.PromptConfirm(
        scope, transcriptFiles.Count, sampleRepos, visibilityDesc, skipConfirmation)) {
    await Console.Error.WriteLineAsync("Import cancelled.");
    return 0;
}
```

- [ ] **Step 3: Track imported session ids for the --private loop**

Below the existing `OnSessionEnded` handler (search for `OnSessionEnded = (_, c, outcome, lines) =>`), add a `ConcurrentBag<string>` for imported ids and append to it from the handler:

Find this block (around line 384):

```csharp
            OnSessionEnded = (_, c, outcome, lines) => {
                var verb = outcome == SessionImportOutcome.Resumed
                    ? $"resuming from line {c.ResumeFromLine}"
                    : "new";

                display.Line(
                    $"Loading {c.SessionId}... {lines} lines [{verb}]",
                    $"[green]✓[/] Loading [cyan]{Markup.Escape(c.SessionId)}[/]... {lines} lines [{verb}]"
                );
            },
```

Just above the `var events = new ChainWorkerEvents { ... }` declaration, add:

```csharp
var importedSessionIds = new System.Collections.Concurrent.ConcurrentBag<string>();
```

Then in **both** `OnSessionEnded` handlers (the non-TTY one above and the TTY-wrapped one in the Progress block below), add `importedSessionIds.Add(c.SessionId);` as the first line of the lambda. For the TTY-wrapped handler (which uses `_` for the classification), change the parameter to a name (e.g. `c`) and add the line there too.

Concretely, change the non-TTY handler to:

```csharp
            OnSessionEnded = (_, c, outcome, lines) => {
                importedSessionIds.Add(c.SessionId);
                var verb = outcome == SessionImportOutcome.Resumed
                    ? $"resuming from line {c.ResumeFromLine}"
                    : "new";

                display.Line(
                    $"Loading {c.SessionId}... {lines} lines [{verb}]",
                    $"[green]✓[/] Loading [cyan]{Markup.Escape(c.SessionId)}[/]... {lines} lines [{verb}]"
                );
            },
```

And the TTY-wrapped handler (currently using `(slot, _, _, _) => { ... bar.Increment(1); slots[slot].IsIndeterminate = false; }`) to:

```csharp
                                OnSessionEnded = (slot, c, _, _) => {
                                    importedSessionIds.Add(c.SessionId);
                                    bar.Increment(1);
                                    slots[slot].IsIndeterminate = false;
                                },
```

- [ ] **Step 4: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: 0 errors.

- [ ] **Step 5: Re-run all existing history unit tests to confirm no regression**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/History*/*"
```

Expected: all PASS. (Some tests already exercise `HandleHistory`'s internals — none should fail because the only new parameters are optional.)

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs
git commit -m "[AI-613] wire scope resolution + confirmation into HandleHistory"
```

---

## Task 7: `--private` post-import visibility loop

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`
- Create: `test/kapacitor.Tests.Integration/HistoryPrivateImportTests.cs`

- [ ] **Step 1: Write the failing integration test**

`test/kapacitor.Tests.Integration/HistoryPrivateImportTests.cs`:

```csharp
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Integration;

public class HistoryPrivateImportTests : IDisposable {
    readonly WireMockServer _server  = WireMockServer.Start();
    readonly string         _tempDir = Directory.CreateTempSubdirectory("kapacitor-private-test").FullName;

    public void Dispose() {
        _server.Stop();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task SetVisibilityNoneForAll_calls_PUT_for_each_session_id() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();

        await HistoryCommand.SetVisibilityNoneForAll(
            client,
            _server.Url!,
            ["sess1", "sess2", "sess3"]);

        var calls = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "PUT")
            .Select(e => e.RequestMessage.Path)
            .OrderBy(p => p)
            .ToArray();

        await Assert.That(calls).IsEquivalentTo(new[] {
            "/api/sessions/sess1/visibility",
            "/api/sessions/sess2/visibility",
            "/api/sessions/sess3/visibility",
        });
    }

    [Test]
    public async Task SetVisibilityNoneForAll_continues_on_per_session_failure() {
        _server.Given(Request.Create().WithPath("/api/sessions/sess2/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(500));
        _server.Given(Request.Create().WithPath("/api/sessions/sess*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();

        // Should not throw even though sess2 returns 500.
        await HistoryCommand.SetVisibilityNoneForAll(
            client,
            _server.Url!,
            ["sess1", "sess2", "sess3"]);

        var attempted = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "PUT")
            .Count();

        await Assert.That(attempted).IsEqualTo(3);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj -- --treenode-filter "/*/*/HistoryPrivateImportTests/*"
```

Expected: FAIL — `SetVisibilityNoneForAll` does not exist.

- [ ] **Step 3: Add the helper and call it post-import**

In `src/kapacitor/Commands/HistoryCommand.cs`, add this internal helper near the end of the class (just before the closing brace):

```csharp
    /// <summary>
    /// PUT visibility=none for every imported session id. Failures are logged
    /// inline (one line per session) but never throw — the import already
    /// succeeded; users can re-run `kapacitor hide` for any that failed.
    /// </summary>
    internal static async Task SetVisibilityNoneForAll(
        HttpClient            httpClient,
        string                baseUrl,
        IReadOnlyList<string> sessionIds) {
        foreach (var sessionId in sessionIds) {
            var payload = new System.Text.Json.Nodes.JsonObject { ["visibility"] = "none" };
            using var content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            try {
                using var resp = await httpClient.PutWithRetryAsync(
                    $"{baseUrl}/api/sessions/{sessionId}/visibility", content);
                if (!resp.IsSuccessStatusCode) {
                    await Console.Error.WriteLineAsync(
                        $"  ! visibility=none failed for {sessionId}: HTTP {(int)resp.StatusCode}");
                }
            } catch (Exception ex) {
                await Console.Error.WriteLineAsync(
                    $"  ! visibility=none failed for {sessionId}: {ex.Message}");
            }
        }
    }
```

Then call it after the import loop completes. In `HandleHistory`, just before the `// --- Background phase ---` comment (around line 552), insert:

```csharp
// --- --private: mark all imported sessions owner-only ---
if (forcePrivate && !importedSessionIds.IsEmpty) {
    display.BeginPhase("Marking imported sessions private");
    await SetVisibilityNoneForAll(httpClient, baseUrl, [.. importedSessionIds]);
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj -- --treenode-filter "/*/*/HistoryPrivateImportTests/*"
```

Expected: both tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs test/kapacitor.Tests.Integration/HistoryPrivateImportTests.cs
git commit -m "[AI-613] post-import --private visibility loop"
```

---

## Task 8: Wire argument parser in Program.cs

**Files:**
- Modify: `src/kapacitor/Program.cs`

- [ ] **Step 1: Update the `case "history"` block**

Locate the `case "history"` block (around line 382). Replace the entire block's body with the version below — note the new flag parsing, current-repo resolution, and the updated `HandleHistory` call.

```csharp
    case "history": {
        string?   filterCwd     = null;
        string?   filterSession = null;
        var       minLines      = 15;
        DateOnly? since         = null;
        var       codex         = args.Contains("--codex");
        var       cwdArgIdx     = Array.IndexOf(args, "--cwd");

        if (cwdArgIdx >= 0 && cwdArgIdx + 1 < args.Length) {
            filterCwd = args[cwdArgIdx + 1];
        }

        var sessionArgIdx = Array.IndexOf(args, "--session");

        if (sessionArgIdx >= 0 && sessionArgIdx + 1 < args.Length) {
            filterSession = args[sessionArgIdx + 1];
        }

        var minLinesIdx = Array.IndexOf(args, "--min-lines");

        if (minLinesIdx >= 0 && minLinesIdx + 1 < args.Length && int.TryParse(args[minLinesIdx + 1], out var parsed)) {
            minLines = parsed;
        }

        var sinceIdx = Array.IndexOf(args, "--since");

        if (sinceIdx >= 0 && sinceIdx + 1 < args.Length) {
            if (!DateOnly.TryParseExact(args[sinceIdx + 1], "yyyy-MM-dd", out var parsedSince)) {
                Console.Error.WriteLine("--since must be YYYY-MM-DD");

                return 1;
            }

            since = parsedSince;
        }

        var generateSummaries = args.Contains("--generate-summaries");

        // --- Scope resolution (AI-613) ---
        var profileConfig = await AppConfig.LoadProfileConfig();
        var activeProfile = string.IsNullOrEmpty(profileConfig.ActiveProfile) ? "default" : profileConfig.ActiveProfile;

        var currentRepoDetected = await RepositoryDetection.DetectRepositoryAsync(Environment.CurrentDirectory);
        (string Owner, string Name)? currentRepo = currentRepoDetected is { Owner: { } o, RepoName: { } n }
            ? (o, n)
            : null;

        var flags = ImportScopeArgs.ParseFlags(args);
        var resolveResult = ImportScopeArgs.Resolve(new(
            Flags:         flags,
            ActiveProfile: activeProfile,
            IsInteractive: !Console.IsInputRedirected && !Console.IsOutputRedirected,
            CurrentRepo:   currentRepo));

        if (resolveResult.Error is not null) {
            Console.Error.WriteLine(resolveResult.Error);
            return 1;
        }

        return await HistoryCommand.HandleHistory(
            baseUrl!,
            filterCwd,
            filterSession,
            minLines,
            generateSummaries,
            codex,
            since,
            scope:            resolveResult.Scope,     // null => HandleHistory runs picker
            skipConfirmation: resolveResult.Yes,
            forcePrivate:     resolveResult.Private,
            activeProfile:    activeProfile,
            currentRepo:      currentRepo);
    }
```

Add the using at the top of the file if not already present:

```csharp
using kapacitor.Commands;
```

(Check first; the file already references several `kapacitor.Commands` types — the using is likely already imported.)

- [ ] **Step 2: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: 0 errors.

- [ ] **Step 3: Verify no AOT warnings on publish**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' | head
```

Expected: no output (no AOT trim/dynamic-code warnings).

- [ ] **Step 4: Run the full unit test suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Program.cs
git commit -m "[AI-613] wire history scope flags in Program.cs"
```

---

## Task 9: Setup tip, help text, manual smoke test

**Files:**
- Modify: `src/kapacitor/Commands/SetupCommand.cs`
- Modify: `src/Kapacitor.Core/Resources/help-history.txt`

- [ ] **Step 1: Append the tip to setup**

In `src/kapacitor/Commands/SetupCommand.cs`, find the line:

```csharp
        AnsiConsole.MarkupLine("\n[dim]Optional:[/] start the agent daemon with [cyan]kapacitor agent start -d[/]");
```

Add a second tip line directly after it:

```csharp
        AnsiConsole.MarkupLine("[dim]Optional:[/] import past sessions with [cyan]kapacitor history --org[/]");
```

- [ ] **Step 2: Update the help text**

Replace `src/Kapacitor.Core/Resources/help-history.txt` with:

```
kapacitor history — Import local transcript history to server

Usage: kapacitor history [scope] [options]

Scope (one of, required for non-interactive use):
  --all                   Import every discovered session
  --org                   Import only sessions from your active profile's org
  --repo <owner/name>     Import only sessions from a specific repo
  --repo .                Import only sessions from the current cwd's repo

Without a scope flag on an interactive terminal, an interactive picker is shown.

Options:
  --yes, -y               Skip the confirmation prompt
  --private               Mark every imported session as Only Visible to You
  --codex                 Import Codex rollouts from ~/.codex/sessions instead of Claude
  --since YYYY-MM-DD      Only import sessions started on or after this date
  --cwd <path>            Filter by working directory (composes with scope)
  --session <id>          Import a specific session only (composes with scope)
  --min-lines <n>         Skip sessions shorter than n lines (default: 15)
  --generate-summaries    Also generate per-session what's-done summaries
```

- [ ] **Step 3: Build and run the full test suite**

```bash
dotnet build src/kapacitor/kapacitor.csproj
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj
```

Expected: 0 build errors; all tests PASS.

- [ ] **Step 4: AOT publish check**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: empty output.

- [ ] **Step 5: Manual smoke test**

Run each of these against a real kapacitor server (or one started locally) and verify the noted behaviour:

1. **Picker path:**
   ```bash
   kapacitor history
   ```
   - Expect: three-option picker (`Everything` / `Org repos only (<your profile>)` / `Specific repository`).
   - Pick "Specific repository" — verify the sub-picker lists distinct repos and pins the current cwd's repo at the top.
   - Verify the confirmation summary prints and `n` cancels (exit 0, no imports).

2. **`--all` happy path:**
   ```bash
   kapacitor history --all --yes
   ```
   - Expect: no picker, summary block, immediate proceed, normal import.

3. **`--org` missing profile guard:**
   ```bash
   KAPACITOR_PROFILE=default kapacitor history --org
   ```
   - Expect: error message mentioning "tenant-bound profile", exit 1.

4. **`--repo .` happy path** (run inside a git repo):
   ```bash
   kapacitor history --repo . --yes
   ```
   - Expect: summary names the current repo, only its sessions match.

5. **`--repo .` outside a git repo:**
   ```bash
   cd /tmp && kapacitor history --repo .
   ```
   - Expect: error "not a git repo with an origin remote", exit 1.

6. **`--private`:**
   ```bash
   kapacitor history --repo EventStore/kapacitor --yes --private
   ```
   - Expect: summary shows `visibility: private (--private)`; after import, every imported session shows `visibility: none` on the server.

7. **Non-TTY without flag:**
   ```bash
   kapacitor history < /dev/null
   ```
   - Expect: error "--all, --org, or --repo <owner/name> is required for non-interactive use", exit 1.

8. **Non-TTY with flag but no `--yes`:**
   ```bash
   kapacitor history --all < /dev/null
   ```
   - Expect: error "--yes is required for non-interactive use", exit 1.

9. **Mutual exclusivity:**
   ```bash
   kapacitor history --all --org --yes
   ```
   - Expect: error "mutually exclusive", exit 1.

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/SetupCommand.cs src/Kapacitor.Core/Resources/help-history.txt
git commit -m "[AI-613] setup tip + history help text"
```

- [ ] **Step 7: Open the PR**

```bash
git push -u origin <branch>
gh pr create --title "[AI-613] history: explicit scope selection + --private" --body "$(cat <<'EOF'
## Summary
- Adds `--all` / `--org` / `--repo <owner/name>` / `--repo .` scope flags to `kapacitor history`, plus an interactive picker when no flag is passed on a TTY.
- Always prints a confirmation summary before classify/import; `--yes` skips the prompt.
- Adds `--private` to post-import PUT `visibility=none` for every imported session.
- Non-interactive use without a scope flag now errors (exit 1) to prevent accidental uploads — release-note this.

## Test plan
- [x] Unit: `ImportScopeArgsTests`, `HistoryScopeFilterTests`, `HistoryScopePromptTests`
- [x] Integration: `HistoryPrivateImportTests` (WireMock for visibility PUT)
- [x] AOT publish: no IL2026/IL3050 warnings
- [x] Manual smoke: picker, `--all --yes`, `--org` guard, `--repo .`, `--private`, non-TTY no-flag error

Closes [AI-613](https://linear.app/kurrent/issue/AI-613).

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Self-Review

Spec coverage check (compared with `2026-05-13-ai-613-history-import-scope-design.md`):

- Scope flags `--all`/`--org`/`--repo <owner/name>`/`--repo .` — Tasks 2, 8.
- Mutual exclusivity — Task 2.
- Interactive picker (main + sub-picker, current-repo pinned) — Tasks 4, 5, 6.
- Confirmation summary + y/N — Tasks 4, 5, 6.
- `--yes` skip — Tasks 2, 5, 6.
- `--private` post-import PUT visibility=none — Task 7.
- Non-TTY no-flag error — Task 2.
- Non-TTY flag-without-`--yes` error — Task 2.
- `--repo .` errors when cwd has no repo — Task 2.
- `--org` errors when profile is `default` — Task 2.
- Compose with `--cwd`/`--session`/`--min-lines`/`--since` + `excluded_repos` — unchanged in the pipeline; verified in Task 6 by re-running existing tests.
- Setup tip + help text — Task 9.
- AOT publish check — Tasks 8, 9.

No placeholders, no TODOs, no "similar to Task N" hand-waves. Type names consistent across tasks (`ImportScope.All`, `ImportScope.Org(string)`, `ImportScope.Repo(string, string)`; `ImportScopeArgs.ParsedFlags`, `ResolveInput`, `ResolveResult`).
