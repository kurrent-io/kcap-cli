# Repo-Local Skill Materialization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `kcap skills sync` materializes a repository's approved skills into that checkout or worktree instead of the user's home directory, keeps its ownership ledger in the worktree's own git directory, keeps the generated files out of Git, and migrates the old global copies away.

**Architecture:** The reconciliation planner, the drift rule and the conditional fetch stay as they are. A destination resolver turns an explicit anchor directory into four repository-relative roots; the manifest moves into `<git-dir>/kcap/skills/<target>.json` and gains identity, exposure and a journal of paths awaiting deletion; every write and prune is containment-checked against the anchor; a machine-wide lock covers the legacy migration and a repository lock covers the exclusion block.

**Tech Stack:** .NET 10, NativeAOT, TUnit with the repository's `TempDir` fixture, `System.Text.Json` source generation through `CapacitorJsonContext`.

**Spec:** `docs/superpowers/specs/2026-09-18-repo-local-skill-materialization-design.md`

## Global Constraints

- The four target keys are `agents`, `claude`, `kiro`, `gemini`, with repository-relative roots `.agents/skills`, `.claude/skills`, `.kiro/skills`, `.gemini/skills`.
- Vendors fetched per target: `agents` none, `claude` `"claude"`, `kiro` `"kiro"`, `gemini` none.
- Measured readers per target: `agents` = Codex, Copilot, Cursor, OpenCode, Pi, Antigravity; `claude` = Claude, Copilot, Cursor, OpenCode; `kiro` = Kiro; `gemini` = none.
- Documented consumers per target (these decide adoption): `agents` = Codex, Copilot, Cursor, OpenCode, Pi, Antigravity; `claude` = Claude; `kiro` = Kiro; `gemini` = Gemini, Antigravity.
- Manifest path: `<git-dir>/kcap/skills/<target>.json`. `<git-dir>` is `.git` in a main checkout and `<main>/.git/worktrees/<name>` in a linked worktree.
- Lock order, never reversed: migration lock, then repository lock, then per-worktree manifest lock. No shared lock is held across a network request.
- The migration lock is machine-wide and single-keyed. The repository lock is keyed by repository. The manifest lock keeps today's per-manifest key.
- Identity is the account the token authenticates as plus the server URL. The profile name is not identity.
- Precedence: an identity retirement deletes entries and journal paths before any fetch; an anchor move is copy-before-delete and only while the identity is unchanged.
- A journal entry is a `(path, root)` pair. Journals merge, never replace, and are reconciled against the plan before publication so a path that is live again is not deleted.
- No production assembly gets `InternalsVisibleTo`; a member another shipping project needs is public.
- `Environment.GetFolderPath` is banned (RS0030); take a `UserHome`.
- AOT: run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release` and check for IL3050/IL2026 after touching serialization.
- Comments follow CLAUDE.md: scarce, no history, no ticket ids, no plan coordinates.
- Commit subjects: one imperative clause, at most 80 characters including a trailing `(#778)`, trailer `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.
- In this worktree run git as `/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/peppy-percolating-biscuit <args>`, one plain command per invocation, no heredocs.
- Build with `dotnet build Capacitor.slnx`; run one suite with `dotnet run --project test/<project>/<project>.csproj`.

---

## File structure

```
src/Capacitor.Cli.Core/
  CanonicalPath.cs                     NEW  root-first symlink resolution and containment
  GitRepository.cs                     MOD  + ResolveGitDir
  AgentsPaths.cs                       MOD  + RepoSkillsDir(anchor)
  Harness/Claude/ClaudePaths.cs        MOD  + RepoSkillsDir(anchor)
  Harness/Kiro/KiroPaths.cs            MOD  + RepoSkillsDir(anchor)
  Harness/Gemini/GeminiPaths.cs        MOD  + RepoSkillsDir(anchor)
  Skills/SkillsSync.cs                 MOD  SkillsTarget, SkillsManifest, entries, identity, journal
  Skills/SkillsMaterializer.cs         MOD  containment-checked, atomic write; prune by (path, root)
  Skills/SkillsJournal.cs              NEW  merge, reconcile against a plan, recover
  Skills/SkillsExclusion.cs            NEW  the managed info/exclude block
  Skills/SkillsLocks.cs                NEW  lock names and the fixed acquisition order
  Skills/SkillsLegacyMigration.cs      NEW  global manifest discovery, overlap rule, retirement
src/Capacitor.Cli/Commands/
  SkillsCommand.cs                     MOD  anchor, transitions, adoption, no-change paths
test/Capacitor.Cli.Core.Tests.Unit/
  CanonicalPathTests.cs                NEW
  GitRepositoryGitDirTests.cs          NEW
  Skills/SkillsMaterializerTests.cs    MOD  containment and atomic publication
  Skills/SkillsJournalTests.cs         NEW
  Skills/SkillsExclusionTests.cs       NEW
  Skills/SkillsLegacyMigrationTests.cs NEW
test/Capacitor.Cli.Tests.Unit/Commands/
  SkillsTargetCatalogTests.cs          MOD  anchor-relative roots, consumers, readers
  SkillsSyncFlowTests.cs               NEW  the command path against a mocked snapshot API
docs/probes/2026-09-16-skills-discovery/
  probe.py, harness/*.py               MOD  a compatibility-root run of S3 and S8
README.md                              MOD  where `kcap skills sync` writes
```

---

### Task 1: Canonical path resolution in Core

The daemon already resolves symlinks in every path component, but that code lives in an assembly the CLI cannot reference. Move the walk into Core and leave the daemon delegating to it.

**Files:**
- Create: `src/Capacitor.Cli.Core/CanonicalPath.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/BorrowAuthorizer.cs` (delete `RealPath`, `SplitSegments`, `MaxResolveSteps`; `Canonicalize` delegates)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/CanonicalPathTests.cs`

**Interfaces:**
- Produces: `CanonicalPath.Resolve(string path) -> string`, `CanonicalPath.IsWithin(string candidate, string boundary) -> bool`.

- [ ] **Step 1: Write the failing test**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit;

public class CanonicalPathTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task An_ancestor_symlink_is_resolved_not_just_the_leaf() {
        var real   = Tmp.CreateDir("real/inner");
        var linked = Tmp.PathTo("link");
        Directory.CreateSymbolicLink(linked, Tmp.GetResolvedPath("real"));

        var resolved = CanonicalPath.Resolve(Path.Combine(linked, "inner"));

        await Assert.That(resolved).IsEqualTo(Tmp.GetResolvedPath("real/inner"));
    }

    [Test]
    public async Task Containment_follows_links_out_of_the_boundary() {
        var boundary = Tmp.CreateDir("repo");
        var outside  = Tmp.CreateDir("outside");
        var escape   = Path.Combine(boundary, "escape");
        Directory.CreateSymbolicLink(escape, outside);

        await Assert.That(CanonicalPath.IsWithin(Path.Combine(boundary, "inside"), boundary)).IsTrue();
        await Assert.That(CanonicalPath.IsWithin(escape, boundary)).IsFalse();
        await Assert.That(CanonicalPath.IsWithin(Path.Combine(escape, "deeper"), boundary)).IsFalse();
        await Assert.That(CanonicalPath.IsWithin(boundary, boundary)).IsTrue();
    }

    [Test]
    public async Task A_symlink_cycle_terminates() {
        var a = Tmp.PathTo("a");
        var b = Tmp.PathTo("b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        await Assert.That(CanonicalPath.Resolve(Path.Combine(a, "x"))).IsNotNull();
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj --treenode-filter "/*/*/CanonicalPathTests/*"`
Expected: FAIL, `CanonicalPath` does not exist.

- [ ] **Step 3: Move the walk into Core**

Create `src/Capacitor.Cli.Core/CanonicalPath.cs` with the body of `BorrowAuthorizer.RealPath`, `SplitSegments` and `MaxResolveSteps` moved verbatim, renamed as below, plus the containment check:

```csharp
namespace Capacitor.Cli.Core;

/// <summary>A realpath-style walk: every component is resolved, not only the leaf, because an
/// ancestor symlink is how a path outside a boundary textually matches one inside it.</summary>
public static class CanonicalPath {
    const int MaxResolveSteps = 64;

    public static string Resolve(string path) => RealPath(Path.GetFullPath(path));

    /// <summary>Whether <paramref name="candidate"/> resolves to <paramref name="boundary"/> itself
    /// or to something beneath it.</summary>
    public static bool IsWithin(string candidate, string boundary) {
        var resolvedBoundary = Resolve(boundary);
        var resolved         = Resolve(candidate);
        if (string.Equals(resolved, resolvedBoundary, StringComparison.Ordinal)) return true;
        var prefix = resolvedBoundary.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedBoundary
            : resolvedBoundary + Path.DirectorySeparatorChar;
        return resolved.StartsWith(prefix, StringComparison.Ordinal);
    }

    static string RealPath(string fullPath) { /* the existing BorrowAuthorizer.RealPath body */ }
    static Queue<string> SplitSegments(string relative) { /* the existing body */ }
}
```

Then in `BorrowAuthorizer`, delete the moved members and replace the public entry point:

```csharp
    public static string Canonicalize(string path) => CanonicalPath.Resolve(path);
```

- [ ] **Step 4: Run both suites**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj --treenode-filter "/*/*/CanonicalPathTests/*"`
Expected: PASS.
Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj --treenode-filter "/*/*/BorrowAuthorizer*/*"`
Expected: PASS, unchanged.

- [ ] **Step 5: Commit**

`Resolve a containment boundary from Core, not the daemon (#778)`

---

### Task 2: The worktree's own git directory

**Files:**
- Modify: `src/Capacitor.Cli.Core/GitRepository.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/GitRepositoryGitDirTests.cs`

**Interfaces:**
- Produces: `GitRepository.ResolveGitDir(string startDir) -> string?` — the git directory that belongs to this working tree: `<root>/.git` for a main checkout, the `gitdir:` target for a linked worktree, `null` when there is no repository.

- [ ] **Step 1: Write the failing test**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit;

public class GitRepositoryGitDirTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_main_checkout_resolves_to_its_own_dot_git() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.git");

        await Assert.That(GitRepository.ResolveGitDir(repo))
            .IsEqualTo(Path.Combine(Tmp.GetResolvedPath("repo"), ".git"));
    }

    [Test]
    public async Task A_linked_worktree_resolves_to_its_worktrees_entry() {
        var main     = Tmp.CreateDir("main");
        var worktree = Tmp.CreateDir("wt");
        var entry    = Tmp.CreateDir("main/.git/worktrees/wt");
        Tmp.CreateFile("wt/.git", $"gitdir: {entry}\n");

        await Assert.That(GitRepository.ResolveGitDir(worktree))
            .IsEqualTo(Tmp.GetResolvedPath("main/.git/worktrees/wt"));
    }

    [Test]
    public async Task No_repository_resolves_to_null() =>
        await Assert.That(GitRepository.ResolveGitDir(Tmp.CreateDir("bare"))).IsNull();
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj --treenode-filter "/*/*/GitRepositoryGitDirTests/*"`
Expected: FAIL, `ResolveGitDir` does not exist.

- [ ] **Step 3: Implement**

```csharp
    /// <summary>The git directory belonging to THIS working tree: <c>.git</c> in a main checkout,
    /// and the <c>gitdir:</c> target in a linked worktree, which is where per-worktree state
    /// belongs and where Git itself removes it with the worktree.</summary>
    public static string? ResolveGitDir(string startDir) {
        var root = FindRoot(startDir);
        if (root is null) return null;
        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit)) return CanonicalPath.Resolve(dotGit);
        try {
            var line = File.ReadAllText(dotGit).Trim();
            const string marker = "gitdir:";
            if (!line.StartsWith(marker, StringComparison.Ordinal)) return null;
            var target = line[marker.Length..].Trim();
            if (!Path.IsPathRooted(target)) target = Path.Combine(root, target);
            return CanonicalPath.Resolve(target);
        } catch {
            return null;
        }
    }
```

- [ ] **Step 4: Run the test**

Expected: PASS.

- [ ] **Step 5: Commit**

`Resolve the git directory that belongs to a worktree (#778)`

---

### Task 3: Repository-relative skills roots and the target catalogue

**Files:**
- Modify: `src/Capacitor.Cli.Core/AgentsPaths.cs`, `src/Capacitor.Cli.Core/Harness/Claude/ClaudePaths.cs`, `src/Capacitor.Cli.Core/Harness/Kiro/KiroPaths.cs`, `src/Capacitor.Cli.Core/Harness/Gemini/GeminiPaths.cs`
- Modify: `src/Capacitor.Cli.Core/Skills/SkillsSync.cs` (the `SkillsTarget` record)
- Modify: `src/Capacitor.Cli/Commands/SkillsCommand.cs` (`Targets`, `ConsumerPresent`)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/SkillsTargetCatalogTests.cs`

**Interfaces:**
- Produces: `SkillsTarget(string Key, string RelativePath, string? Vendor, IReadOnlyList<HarnessId> Consumers, IReadOnlyList<HarnessId> Readers)` with `string Root(string anchor) => Path.Combine(anchor, RelativePath)`; `SkillsCommand.Targets() -> IReadOnlyList<SkillsTarget>` (no arguments — roots are now anchor-relative); `SkillsCommand.Adopted(HarnessRegistry harnesses, SkillsTarget target, bool hasManifest, bool hasLegacyManifest) -> bool`.
- Each `Paths` type gains `public static string RepoSkillsDir(string anchor)`.

- [ ] **Step 1: Write the failing test**

Replace the body of `SkillsTargetCatalogTests` with:

```csharp
public class SkillsTargetCatalogTests {
    [Test]
    public async Task Every_target_is_anchor_relative_and_leafed_skills() {
        foreach (var t in SkillsCommand.Targets()) {
            await Assert.That(Path.IsPathRooted(t.RelativePath)).IsFalse();
            await Assert.That(Path.GetFileName(t.RelativePath)).IsEqualTo("skills");
            await Assert.That(t.Root("/anchor")).IsEqualTo(Path.Combine("/anchor", t.RelativePath));
        }
    }

    [Test]
    public async Task The_catalogue_matches_the_measured_roots_and_vendors() {
        var byKey = SkillsCommand.Targets().ToDictionary(t => t.Key);

        await Assert.That(byKey["agents"].RelativePath).IsEqualTo(Path.Combine(".agents", "skills"));
        await Assert.That(byKey["claude"].RelativePath).IsEqualTo(Path.Combine(".claude", "skills"));
        await Assert.That(byKey["kiro"].RelativePath).IsEqualTo(Path.Combine(".kiro", "skills"));
        await Assert.That(byKey["gemini"].RelativePath).IsEqualTo(Path.Combine(".gemini", "skills"));

        await Assert.That(byKey["agents"].Vendor).IsNull();
        await Assert.That(byKey["claude"].Vendor).IsEqualTo("claude");
        await Assert.That(byKey["kiro"].Vendor).IsEqualTo("kiro");
        await Assert.That(byKey["gemini"].Vendor).IsNull();

        // Measured in the probe matrix; the exposure a manifest records.
        await Assert.That(byKey["claude"].Readers)
            .IsEquivalentTo([HarnessId.Claude, HarnessId.Copilot, HarnessId.Cursor, HarnessId.OpenCode]);
        await Assert.That(byKey["gemini"].Readers).IsEmpty();
    }

    [Test]
    public async Task Adoption_follows_consumers_so_an_unmeasured_tree_is_still_served() {
        var byKey = SkillsCommand.Targets().ToDictionary(t => t.Key);
        var gemini = new HarnessRegistryStub(HarnessId.Gemini);
        var antigravity = new HarnessRegistryStub(HarnessId.Antigravity);

        await Assert.That(SkillsCommand.Adopted(gemini, byKey["gemini"], false, false)).IsTrue();
        await Assert.That(SkillsCommand.Adopted(antigravity, byKey["agents"], false, false)).IsTrue();
        await Assert.That(SkillsCommand.Adopted(antigravity, byKey["claude"], false, false)).IsFalse();
        // A target kcap already owns keeps reconciling so a revocation still reaches it.
        await Assert.That(SkillsCommand.Adopted(antigravity, byKey["claude"], true, false)).IsTrue();
        await Assert.That(SkillsCommand.Adopted(antigravity, byKey["claude"], false, true)).IsTrue();
    }
}
```

`HarnessRegistryStub` is a test double in the same file whose `Detected(HarnessId)` returns true only for the ids it was constructed with; model it on the existing stub in that test project if one is present, otherwise:

```csharp
sealed class HarnessRegistryStub(params HarnessId[] present) : HarnessRegistry {
    public override bool Detected(HarnessId id) => present.Contains(id);
}
```

If `HarnessRegistry.Detected` is not virtual, add an interface `IHarnessDetection { bool Detected(HarnessId id); }` implemented by `HarnessRegistry`, and take that in `Adopted`.

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SkillsTargetCatalogTests/*"`
Expected: FAIL, `Targets()` takes arguments and `SkillsTarget` has no `RelativePath`.

- [ ] **Step 3: Add the per-vendor accessors**

In each `Paths` type, beside the existing user-global member:

```csharp
    /// <summary>The repository-local skills tree, resolved against the session's anchor.</summary>
    public static string RepoSkillsDir(string anchor) => Path.Combine(anchor, ".claude", "skills");
```

with `.agents`, `.kiro` and `.gemini` in the other three. `GeminiPaths` has no user-global skills member today; this is its first.

- [ ] **Step 4: Widen the record and the catalogue**

```csharp
/// <summary>One harness tree skills materialize into, relative to a session's anchor. A null
/// <see cref="Vendor"/> marks a tree several harnesses read: the snapshot is fetched WITHOUT a
/// vendor, so unknown-excludes keeps every vendor-restricted doc out of it. <see cref="Consumers"/>
/// is the documented set this tree serves and decides adoption; <see cref="Readers"/> is the
/// measured set and is what a manifest records as exposure.</summary>
public sealed record SkillsTarget(
    string Key, string RelativePath, string? Vendor,
    IReadOnlyList<HarnessId> Consumers, IReadOnlyList<HarnessId> Readers) {
    public string Root(string anchor) => Path.Combine(anchor, RelativePath);
}
```

```csharp
    internal static IReadOnlyList<SkillsTarget> Targets() => [
        new("agents", Path.Combine(".agents", "skills"), null,
            [HarnessId.Codex, HarnessId.Copilot, HarnessId.Cursor, HarnessId.OpenCode, HarnessId.Pi,
             HarnessId.Antigravity],
            [HarnessId.Codex, HarnessId.Copilot, HarnessId.Cursor, HarnessId.OpenCode, HarnessId.Pi,
             HarnessId.Antigravity]),
        new("claude", Path.Combine(".claude", "skills"), "claude",
            [HarnessId.Claude],
            [HarnessId.Claude, HarnessId.Copilot, HarnessId.Cursor, HarnessId.OpenCode]),
        new("kiro", Path.Combine(".kiro", "skills"), "kiro",
            [HarnessId.Kiro], [HarnessId.Kiro]),
        // No session has confirmed a repository-local .gemini/skills; the tree is kept on the
        // vendor's documentation, which is why it has a consumer and no reader.
        new("gemini", Path.Combine(".gemini", "skills"), null,
            [HarnessId.Gemini, HarnessId.Antigravity], []),
    ];

    internal static bool Adopted(HarnessRegistry harnesses, SkillsTarget target,
                                 bool hasManifest, bool hasLegacyManifest) =>
        hasManifest || hasLegacyManifest || target.Consumers.Any(harnesses.Detected);
```

Delete `ConsumerPresent` and its uses.

- [ ] **Step 5: Run the test and the whole CLI suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: PASS. `SkillsCommand` will not compile against the old `Targets(harnesses, agents)` call until Task 10; keep that call site compiling by passing `Targets()` and resolving roots with the current directory as the anchor for now.

- [ ] **Step 6: Commit**

`Make every skills target anchor-relative with its own readers (#778)`

---

### Task 4: The manifest shape

**Files:**
- Modify: `src/Capacitor.Cli.Core/Skills/SkillsSync.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Skills/SkillsManifestShapeTests.cs` (new file)

**Interfaces:**
- Produces: `SkillsIdentity(string Account, string Server)`; `PendingPrune(string Path, string Root)`; `SkillsManifest` gains `Anchor`, `Identity`, `Exposure`, `Pending`, `PendingPrunes`; `SkillsManifestEntry` gains `Home`, `Applicability`.

- [ ] **Step 1: Write the failing test**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsManifestShapeTests {
    [Test]
    public async Task A_manifest_round_trips_its_identity_exposure_and_journal() {
        var manifest = new SkillsManifest {
            Etag = "e1", SyncedAt = DateTimeOffset.UnixEpoch,
            Anchor = "/repo", Identity = new SkillsIdentity("acct-1", "https://server"),
            Exposure = ["claude", "copilot"], Pending = true,
            PendingPrunes = [new PendingPrune("/repo/.claude/skills/kcap-x", "/repo/.claude/skills")],
            Skills = [new SkillsManifestEntry {
                DocId = Guid.Empty, Slug = "x", Version = 1, ContentHash = "h", Path = "/repo/.claude/skills/kcap-x",
                FileHash = "f", Home = "repo:owner/name", Applicability = "vendor:claude",
            }],
        };

        var json  = JsonSerializer.Serialize(manifest, CapacitorJsonContext.Default.SkillsManifest);
        var again = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.SkillsManifest)!;

        await Assert.That(again.Identity!.Account).IsEqualTo("acct-1");
        await Assert.That(again.Anchor).IsEqualTo("/repo");
        await Assert.That(again.Exposure).IsEquivalentTo(["claude", "copilot"]);
        await Assert.That(again.Pending).IsTrue();
        await Assert.That(again.PendingPrunes![0].Root).IsEqualTo("/repo/.claude/skills");
        await Assert.That(again.Skills![0].Home).IsEqualTo("repo:owner/name");
    }

    [Test]
    public async Task An_older_manifest_still_deserializes() {
        const string old = """{"etag":"e","skills":[{"doc_id":"00000000-0000-0000-0000-000000000000",
            "slug":"x","version":1,"content_hash":"h","path":"/p"}]}""";

        var manifest = JsonSerializer.Deserialize(old, CapacitorJsonContext.Default.SkillsManifest)!;

        // Missing identity reads as "no ledger" at the call site, not as a crash here.
        await Assert.That(manifest.Identity).IsNull();
        await Assert.That(manifest.PendingPrunes).IsNull();
        await Assert.That(manifest.Skills![0].Home).IsNull();
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj --treenode-filter "/*/*/SkillsManifestShapeTests/*"`
Expected: FAIL, the properties do not exist.

- [ ] **Step 3: Implement**

```csharp
/// <summary>The credential the snapshot was fetched under. A profile name is not identity: signing
/// in again replaces the credentials inside one profile and server.</summary>
public sealed record SkillsIdentity {
    [JsonPropertyName("account")] public required string Account { get; init; }
    [JsonPropertyName("server")]  public required string Server  { get; init; }
}

/// <summary>A directory awaiting deletion and the skills root that authorises deleting it. The root
/// travels with the path because containment is defined against an anchor, and a path left over
/// from a previous anchor cannot be authorised by the current one.</summary>
public sealed record PendingPrune {
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("root")] public required string Root { get; init; }
}
```

Add to `SkillsManifest`:

```csharp
    [JsonPropertyName("anchor")]         public string?          Anchor        { get; init; }
    [JsonPropertyName("identity")]       public SkillsIdentity?  Identity      { get; init; }
    [JsonPropertyName("exposure")]       public string[]?        Exposure      { get; init; }
    [JsonPropertyName("pending")]        public bool             Pending       { get; init; }
    [JsonPropertyName("pending_prunes")] public PendingPrune[]?  PendingPrunes { get; init; }
```

Add to `SkillsManifestEntry`:

```csharp
    // Server-provided provenance; derived from the request when an older server omits it.
    [JsonPropertyName("home")]          public string? Home          { get; init; }
    [JsonPropertyName("applicability")] public string? Applicability { get; init; }
```

Add the same two to `SkillSnapshotItem` as optional, so a server that sends them is preserved.

- [ ] **Step 4: Run the test, then check AOT**

Run the filtered test: PASS.
Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output. The new records are reachable from the registered `SkillsManifest`, so the source generator covers them.

- [ ] **Step 5: Commit**

`Record identity, exposure and a prune journal in the manifest (#778)`

---

### Task 5: Containment-checked, atomic materialization

**Files:**
- Modify: `src/Capacitor.Cli.Core/Skills/SkillsMaterializer.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Skills/SkillsMaterializerTests.cs`

**Interfaces:**
- Consumes: `CanonicalPath.IsWithin` (Task 1).
- Produces: `SkillsMaterializer.Write(string root, string anchor, SkillSnapshotItem item)`, `SkillsMaterializer.Prune(string root, string anchor, string path)`. Both return `bool`: false when containment refused. `HasDrifted` and `FileHash` keep their signatures.

- [ ] **Step 1: Write the failing test**

Append to `SkillsMaterializerTests`:

```csharp
    [Test]
    public async Task A_destination_linked_outside_the_anchor_is_refused() {
        var anchor  = Tmp.CreateDir("repo");
        var outside = Tmp.CreateDir("global/skills");
        var root    = Path.Combine(anchor, ".agents", "skills");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        Directory.CreateSymbolicLink(root, outside);

        var written = SkillsMaterializer.Write(root, anchor, Item("x"));

        await Assert.That(written).IsFalse();
        await Assert.That(Directory.GetDirectories(outside)).IsEmpty();
    }

    [Test]
    public async Task A_publication_is_atomic() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo/.agents/skills");

        SkillsMaterializer.Write(root, anchor, Item("x"));

        var dir = SkillsMaterializer.SkillDirFor(root, "x");
        // No partial file is ever left beside the published one.
        await Assert.That(Directory.GetFiles(dir).Select(Path.GetFileName)).IsEquivalentTo(["SKILL.md"]);
    }

    [Test]
    public async Task A_prune_outside_the_anchor_is_refused() {
        var anchor  = Tmp.CreateDir("repo");
        var root    = Tmp.CreateDir("repo/.agents/skills");
        var outside = Tmp.CreateDir("global/kcap-x");

        var pruned = SkillsMaterializer.Prune(root, anchor, outside);

        await Assert.That(pruned).IsFalse();
        await Assert.That(Directory.Exists(outside)).IsTrue();
    }
```

`Item(string slug)` is the existing helper in that file.

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj --treenode-filter "/*/*/SkillsMaterializerTests/*"`
Expected: FAIL, the two-argument overloads do not exist.

- [ ] **Step 3: Implement**

```csharp
    /// <summary>Writes one skill, refusing a destination that leaves the anchor through a link: a
    /// vendor directory inside the repository may be a symlink to the user-global tree, which would
    /// publish repository content globally again.</summary>
    public static bool Write(string root, string anchor, SkillSnapshotItem item) {
        var dir = SkillDirFor(root, item.Slug);
        if (!CanonicalPath.IsWithin(dir, anchor)) return false;
        Directory.CreateDirectory(dir);
        var file = SkillFileFor(dir);
        if (File.Exists(file) && new FileInfo(file).LinkTarget is not null) return false;
        // Publish atomically: an interrupted write must not leave a half-file the drift hash then
        // reads as a hand edit.
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, SkillsSyncPlanner.RenderSkillFile(item));
        File.Move(tmp, file, overwrite: true);
        return true;
    }

    /// <summary>Deletes one owned directory: a DIRECT kcap-* child of the given root that also
    /// resolves inside the anchor.</summary>
    public static bool Prune(string root, string anchor, string path) {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(root), StringComparison.Ordinal)) return false;
        if (!Path.GetFileName(full).StartsWith("kcap-", StringComparison.Ordinal)) return false;
        if (!CanonicalPath.IsWithin(full, anchor)) return false;
        if (!Directory.Exists(full)) return false;
        Directory.Delete(full, recursive: true);
        return true;
    }
```

Keep the existing `Prune(string root, SkillsManifestEntry entry)` deleted; every caller moves to the path form in Task 10.

- [ ] **Step 4: Run the suite**

Expected: PASS, including the pre-existing prune-safety tests, updated to the new signature.

- [ ] **Step 5: Commit**

`Prove a skill's destination is inside the anchor before writing it (#778)`

---

### Task 6: The prune journal

**Files:**
- Create: `src/Capacitor.Cli.Core/Skills/SkillsJournal.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Skills/SkillsJournalTests.cs`

**Interfaces:**
- Consumes: `PendingPrune` (Task 4), `CanonicalPath.Resolve` (Task 1).
- Produces: `SkillsJournal.Merge(IReadOnlyList<PendingPrune>? existing, IEnumerable<PendingPrune> added) -> PendingPrune[]`, `SkillsJournal.Reconcile(IReadOnlyList<PendingPrune> journal, IEnumerable<string> liveDestinations) -> PendingPrune[]`.

- [ ] **Step 1: Write the failing test**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsJournalTests {
    static PendingPrune P(string path) => new() { Path = path, Root = "/repo/.claude/skills" };

    [Test]
    public async Task Merging_keeps_what_an_earlier_transition_still_owes() {
        var merged = SkillsJournal.Merge([P("/a"), P("/b")], [P("/b"), P("/c")]);

        await Assert.That(merged.Select(p => p.Path)).IsEquivalentTo(["/a", "/b", "/c"]);
    }

    [Test]
    public async Task A_path_that_is_live_again_is_not_deleted() {
        // x renamed to y, crash, then renamed back to x before the retry.
        var journal = SkillsJournal.Merge(null, [P("/repo/.claude/skills/kcap-x")]);

        var reconciled = SkillsJournal.Reconcile(journal, ["/repo/.claude/skills/kcap-x"]);

        await Assert.That(reconciled).IsEmpty();
    }

    [Test]
    public async Task Reconciliation_compares_resolved_destinations() {
        var journal = SkillsJournal.Merge(null, [P("/repo/./.claude/skills/../skills/kcap-x")]);

        var reconciled = SkillsJournal.Reconcile(journal, ["/repo/.claude/skills/kcap-x"]);

        await Assert.That(reconciled).IsEmpty();
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj --treenode-filter "/*/*/SkillsJournalTests/*"`
Expected: FAIL, `SkillsJournal` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace Capacitor.Cli.Core.Skills;

/// <summary>The paths a sync still owes a deletion. Kept apart from the document-keyed entries
/// because a rename — one document, two paths — cannot be recorded in a ledger with one row per
/// document, and a crash between the write and the prune would otherwise leave the old path with
/// nothing owning it.</summary>
public static class SkillsJournal {
    public static PendingPrune[] Merge(IReadOnlyList<PendingPrune>? existing, IEnumerable<PendingPrune> added) =>
        [.. (existing ?? []).Concat(added)
            .GroupBy(p => CanonicalPath.Resolve(p.Path), StringComparer.Ordinal)
            .Select(g => g.First())];

    /// <summary>Drops the intents whose path the new plan writes again: a document renamed away and
    /// back leaves an intent to delete the very directory that is about to be published.</summary>
    public static PendingPrune[] Reconcile(IReadOnlyList<PendingPrune> journal, IEnumerable<string> liveDestinations) {
        var live = liveDestinations.Select(CanonicalPath.Resolve).ToHashSet(StringComparer.Ordinal);
        return [.. journal.Where(p => !live.Contains(CanonicalPath.Resolve(p.Path)))];
    }
}
```

- [ ] **Step 4: Run the test**

Expected: PASS.

- [ ] **Step 5: Commit**

`Journal the paths a sync still owes a deletion (#778)`

---

### Task 7: Lock names and their order

**Files:**
- Create: `src/Capacitor.Cli.Core/Skills/SkillsLocks.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Skills/SkillsLocksTests.cs`

**Interfaces:**
- Consumes: `ConfigRoot.AcquireLock(string name, TimeSpan? timeout = null)`.
- Produces: `SkillsLocks.Migration` (a constant name), `SkillsLocks.Repository(string repoHash)`, `SkillsLocks.Manifest(string gitDir, string targetKey)`.

- [ ] **Step 1: Write the failing test**

```csharp
public class SkillsLocksTests {
    [Test]
    public async Task The_migration_lock_is_one_key_for_the_whole_machine() {
        // Legacy ownership crosses repositories, so two repositories must not hold two keys.
        await Assert.That(SkillsLocks.Migration).IsEqualTo(SkillsLocks.Migration);
        await Assert.That(SkillsLocks.Repository("aaaa")).IsNotEqualTo(SkillsLocks.Repository("bbbb"));
        await Assert.That(SkillsLocks.Manifest("/a/.git", "claude"))
            .IsNotEqualTo(SkillsLocks.Manifest("/a/.git", "agents"));
        await Assert.That(SkillsLocks.Manifest("/a/.git", "claude"))
            .IsNotEqualTo(SkillsLocks.Manifest("/b/.git", "claude"));
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Expected: FAIL, `SkillsLocks` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace Capacitor.Cli.Core.Skills;

/// <summary>Lock names for skills materialization. Always acquired in this order — migration, then
/// repository, then manifest — and no shared lock is ever held across a network request.</summary>
public static class SkillsLocks {
    /// <summary>One key for the machine: a legacy global directory can be owned by two repositories,
    /// so two keys would let each observe the other as the remaining owner and neither delete it.
    /// </summary>
    public const string Migration = "skills/legacy-migration";

    public static string Repository(string repoHash) => $"skills/{repoHash}/repository";

    public static string Manifest(string gitDir, string targetKey) =>
        $"skills/manifest/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(gitDir)))[..16]}/{targetKey}";
}
```

- [ ] **Step 4: Run the test**

Expected: PASS.

- [ ] **Step 5: Commit**

`Name the three skills locks and fix their order (#778)`

---

### Task 8: The managed exclusion block

**Files:**
- Create: `src/Capacitor.Cli.Core/Skills/SkillsExclusion.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Skills/SkillsExclusionTests.cs`

**Interfaces:**
- Produces: `SkillsExclusion.Apply(string gitCommonDir, string repoRoot, IReadOnlyList<string> relativeRoots)`, `SkillsExclusion.Remove(string gitCommonDir)`.

- [ ] **Step 1: Write the failing test**

```csharp
public class SkillsExclusionTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task The_block_is_idempotent_and_removable() {
        var gitDir = Tmp.CreateDir("main/.git");
        var repo   = Tmp.GetResolvedPath("main");
        Tmp.CreateFile("main/.git/info/exclude", "# existing\n*.log\n");
        var roots  = new[] { Path.Combine(".agents", "skills"), Path.Combine(".claude", "skills") };

        SkillsExclusion.Apply(gitDir, repo, roots);
        var once = File.ReadAllText(Path.Combine(gitDir, "info", "exclude"));
        SkillsExclusion.Apply(gitDir, repo, roots);
        var twice = File.ReadAllText(Path.Combine(gitDir, "info", "exclude"));

        await Assert.That(twice).IsEqualTo(once);
        await Assert.That(once).Contains("*.log");
        await Assert.That(once).Contains("/.agents/skills/kcap-*/");
        await Assert.That(once).Contains("/.claude/skills/kcap-*/");

        SkillsExclusion.Remove(gitDir);
        var removed = File.ReadAllText(Path.Combine(gitDir, "info", "exclude"));

        await Assert.That(removed).Contains("*.log");
        await Assert.That(removed).DoesNotContain("kcap-");
    }

    [Test]
    public async Task Patterns_are_relative_to_the_repository_root() {
        var gitDir = Tmp.CreateDir("main/.git");
        var repo   = Tmp.GetResolvedPath("main");
        var nested = Path.Combine(repo, "sub", "dir");
        Directory.CreateDirectory(nested);

        SkillsExclusion.Apply(gitDir, repo, [Path.Combine(nested, ".agents", "skills")]);

        // An anchor below the root still excludes by a root-relative pattern.
        await Assert.That(File.ReadAllText(Path.Combine(gitDir, "info", "exclude")))
            .Contains("/sub/dir/.agents/skills/kcap-*/");
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Expected: FAIL, `SkillsExclusion` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace Capacitor.Cli.Core.Skills;

/// <summary>One marker-delimited block in the worktree's <c>info/exclude</c>, which Git resolves to
/// the shared common directory, so a single block covers the repository and all its worktrees. The
/// patterns name only kcap's own directories: a repository may have committed skills beside
/// them.</summary>
public static class SkillsExclusion {
    const string Begin = "# kcap skills (managed) — do not edit between these markers";
    const string End   = "# end kcap skills";

    public static void Apply(string gitCommonDir, string repoRoot, IReadOnlyList<string> roots) {
        var patterns = roots.Select(r => "/" + Relative(repoRoot, r).Replace(Path.DirectorySeparatorChar, '/')
                                              .TrimStart('/') + "/kcap-*/");
        Rewrite(gitCommonDir, string.Join('\n', [Begin, .. patterns, End]));
    }

    public static void Remove(string gitCommonDir) => Rewrite(gitCommonDir, null);

    static string Relative(string repoRoot, string root) =>
        Path.IsPathRooted(root) ? Path.GetRelativePath(repoRoot, root) : root;

    static void Rewrite(string gitCommonDir, string? block) {
        var path = Path.Combine(gitCommonDir, "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var start = lines.IndexOf(Begin);
        if (start >= 0) {
            var end = lines.IndexOf(End, start);
            lines.RemoveRange(start, (end < 0 ? lines.Count - 1 : end) - start + 1);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (block is not null) lines.AddRange(block.Split('\n'));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join('\n', lines) + "\n");
        File.Move(tmp, path, overwrite: true);
    }
}
```

- [ ] **Step 4: Run the test**

Expected: PASS.

- [ ] **Step 5: Commit**

`Exclude only kcap's own skill directories from Git (#778)`

---

### Task 9: Legacy global migration

**Files:**
- Create: `src/Capacitor.Cli.Core/Skills/SkillsLegacyMigration.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Skills/SkillsLegacyMigrationTests.cs`

**Interfaces:**
- Consumes: `SkillsManifest`, `SkillsIdentity` (Task 4), `SkillsMaterializer.Prune` (Task 5).
- Produces: `SkillsLegacyMigration.Plan(string configRoot, string repoHash, string targetKey, SkillsIdentity current) -> LegacyMigrationPlan` with `LegacyMigrationPlan(string ManifestPath, IReadOnlyList<string> Delete, IReadOnlyList<string> Keep)`.

- [ ] **Step 1: Write the failing test**

```csharp
public class SkillsLegacyMigrationTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static SkillsIdentity Id(string account) => new() { Account = account, Server = "https://s" };

    void WriteLegacy(string repoHash, string target, string account, params string[] paths) {
        var dir = Tmp.CreateDir($"config/skills/{repoHash}/{target}");
        var manifest = new SkillsManifest {
            Identity = Id(account),
            Skills = [.. paths.Select(p => new SkillsManifestEntry {
                DocId = Guid.NewGuid(), Slug = Path.GetFileName(p)[5..], Version = 1,
                ContentHash = "h", Path = p, FileHash = "f",
            })],
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, CapacitorJsonContext.Default.SkillsManifest));
    }

    [Test]
    public async Task A_path_another_repository_still_owns_is_kept() {
        var shared = Tmp.PathTo("global/kcap-shared");
        var mine   = Tmp.PathTo("global/kcap-mine");
        WriteLegacy("aaaa", "agents", "acct-1", shared, mine);
        WriteLegacy("bbbb", "agents", "acct-1", shared);

        var plan = SkillsLegacyMigration.Plan(Tmp.GetResolvedPath("config"), "aaaa", "agents", Id("acct-1"));

        await Assert.That(plan.Delete).IsEquivalentTo([mine]);
        await Assert.That(plan.Keep).IsEquivalentTo([shared]);
    }

    [Test]
    public async Task A_path_whose_every_owner_is_retired_is_deleted() {
        var shared = Tmp.PathTo("global/kcap-shared");
        WriteLegacy("aaaa", "agents", "acct-1", shared);
        WriteLegacy("bbbb", "agents", "acct-1", shared);

        // Both owners are on the retired account, so nobody serves it under a live credential.
        var plan = SkillsLegacyMigration.Plan(Tmp.GetResolvedPath("config"), "aaaa", "agents", Id("acct-2"));

        await Assert.That(plan.Delete).IsEquivalentTo([shared]);
        await Assert.That(plan.Keep).IsEmpty();
    }

    [Test]
    public async Task A_path_a_live_other_account_owns_survives_a_retirement() {
        var shared = Tmp.PathTo("global/kcap-shared");
        WriteLegacy("aaaa", "agents", "acct-1", shared);
        WriteLegacy("bbbb", "agents", "acct-2", shared);

        var plan = SkillsLegacyMigration.Plan(Tmp.GetResolvedPath("config"), "aaaa", "agents", Id("acct-2"));

        await Assert.That(plan.Keep).IsEquivalentTo([shared]);
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Expected: FAIL, `SkillsLegacyMigration` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace Capacitor.Cli.Core.Skills;

public sealed record LegacyMigrationPlan(
    string ManifestPath, IReadOnlyList<string> Delete, IReadOnlyList<string> Keep);

/// <summary>Retires the user-global copies one repository owns. A global path carries no repository
/// identity, so two repositories can own the same directory — a project-homed skill does exactly
/// that — and deleting one repository's copy would take the other's with it.</summary>
public static class SkillsLegacyMigration {
    public static LegacyMigrationPlan Plan(
            string configRoot, string repoHash, string targetKey, SkillsIdentity current) {
        var mine  = Path.Combine(configRoot, "skills", repoHash, targetKey, "manifest.json");
        var owned = Load(mine)?.Skills?.Select(e => e.Path).ToList() ?? [];
        var delete = new List<string>();
        var keep   = new List<string>();

        foreach (var path in owned) {
            var others = Others(configRoot, mine, path);
            // A remaining owner under the same retired identity is not serving it either.
            var liveOwner = others.Any(m => m.Identity is null || Equals(m.Identity, current)
                                            || !Equals(m.Identity, Load(mine)?.Identity));
            if (others.Count == 0 || !liveOwner) delete.Add(path); else keep.Add(path);
        }
        return new LegacyMigrationPlan(mine, delete, keep);
    }

    static List<SkillsManifest> Others(string configRoot, string minePath, string path) {
        var skills = Path.Combine(configRoot, "skills");
        if (!Directory.Exists(skills)) return [];
        return [.. Directory.EnumerateFiles(skills, "manifest.json", SearchOption.AllDirectories)
            .Where(f => !string.Equals(f, minePath, StringComparison.Ordinal))
            .Select(Load).OfType<SkillsManifest>()
            .Where(m => (m.Skills ?? []).Any(e => string.Equals(e.Path, path, StringComparison.Ordinal)))];
    }

    static SkillsManifest? Load(string path) {
        try {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), CapacitorJsonContext.Default.SkillsManifest)
                : null;
        } catch {
            return null;
        }
    }
}
```

The `liveOwner` expression must read: an owner is live when its identity differs from the identity being retired. Write it as a named local so the test above passes for all three cases:

```csharp
            var retired  = Load(mine)?.Identity;
            var liveOwner = others.Any(m => m.Identity is null || !Equals(m.Identity, retired));
```

- [ ] **Step 4: Run the test**

Expected: PASS, all three cases.

- [ ] **Step 5: Commit**

`Retire a global copy only when no live owner remains (#778)`

---

### Task 10: Wire the command

**Files:**
- Modify: `src/Capacitor.Cli/Commands/SkillsCommand.cs`
- Test: covered by Task 11

**Interfaces:**
- Consumes: everything from Tasks 1 to 9.
- Produces: `SkillsCommand.HandleSync(bool dryRun, bool auto = false)` unchanged in signature; `SkillsCommand.ResolveAnchor(string cwd) -> string?`.

- [ ] **Step 1: Resolve the anchor and the manifest location**

```csharp
    /// <summary>The checkout or linked worktree the session is in. Every write below is relative to
    /// it, and it is a parameter rather than ambient state so a startup adapter can pass a session's
    /// own launch directory.</summary>
    internal static string? ResolveAnchor(string cwd) => GitRepository.FindRoot(cwd);
```

In `HandleSync`, after the existing repository detection:

```csharp
        var anchor = ResolveAnchor(cwd);
        var gitDir = GitRepository.ResolveGitDir(cwd);
        if (anchor is null || gitDir is null) {
            await Console.Error.WriteLineAsync($"Could not resolve this repository's git directory from {cwd}.");
            return 1;
        }
```

and replace the target loop's manifest path with `Path.Combine(gitDir, "kcap", "skills", target.Key + ".json")`, keeping the legacy path (`config.Path("skills", hash, target.Key, "manifest.json")`) as a separate variable for adoption and migration.

- [ ] **Step 2: Apply the transitions before the fetch**

```csharp
        var identity = new SkillsIdentity { Account = accountId, Server = serverUrl };
        var retiring = manifest?.Identity is not null && !Equals(manifest.Identity, identity);
        var moved    = manifest?.Anchor is not null
                       && !string.Equals(CanonicalPath.Resolve(manifest.Anchor), CanonicalPath.Resolve(anchor),
                                         StringComparison.Ordinal);

        if (retiring) {
            // Retirement runs first and unconditionally: a failed replacement fetch must not leave
            // the previous account's files behind.
            foreach (var e in manifest!.Skills ?? []) SkillsMaterializer.Prune(root, anchor, e.Path);
            foreach (var p in manifest.PendingPrunes ?? []) SkillsMaterializer.Prune(p.Root, anchor, p.Path);
            RetireLegacy(hash, target.Key, identity);
            manifest = null;
        } else if (moved) {
            journal  = SkillsJournal.Merge(manifest!.PendingPrunes,
                          (manifest.Skills ?? []).Select(e => new PendingPrune { Path = e.Path, Root = OldRoot(manifest, target) }));
            manifest = manifest with { Skills = [], Etag = null };
        }
```

`OldRoot(manifest, target)` is `target.Root(manifest.Anchor!)`. `accountId` comes from the resolved profile's token subject; if no account is available the sync refuses with the same message as an unauthenticated run.

- [ ] **Step 3: Publish pending state before writing**

```csharp
        var live    = writes.Select(w => SkillsMaterializer.SkillDirFor(root, w.Slug)).ToList();
        journal     = SkillsJournal.Reconcile(SkillsJournal.Merge(journal, plan.Prunes.Select(ToPending)), live);
        SaveManifest(manifestPath, BuildManifest(dto.Etag, snapshot, root, anchor, identity, target,
                                                 pending: true, journal));

        foreach (var w in writes) SkillsMaterializer.Write(root, anchor, w);
        foreach (var p in journal) SkillsMaterializer.Prune(p.Root, anchor, p.Path);

        SaveManifest(manifestPath, BuildManifest(dto.Etag, snapshot, root, anchor, identity, target,
                                                 pending: false, []));
```

- [ ] **Step 4: Run recovery, exclusion and migration on every path, including no-change**

Extract the tail of `SyncTargetAsync` so the `304` branch and the empty-plan branch both reach it:

```csharp
    async Task<int> FinishTargetAsync(string anchor, string gitDir, string hash, SkillsTarget target,
                                      SkillsManifest manifest, string manifestPath) {
        foreach (var p in manifest.PendingPrunes ?? []) SkillsMaterializer.Prune(p.Root, anchor, p.Path);
        using (config.AcquireLock(SkillsLocks.Repository(hash)))
            SkillsExclusion.Apply(GitRepository.ResolveMainRepoRoot(anchor) is var main && main is not null
                                      ? Path.Combine(main, ".git") : gitDir,
                                  anchor, [.. Targets().Select(t => t.RelativePath)]);
        using (config.AcquireLock(SkillsLocks.Migration)) MigrateLegacy(hash, target, anchor);
        SaveManifest(manifestPath, manifest with { Pending = false, PendingPrunes = [] });
        return 0;
    }
```

- [ ] **Step 5: Report contention instead of counting it as done**

In auto mode, a lock the run needed but could not take returns a distinct non-zero result and prints nothing; a recovery or retirement is attempted before `AutoThrottled` is consulted, so the throttle only suppresses a periodic refresh.

- [ ] **Step 6: Build and commit**

Run: `dotnet build Capacitor.slnx`
Expected: no errors.
Commit: `Materialize a repository's skills into the repository (#778)`

---

### Task 11: The command path end to end

**Files:**
- Create: `test/Capacitor.Cli.Tests.Unit/Commands/SkillsSyncFlowTests.cs`

**Interfaces:**
- Consumes: `IRepositoriesApi` (a test double returning `SkillsSnapshotResult`), `SkillsCommand`.

- [ ] **Step 1: Write the failing tests**

Cover, one test each: a first sync writes into the checkout and nothing into the home directory; two worktrees of one repository materialize independently with independent manifests; a second repository sees none of the first's skills; a hand-authored skill beside kcap's survives a prune; a crash between the pending save and the prune, with the document renamed in between, prunes the orphan on retry; the same crash with the document renamed back leaves the live copy; an account change prunes locally and retires the legacy copies before a replacement fetch that then fails; an anchor change with an identical snapshot and etag materializes the new paths and removes the old; a `304` still clears a pending flag, refreshes the exclusion block and migrates.

Each test builds a temporary repository with `TempDir`, stubs `IRepositoriesApi`, and asserts on the filesystem and the manifest rather than on log output.

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SkillsSyncFlowTests/*"`
Expected: FAIL.

- [ ] **Step 3: Fix what they catch in Task 10's wiring**

No new production types; every failure is a defect in the wiring.

- [ ] **Step 4: Run the whole solution's tests**

Run: `dotnet test --solution Capacitor.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

`Cover the skills sync flow end to end (#778)`

---

### Task 12: Measure the compatibility root

The spec's evidence covers `.agents/skills` for reading but not for Git exclusion or launch-directory anchoring, which were measured at each harness's own root.

**Files:**
- Modify: `docs/probes/2026-09-16-skills-discovery/probe.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/findings.md`, `matrix.json`, `capability-matrix.md`

- [ ] **Step 1: Add a root override to the two scenarios**

`arm_s3` and `arm_s8` already take a root parameter internally; expose `--root <relative>` on `probe.py` so a sweep can run them against `.agents/skills`.

- [ ] **Step 2: Sweep**

Run, for codex, copilot, cursor, opencode-v1 and pi:
`python3 docs/probes/2026-09-16-skills-discovery/probe.py --harness <entry> --mode print --scenario S3 --scenario S8 --root .agents/skills --turn`

- [ ] **Step 3: Regenerate and record**

Run `probe.py --emit` and `report.py`, then add one paragraph to `findings.md` stating whether both properties hold at the compatibility root.

- [ ] **Step 4: Commit**

`Measure exclusion and nested launch at the shared skills root (#961)`

---

### Task 13: Document where skills now land

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update both sections**

The quick start and the `kcap skills sync` entry under CLI commands both say skills install into user-global trees. State that a sync writes into the current checkout or worktree, that the files are excluded through the repository's `info/exclude`, that existing global copies migrate on the first sync, and that a Claude-restricted skill is readable by Copilot, Cursor and OpenCode working in the same checkout.

- [ ] **Step 2: Commit**

`Say where kcap skills sync writes (#778)`

---

## Self-review

**Spec coverage.** Destinations, Task 3. The anchor and its transition, Tasks 10 and 11. Containment, Tasks 1 and 5. Identity and ownership, Tasks 4, 9, 10, 11. Crash recovery including the journal and its reconciliation, Tasks 5, 6, 10, 11. Serialization, Tasks 7 and 10. Reconciliation and migration, Tasks 9 and 10. Git exclusion, Task 8. Failure modes, Tasks 5 and 10. Testing, Tasks 1 to 11. The evidence gap, Task 12. The README rule from CLAUDE.md, Task 13.

**Type consistency.** `SkillsTarget.Root(anchor)` is used in Tasks 3, 9 and 10. `SkillsMaterializer.Write/Prune` take `(root, anchor, …)` in Tasks 5, 10 and 11. `PendingPrune(Path, Root)` is written in Task 4 and consumed in Tasks 6 and 10. `SkillsIdentity(Account, Server)` is written in Task 4 and consumed in Tasks 9 and 10. `SkillsLocks` names are produced in Task 7 and consumed in Task 10.

**Known gap for the implementer.** Task 10 assumes an account identifier is reachable from the resolved profile. If the token store exposes no subject, the first step of Task 10 is to add one, and the refusal path is the same as an unauthenticated sync.
