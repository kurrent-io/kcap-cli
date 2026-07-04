# Nested GitLab Groups in PR/MR Detection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Support GitLab **nested groups** (`group/subgroup/project`) end-to-end in PR/MR detection and `kcap review`, the deferred fast-follow (§6b) of the multi-provider work (#229 / AI-1118).

**Architecture:** Nested support is the "single scope, everywhere" flip promised in the multi-provider design (§3): the CLI parses a multi-segment namespace as `owner = everything-before-the-last-segment`, `repo = last-segment` (so `group/subgroup/project` → owner `group/subgroup`, repo `project`), and the server keeps `repo_hash` consistent by URL-decoding the owner/repo route values before hashing. **Empirically validated (.NET 10, minimal Kestrel spike, 2026-07-03): `%2F` in a named route segment is accepted (HTTP 200), is NOT split, and arrives still-encoded in the route value — so NO route-template change is needed; the server just calls `Uri.UnescapeDataString` on `owner`/`repo`.** This overturns the design's §6b worry that `%2F` is "commonly rejected."

**Tech Stack:** .NET 10 (NativeAOT CLI), TUnit + WireMock (CLI tests), TUnit + Testcontainers + WebApplicationFactory (server tests), ASP.NET Core minimal APIs (server routes).

## Global Constraints

- `repo_hash` stays **host-agnostic**: `ComputeRepoHash(owner, repo) = SHA256("{owner}/{repo}".ToLowerInvariant())[..16]`. The CLI (`Capacitor.Cli.Core/RepoHashHelper.cs`) and server (`Capacitor.Server.Core/RepoHashHelper.cs`) implementations are byte-identical — do NOT diverge them.
- The `owner`/`repo` split rule is **identical at detection-time and query-time**: `owner` = all path segments before the last, `repo` = the last segment. Nested applies to **GitLab MR URLs and git remotes only**. GitHub PR URLs and the `owner/repo#123` shorthand stay **single-level** (existing safety rule — a `/` in the shorthand repo group is a malformed API path segment).
- AOT: after CLI changes run `dotnet publish -c Release` and confirm **no new IL3050/IL2026** warnings.
- README must change in the **same PR** as any user-facing CLI surface change (repo policy).
- Two repos, two PRs. **kcap-cli PR merges first**, then the kcap-server PR bumps the `src/cli` submodule. The server route change is independent of CLI compile, so the two can be developed in parallel worktrees.
- PR descriptions reference both the GitHub issue (`Closes #231`) and Linear (`AI-1121`).

---

## Track A — CLI (kcap-cli), PR 1

Worktree: `src/cli/.claude/worktrees/ai-1121-nested-gitlab-groups`, branch `worktree-ai-1121-nested-gitlab-groups`, based on cli `origin/main`.

### Task A1: `GitUrlParser.ParseRemoteUrl` — multi-segment owner

**Files:**
- Modify: `src/Capacitor.Cli.Core/Models.cs` (the `GitUrlParser` regexes, ~L625-632)
- Test: `test/Capacitor.Cli.Tests.Unit/GitUrlParserTests.cs`

**Interfaces:**
- Produces: `GitUrlParser.ParseRemoteUrl(string?) → (string? Owner, string? RepoName)`. For `gitlab.com/group/subgroup/project(.git)` returns `("group/subgroup", "project")`; single-level and invalid inputs behave exactly as before.

- [ ] **Step 1: Flip the existing nested-rejection test + add HTTPS/SSH nested cases.** In `GitUrlParserTests.cs`, rename `ParseRemoteUrl_SshProtoUrl_NestedGroup_ReturnsBothNull` (L55-61) to `ParseRemoteUrl_SshProtoUrl_NestedGroup_ReturnsMultiSegmentOwner` and change its assertions:

```csharp
    [Test]
    public async Task ParseRemoteUrl_SshProtoUrl_NestedGroup_ReturnsMultiSegmentOwner() {
        var (owner, repoName) = GitUrlParser.ParseRemoteUrl("ssh://git@gitlab.com/group/sub/project.git");

        await Assert.That(owner).IsEqualTo("group/sub");
        await Assert.That(repoName).IsEqualTo("project");
    }
```

Add nested rows to the HTTPS `[Arguments]` block (after L18) and SSH block (after L37):

```csharp
    [Arguments("https://gitlab.com/group/subgroup/project", "group/subgroup", "project")]
    [Arguments("https://gitlab.com/group/subgroup/project.git", "group/subgroup", "project")]
    [Arguments("https://gitlab.com/a/b/c/deep-project.git", "a/b/c", "deep-project")]
```
```csharp
    [Arguments("git@gitlab.com:group/subgroup/project.git", "group/subgroup", "project")]
    [Arguments("git@gitlab.com:a/b/c/deep-project", "a/b/c", "deep-project")]
```

- [ ] **Step 2: Run tests, verify the nested cases FAIL.**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/GitUrlParserTests/*"`
Expected: FAIL — nested rows return `(null, null)` / the flipped test's assertions fail.

- [ ] **Step 3: Change the owner group from `[^/]+` to `.+` in all three regexes** in `Models.cs`:

```csharp
    [GeneratedRegex(@"https?://[^/]+/(?<owner>.+)/(?<repo>[^/]+?)(?:\.git)?$")]
    internal static partial Regex HttpsRegex();

    [GeneratedRegex(@"git@[\w.-]+:(?<owner>.+)/(?<repo>[^/]+?)(?:\.git)?$")]
    internal static partial Regex SshRegex();

    [GeneratedRegex(@"ssh://(?:[^@/]+@)?[^/]+/(?<owner>.+)/(?<repo>[^/]+?)(?:\.git)?$")]
    internal static partial Regex SshProtoRegex();
```

- [ ] **Step 4: Run tests, verify PASS** (including the existing single-level, `.git`, `a.b.c`, and `InvalidUrls`/`Null` cases — `ftp://`, `https://`, `not-a-url` must still return `(null, null)`).

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/GitUrlParserTests/*"`
Expected: PASS (all rows).

- [ ] **Step 5: Commit.**

```bash
git add src/Capacitor.Cli.Core/Models.cs test/Capacitor.Cli.Tests.Unit/GitUrlParserTests.cs
git commit -m "feat: parse multi-segment (nested GitLab group) git remotes in GitUrlParser"
```

### Task A2: `PrRefParser` — multi-segment GitLab MR URL

**Files:**
- Modify: `src/Capacitor.Cli.Core/Commands/PrRefParser.cs` (the `GitLabUrlPattern` regex, ~L47)
- Test: `test/Capacitor.Cli.Tests.Unit/PrRefParserTests.cs`

**Interfaces:**
- Produces: `PrRefParser.TryParse(input, out owner, out repo, out prNumber)`. A nested GitLab MR URL yields `owner="group/sub"`, `repo="project"`. GitHub URLs and shorthand stay single-level.

- [ ] **Step 1: Flip the rejection test + add nested + suffix cases.** In `PrRefParserTests.cs`, replace `Gitlab_nested_group_mr_url_is_rejected_in_first_pass` (L104-107):

```csharp
    [Test]
    public async Task Gitlab_nested_group_mr_url_is_parsed() {
        var ok = PrRefParser.TryParse("https://gitlab.com/group/sub/project/-/merge_requests/42",
                                      out var owner, out var repo, out var pr);
        await Assert.That(ok).IsTrue();
        await Assert.That(owner).IsEqualTo("group/sub");
        await Assert.That(repo).IsEqualTo("project");
        await Assert.That(pr).IsEqualTo(42);
    }

    [Test]
    public async Task Gitlab_deeply_nested_mr_url_with_suffix_is_parsed() {
        var ok = PrRefParser.TryParse("https://gitlab.com/a/b/c/proj/-/merge_requests/7/diffs",
                                      out var owner, out var repo, out var pr);
        await Assert.That(ok).IsTrue();
        await Assert.That(owner).IsEqualTo("a/b/c");
        await Assert.That(repo).IsEqualTo("proj");
        await Assert.That(pr).IsEqualTo(7);
    }

    [Test]
    public async Task Nested_shorthand_is_still_rejected() {
        // Safety rule preserved: a '/' in the shorthand repo group stays rejected.
        await Assert.That(PrRefParser.TryParse("group/sub/project#42", out _, out _, out _)).IsFalse();
    }
```

- [ ] **Step 2: Run tests, verify the new nested cases FAIL** (and `Nested_shorthand_is_still_rejected` already passes).

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/PrRefParserTests/*"`
Expected: FAIL on the two nested-parse tests.

- [ ] **Step 3: Change only the GitLab MR owner group from `[^/]+` to `.+`** in `PrRefParser.cs`. Leave `GitHubUrlPattern` and `ShorthandPattern` unchanged:

```csharp
    // GitLab MR URL. Nested groups supported (§6b / AI-1121): owner is the full
    // namespace path before the project; project is the last segment.
    // Same trailing-suffix tolerance so /diffs, /commits, ?query, #note parse.
    [GeneratedRegex(@"^https?://[^/]+/(.+)/([^/]+)/-/merge_requests/(\d+)(?:[/?#].*)?$")]
    private static partial Regex GitLabUrlPattern();
```

- [ ] **Step 4: Run tests, verify PASS** (nested + existing single-level GitLab L87/L97, GitHub, shorthand, and empty-number rejection L115 all green).

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/PrRefParserTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/Capacitor.Cli.Core/Commands/PrRefParser.cs test/Capacitor.Cli.Tests.Unit/PrRefParserTests.cs
git commit -m "feat: accept nested GitLab group MR URLs in PrRefParser"
```

### Task A3: GitLab detector + daemon RepoMatcher — lock-in tests (no production change)

Both already handle nested transparently once `owner` carries slashes (`GitLabPrDetector` does `Uri.EscapeDataString($"{owner}/{repo}")`; `RepoMatcher` compares `PathAfterHost(remote)` against `$"{owner}/{repo}"`). Add regression tests so a future refactor can't silently drop nested support.

**Files:**
- Test: `test/Capacitor.Cli.Tests.Unit/GitLabPrDetectorTests.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/RepoMatcherTests.cs`

- [ ] **Step 1: Add a GitLabPrDetector nested-encoding test.** Mirror the existing request-building test in `GitLabPrDetectorTests.cs` (find the test that captures the `glab` args via a fake `CommandRunner`) with `owner="group/sub"`, `repo="proj"`, and assert the args contain `projects/group%2Fsub%2Fproj/merge_requests`.

- [ ] **Step 2: Add a RepoMatcher nested-match test.** Mirror the existing single-level match test in `RepoMatcherTests.cs` with an origin remote `https://gitlab.com/group/sub/proj.git` on a temp git repo, calling `FindAsync("group/sub", "proj", …)` and asserting the git root is returned. (If setting up an on-disk temp git repo is heavy, instead assert the pure matcher: `RemoteMatcher.PathAfterHost(RemoteMatcher.NormalizeRemoteUrl("git@gitlab.com:group/sub/proj.git"))` equals `"group/sub/proj"`, which is exactly what `FindAsync` compares `$"{owner}/{repo}"` against — put this in `RemoteMatcherTests.cs` if it fits better there.)

- [ ] **Step 3: Run both suites, verify PASS** (no production change, so they should pass immediately — that's the point; if either fails, a real gap exists, fix it).

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/GitLabPrDetectorTests/*"`
Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/RepoMatcherTests/*"`
Expected: PASS.

- [ ] **Step 4: Commit.**

```bash
git add test/Capacitor.Cli.Tests.Unit/GitLabPrDetectorTests.cs test/Capacitor.Cli.Tests.Unit/RepoMatcherTests.cs
git commit -m "test: lock in nested-group support in GitLab detector + daemon RepoMatcher"
```

### Task A4: Detection cache — bump schema version to discard stale null-owner entries

A nested repo cached under v2 has `owner=null` (old parser failed) and is served without reparsing for up to the 1h TTL. Bump the version so upgraded clients re-derive.

**Files:**
- Modify: `src/Capacitor.Cli/RepositoryDetection.cs:15` (`CacheSchemaVersion`)
- Test: `test/Capacitor.Cli.Tests.Unit/RepositoryDetectionCacheTests.cs`

- [ ] **Step 1: Add a test that a v2 entry is now stale.** In `RepositoryDetectionCacheTests.cs`:

```csharp
    [Test]
    public async Task GitCacheEntry_v2_is_stale_after_nested_group_bump() {
        // v2 pre-dates multi-segment owner parsing; a nested repo cached under v2 has owner=null.
        var json  = """{"schema_version":2,"host":"gitlab.com","owner":null,"repo_name":null}""";
        var entry = System.Text.Json.JsonSerializer.Deserialize<GitCacheEntry>(json, /* same options the file uses */);
        await Assert.That(entry!.SchemaVersion == RepositoryDetection.CacheSchemaVersion).IsFalse();
    }
```
(Match the exact deserialization the file's existing tests use — copy the pattern from `GitCacheEntry_without_schema_version_deserializes_as_stale`.)

- [ ] **Step 2: Run, verify FAIL** (v2 currently equals the version).

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/RepositoryDetectionCacheTests/*"`
Expected: FAIL.

- [ ] **Step 3: Bump the version** in `RepositoryDetection.cs`:

```csharp
    // Bump whenever the cached shape/derivation changes so stale entries are ignored.
    // v2: added Host + provider-aware parsing.
    // v3: multi-segment owner parsing (nested GitLab groups, AI-1121).
    internal const int CacheSchemaVersion = 3;
```

- [ ] **Step 4: Run, verify PASS** (new test + the two existing version tests, which reference the symbol, stay green).

- [ ] **Step 5: Commit.**

```bash
git add src/Capacitor.Cli/RepositoryDetection.cs test/Capacitor.Cli.Tests.Unit/RepositoryDetectionCacheTests.cs
git commit -m "fix: bump detection cache schema to v3 so nested repos re-derive on upgrade"
```

### Task A5: Docs (README + help) + full suite + AOT gate

**Files:**
- Modify: `README.md:277` (the `kcap review` accepted-formats paragraph)
- Modify: `src/Capacitor.Cli.Core/Resources/help-review.txt` (add a nested example)
- Design doc: `docs/superpowers/specs/2026-07-01-multi-provider-pr-detection-design.md` (mark §3/§6b nested as landed + note the empirical `%2F` finding)

- [ ] **Step 1: Update README:277.** Replace the "Only single-level … aren't recognized yet." sentence with:

```markdown
Accepts a GitHub PR URL (`https://github.com/owner/repo/pull/123`, any host including GitHub Enterprise), a GitLab MR URL (`https://gitlab.com/owner/repo/-/merge_requests/123`, including nested groups such as `https://gitlab.com/group/subgroup/repo/-/merge_requests/123`), or the shorthand `owner/repo#123` (single-level only).
```

- [ ] **Step 2: Add a nested example to `help-review.txt`** under the existing GitLab example (L10):

```
  kcap review https://gitlab.com/group/subgroup/repo/-/merge_requests/123
```

- [ ] **Step 3: Update the design doc** — in `2026-07-01-multi-provider-pr-detection-design.md`, add a short note at §3, §6b, and Open decision #2 that nested groups landed in AI-1121, and that the `%2F` empirical test (see §6b) showed .NET 10 accepts `%2F` in a route segment (still-encoded route value → `Uri.UnescapeDataString` on the server; no route-template change).

- [ ] **Step 4: Run the FULL unit suite.**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: all green.

- [ ] **Step 5: AOT publish gate.**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo "no AOT warnings"`
Expected: `no AOT warnings`.

- [ ] **Step 6: Commit + open PR.**

```bash
git add README.md src/Capacitor.Cli.Core/Resources/help-review.txt docs/superpowers/
git commit -m "docs: nested GitLab group support in kcap review (README + help + design)"
git push -u origin worktree-ai-1121-nested-gitlab-groups
gh pr create --repo kurrent-io/kcap-cli --title "Support nested GitLab groups in PR/MR detection" --body "…Closes #231 … AI-1121"
```

---

## Track B — Server (kcap-server), PR 2

Worktree: `.claude/worktrees/ai-1121-server`, branch `worktree-ai-1121-nested-gitlab-groups`, based on server `origin/main`. Independent of Track A for compile/test.

### Task B1: Decode owner/repo route values in `ReviewApiHandlers`

**Files:**
- Modify: `src/Capacitor.Server/Review/ReviewApiHandlers.cs` (all handlers except `GetSessionTranscript`)
- Test: `test/Capacitor.Server.Tests.Read/ReadModels/ReviewApiHandlersNestedTests.cs` (new)

**Interfaces:**
- Produces: each `owner`/`repo`-keyed handler decodes its route values (`Uri.UnescapeDataString`) before `RepoHashHelper.ComputeRepoHash` and before echoing them in the response, so a nested owner arriving as `group%2Fsub` hashes identically to the CLI's detection-time `ComputeRepoHash("group/sub", "proj")`.

- [ ] **Step 1: Write the failing test.** New file, mirroring `ReviewQueriesTests` seeding (`PostgresTestDb.CreateDbAsync`, `InsertRepository/InsertSession/InsertSessionPr`). Seed under the **nested** hash, then call the handler with the **encoded** owner and assert it finds the sessions (i.e. it decoded before hashing):

```csharp
using Capacitor.ReadModels;
using Capacitor.Review;
using Capacitor.Server.TestHelpers.Fixtures;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Capacitor.Server.Tests.Read.ReadModels;

public class ReviewApiHandlersNestedTests {
    [Test]
    public async Task GetPrSummary_decodes_nested_owner_and_matches_detection_time_hash() {
        var (db, lease) = await PostgresTestDb.CreateDbAsync();
        try {
            // repo_hash as the CLI computes it at detection time for a nested group.
            var repoHash = RepoHashHelper.ComputeRepoHash("group/sub", "proj");
            SeedRepoWithPr(db, repoHash, owner: "group/sub", repoName: "proj", pr: 5, sessionId: "s-nested-1");

            var queries = new ReviewQueries(db);
            // ASP.NET binds %2F in a segment as a still-encoded route value.
            var result  = await ReviewApiHandlers.GetPrSummary("group%2Fsub", "proj", 5, queries);

            await Assert.That(result).IsTypeOf<Ok<...>>(); // adjust to the handler's Ok<T>; or execute + assert 200
        } finally {
            await lease.DisposeAsync();
        }
    }
    // + SeedRepoWithPr helper copied from ReviewQueriesTests inserts.
}
```
Assert on the result: prefer asserting the handler returns an `Ok` result carrying the seeded session (not `NotFound`). If asserting the anonymous `Ok<T>` type is awkward, execute the `IResult` against a `DefaultHttpContext` and assert `StatusCode == 200`. (Confirm the exact `ReviewQueries` ctor + `GetPrSessionsAsync` shape from `ReviewQueriesTests.cs`.)

- [ ] **Step 2: Run, verify FAIL** — the handler currently hashes the raw `"group%2Fsub"`, mismatching the seeded hash → `NotFound`.

Run: `dotnet run --project test/Capacitor.Server.Tests.Read/Capacitor.Server.Tests.Read.csproj --treenode-filter "/*/*/ReviewApiHandlersNestedTests/*"`
Expected: FAIL (NotFound / wrong result type).

- [ ] **Step 3: Decode in each handler.** In `ReviewApiHandlers.cs`, at the top of `GetPrSummary`, `GetPrFiles`, `GetPrFileContext`, `SearchPrContext`, `GetPrSessions`, decode both route values and use the decoded values for the hash AND the echoed response:

```csharp
        owner = Uri.UnescapeDataString(owner);
        repo  = Uri.UnescapeDataString(repo);
        var repoHash = RepoHashHelper.ComputeRepoHash(owner, repo);
```
(`GetSessionTranscript` takes only `sessionId` — leave it.)

- [ ] **Step 4: Run, verify PASS.**

Run: `dotnet run --project test/Capacitor.Server.Tests.Read/Capacitor.Server.Tests.Read.csproj --treenode-filter "/*/*/ReviewApiHandlersNestedTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/Capacitor.Server/Review/ReviewApiHandlers.cs test/Capacitor.Server.Tests.Read/ReadModels/ReviewApiHandlersNestedTests.cs
git commit -m "fix: URL-decode owner/repo in review handlers so nested GitLab groups hash consistently"
```

### Task B2: HTTP-level regression test (routing + decode end-to-end)

Proves `%2F` survives ASP.NET routing to the real endpoint (guards against a future route refactor) — the empirical spike, pinned as a test.

**Files:**
- Test: `test/Capacitor.Server.Tests.Read/` (new HTTP test) — reuse the `db.CreateFactory()` + `CreateClient()` pattern from `test/Capacitor.Server.Tests.Evals/V2RouteTests.cs`.

- [ ] **Step 1: Write the test.** Seed a session tagged to a nested repo so the Postgres read model has review rows under `ComputeRepoHash("group/sub","proj")` (seed via the same event/import path other read-model HTTP tests use; confirm the factory projects to Postgres). Then `GET /api/review/{Uri.EscapeDataString("group/sub")}/proj/pulls/{n}`, assert `200 OK` and that the response `owner` echoes `group/sub` (decoded).

- [ ] **Step 2: Run, verify it PASSES** with Task B1's decode in place (and would FAIL without it / if the route rejected `%2F`).

Run: `dotnet run --project test/Capacitor.Server.Tests.Read/Capacitor.Server.Tests.Read.csproj --treenode-filter "/*/*/<NewHttpTestClass>/*"`
Expected: PASS.

- [ ] **Step 3: Run the full Read suite.**

Run: `dotnet run --project test/Capacitor.Server.Tests.Read/Capacitor.Server.Tests.Read.csproj`
Expected: all green.

- [ ] **Step 4: Commit.**

```bash
git add test/Capacitor.Server.Tests.Read/
git commit -m "test: HTTP-level regression that %2F nested owner routes + decodes end-to-end"
```

### Task B3: Submodule bump + PR (after Track A merges)

- [ ] **Step 1:** After the kcap-cli PR merges, bump `src/cli` to the merge commit:

```bash
git -C src/cli fetch origin && git -C src/cli checkout origin/main
git add src/cli
git commit -m "chore: bump kcap CLI submodule to nested-GitLab-group detection"
```

- [ ] **Step 2:** Push + open the server PR referencing `AI-1121` and the cli PR. Note in the body: **no route-template change** (empirical `%2F` finding), server change is decode-only, and the reverse-proxy caveat (if a prod proxy rejects raw `%2F`, the CLI can switch to double-encoding with zero server change).

---

## Self-Review notes

- **Spec coverage:** GitUrlParser (A1), PrRefParser (A2), GitLabPrDetector + RepoMatcher §5 (A3), cache §3 (A4), docs §7 (A5), server route §6b + repo_hash consistency §6a-consistency (B1/B2). All issue bullets covered.
- **`%2F` decision:** resolved empirically (accepted, decode-only) — recorded in memory `aspnet-percent2f-route-value` and the design doc update (A5-3).
- **Reverse-proxy risk:** documented in B3; not blocking (double-encode is a CLI-only fallback needing no server change, since one `UnescapeDataString` handles both single- and double-encoded input).
