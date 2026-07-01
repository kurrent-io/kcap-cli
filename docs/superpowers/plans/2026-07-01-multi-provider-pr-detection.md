# Multi-provider PR/MR detection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend kcap's PR detection beyond GitHub so that GitLab.com sessions are auto-tagged with their merge request and `kcap review <gitlab-url>` works, by delegating to the `gh`/`glab` CLIs — with no kcap-managed tokens and no server changes.

**Architecture:** Resolution stays client-side. A remote's host selects a provider detector behind an `IPrDetector`-style seam: GitHub via the existing `gh pr view --json`, GitLab via `glab api` (raw JSON passthrough using glab's own auth). The PR/MR is only a grouping key (`owner/repo/number`) the server already accepts, so nothing server-side changes. Nested GitLab namespaces are out of scope for this pass (single-level `owner/repo` only).

**Tech Stack:** .NET 10, NativeAOT, System.Text.Json (`JsonNode` for reflection-free parsing + source-gen `CapacitorJsonContext`), TUnit on Microsoft Testing Platform.

**Design spec:** `docs/superpowers/specs/2026-07-01-multi-provider-pr-detection-design.md`

## Global Constraints

- **No kcap-managed provider tokens.** Only shell out to `gh`/`glab`, which own their own auth. Never read, store, or pass a provider token.
- **Best-effort detection.** Missing/unauthenticated CLI, unknown host, non-git dir, malformed JSON, empty branch, or timeout → return without PR fields (untagged session), never throw. Preserve the `try/catch → return null` contract already in `DetectRepositoryAsync`.
- **Single-level namespaces only.** `owner/repo` with no embedded slash. GitLab nested groups (`group/subgroup/project`) are explicitly deferred — such remotes/URLs must not parse or tag.
- **AOT clean.** After changes, `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` must be empty. Parse JSON with `JsonNode` (reflection-free) or the source-generated `CapacitorJsonContext`; never build a `JsonArray` with a collection expression.
- **TUnit run/filter.** Run unit tests with `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/<ClassName>/*"`. Never `--filter`; the bare `"*Class*"` glob matches zero tests.
- **Commit trailer.** Every commit message ends with a blank line then:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- **README/help sync.** Any user-facing CLI surface change updates `README.md` in the same PR (see Task 9 for when GitLab docs land).

---

## File Structure

**Phase 1 — dependency-free groundwork (no user-facing GitLab surface):**
- `src/Capacitor.Cli.Core/Commands/PrRefParser.cs` — modify: generalize URL parsing to any host + GitLab MR URLs; keep shorthand single-level.
- `src/Capacitor.Cli.Core/Config/RemoteMatcher.cs` — add `ExtractHost` / `PathAfterHost` pure helpers.
- `src/Capacitor.Cli.Daemon/Services/RepoMatcher.cs` — modify: host-agnostic `owner/repo`-suffix match.
- `src/Capacitor.Cli.Core/Models.cs` — modify: add `Host` to `RepositoryPayload` and `GitCacheEntry`; add `SchemaVersion` to `GitCacheEntry`.
- `src/Capacitor.Cli/RepositoryDetection.cs` — modify: compute host; version-guard the cache; emit `host`.

**Phase 2 — auto-detection (adds `glab` soft dep; user-visible):**
- `src/Capacitor.Cli/PrDetection/PrDetector.cs` — create: `PrInfo`, `CommandRunner`, `GitHubPrDetector`.
- `src/Capacitor.Cli/PrDetection/GitLabPrDetector.cs` — create: `glab api` MR detection + selection.
- `src/Capacitor.Cli/PrDetection/GitProviderRouter.cs` — create: host → provider kind, custom-host probe, per-process memo.
- `src/Capacitor.Cli/RepositoryDetection.cs` — modify: replace inline `gh` call with router + detectors; effective provider budget.
- `README.md`, `src/Capacitor.Cli.Core/Resources/help-*.txt`, `src/Capacitor.Cli/Commands/ReviewCommand.cs` — GitLab docs/usage.

**Tests:** all under `test/Capacitor.Cli.Tests.Unit/` (references all three assemblies with `InternalsVisibleTo`).

---

## Phase 1 — Groundwork

### Task 1: Generalize `PrRefParser` to GitHub-any-host + GitLab MR URLs

**Files:**
- Modify: `src/Capacitor.Cli.Core/Commands/PrRefParser.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/PrRefParserTests.cs`

**Interfaces:**
- Produces: `PrRefParser.TryParse(string input, out string owner, out string repo, out int prNumber) → bool` (signature unchanged). `owner`/`repo` remain single-segment.

- [ ] **Step 1: Write the failing tests**

Add to `PrRefParserTests.cs`:

```csharp
[Test]
public async Task Github_url_on_enterprise_host_parses() {
    var ok = PrRefParser.TryParse("https://ghe.corp.com/team/app/pull/7", out var owner, out var repo, out var pr);
    await Assert.That(ok).IsTrue();
    await Assert.That(owner).IsEqualTo("team");
    await Assert.That(repo).IsEqualTo("app");
    await Assert.That(pr).IsEqualTo(7);
}

[Test]
public async Task Gitlab_mr_url_parses() {
    var ok = PrRefParser.TryParse("https://gitlab.com/group/project/-/merge_requests/42", out var owner, out var repo, out var pr);
    await Assert.That(ok).IsTrue();
    await Assert.That(owner).IsEqualTo("group");
    await Assert.That(repo).IsEqualTo("project");
    await Assert.That(pr).IsEqualTo(42);
}

[Test]
public async Task Gitlab_mr_url_with_browser_suffix_parses() {
    foreach (var suffix in new[] { "/diffs", "/commits", "?tab=1", "#note_9" }) {
        var ok = PrRefParser.TryParse($"https://gitlab.com/group/project/-/merge_requests/42{suffix}", out _, out _, out var pr);
        await Assert.That(ok).IsTrue();
        await Assert.That(pr).IsEqualTo(42);
    }
}

[Test]
public async Task Gitlab_nested_group_mr_url_is_rejected_in_first_pass() {
    // Multi-segment namespace deferred (§3/§6b): server routes are /{owner}/{repo}.
    var ok = PrRefParser.TryParse("https://gitlab.com/group/sub/project/-/merge_requests/42", out _, out _, out _);
    await Assert.That(ok).IsFalse();
}
```

Also **replace** the existing `Garbage_input_rejected` test — its `gitlab.com/.../pull/1` line now parses (host-agnostic `/pull/`), so change that assertion:

```csharp
[Test]
public async Task Garbage_input_rejected() {
    await Assert.That(PrRefParser.TryParse("not-a-pr-ref", out _, out _, out _)).IsFalse();
    // A GitHub-shaped /pull/ URL now parses on any host (see Github_url_on_enterprise_host_parses);
    // reject genuinely malformed refs instead.
    await Assert.That(PrRefParser.TryParse("https://gitlab.com/owner/repo/-/merge_requests/", out _, out _, out _)).IsFalse();
    await Assert.That(PrRefParser.TryParse("https://gitlab.com/owner/repo/issues/3", out _, out _, out _)).IsFalse();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PrRefParserTests/*"`
Expected: FAIL — new GitHub-enterprise/GitLab cases fail (regex still requires `github.com` and has no MR pattern).

- [ ] **Step 3: Implement — drop the `github.com` literal and add a GitLab MR pattern**

Replace the two GitHub-specific regexes and add a GitLab one. New body of `PrRefParser`:

```csharp
public static bool TryParse(string input, out string owner, out string repo, out int prNumber) {
    owner = ""; repo = ""; prNumber = 0;
    if (string.IsNullOrWhiteSpace(input)) return false;
    input = input.Trim();

    var gh = GitHubUrlPattern().Match(input);
    if (gh.Success) {
        owner = gh.Groups[1].Value; repo = gh.Groups[2].Value;
        prNumber = int.Parse(gh.Groups[3].Value);
        return true;
    }

    var gl = GitLabUrlPattern().Match(input);
    if (gl.Success) {
        owner = gl.Groups[1].Value; repo = gl.Groups[2].Value;
        prNumber = int.Parse(gl.Groups[3].Value);
        return true;
    }

    var shortMatch = ShorthandPattern().Match(input);
    if (shortMatch.Success) {
        owner = shortMatch.Groups[1].Value; repo = shortMatch.Groups[2].Value;
        prNumber = int.Parse(shortMatch.Groups[3].Value);
        return true;
    }

    return false;
}

// GitHub-style PR URL on ANY host (github.com or GitHub Enterprise). owner/repo are
// single-segment; trailing path/query/fragment (browser copies) tolerated.
[GeneratedRegex(@"^https?://[^/]+/([^/]+)/([^/]+)/pull/(\d+)(?:[/?#].*)?$")]
private static partial Regex GitHubUrlPattern();

// GitLab MR URL. Single-level owner/repo only (nested groups deferred, §3/§6b).
// Same trailing-suffix tolerance so /diffs, /commits, ?query, #note parse.
[GeneratedRegex(@"^https?://[^/]+/([^/]+)/([^/]+)/-/merge_requests/(\d+)(?:[/?#].*)?$")]
private static partial Regex GitLabUrlPattern();

// Shorthand owner/repo#123 — single-level, unchanged. Repo forbids '/'.
[GeneratedRegex(@"^([^/]+)/([^/#]+)#(\d+)$")]
private static partial Regex ShorthandPattern();
```

Update the class doc comment: replace "or github.com URL form" with "GitHub PR URL on any host, or GitLab MR URL".

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PrRefParserTests/*"`
Expected: PASS (all, including the updated `Garbage_input_rejected` and the nested-group rejection).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Commands/PrRefParser.cs test/Capacitor.Cli.Tests.Unit/PrRefParserTests.cs
git commit
```
Message: `feat(review): parse GitHub PR URLs on any host and GitLab MR URLs`

---

### Task 2: Add host helpers to `RemoteMatcher`

**Files:**
- Modify: `src/Capacitor.Cli.Core/Config/RemoteMatcher.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/RemoteMatcherTests.cs`

**Interfaces:**
- Produces: `RemoteMatcher.ExtractHost(string url) → string?` (host from a raw remote URL, or null); `RemoteMatcher.PathAfterHost(string normalized) → string?` (the `owner/repo` part of a `host/owner/path` normalized string, or null).

- [ ] **Step 1: Write the failing tests**

Add to `RemoteMatcherTests.cs`:

```csharp
[Test]
public async Task ExtractHost_from_ssh_and_https() {
    await Assert.That(RemoteMatcher.ExtractHost("git@github.com:kurrent-io/kcap.git")).IsEqualTo("github.com");
    await Assert.That(RemoteMatcher.ExtractHost("https://gitlab.com/group/project.git")).IsEqualTo("gitlab.com");
    await Assert.That(RemoteMatcher.ExtractHost("ssh://git@ghe.corp.com/team/app")).IsEqualTo("ghe.corp.com");
    await Assert.That(RemoteMatcher.ExtractHost("not a url")).IsNull();
}

[Test]
public async Task PathAfterHost_strips_leading_host_segment() {
    await Assert.That(RemoteMatcher.PathAfterHost("github.com/kurrent-io/kcap")).IsEqualTo("kurrent-io/kcap");
    await Assert.That(RemoteMatcher.PathAfterHost("gitlab.com/group/project")).IsEqualTo("group/project");
    await Assert.That(RemoteMatcher.PathAfterHost("nohostonly")).IsNull();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteMatcherTests/*"`
Expected: FAIL — `ExtractHost`/`PathAfterHost` don't exist (compile error).

- [ ] **Step 3: Implement the helpers**

Add to `RemoteMatcher` (after `NormalizeRemoteUrl`):

```csharp
/// <summary>Host of a raw git remote URL (e.g. "github.com"), or null if unrecognized.</summary>
public static string? ExtractHost(string url) {
    var normalized = NormalizeRemoteUrl(url);
    if (normalized is null) return null;
    var slash = normalized.IndexOf('/');
    return slash <= 0 ? null : normalized[..slash];
}

/// <summary>The "owner/repo" tail of a normalized "host/owner/path" string, or null.</summary>
public static string? PathAfterHost(string normalized) {
    if (string.IsNullOrEmpty(normalized)) return null;
    var slash = normalized.IndexOf('/');
    return slash <= 0 || slash == normalized.Length - 1 ? null : normalized[(slash + 1)..];
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteMatcherTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Config/RemoteMatcher.cs test/Capacitor.Cli.Tests.Unit/RemoteMatcherTests.cs
git commit
```
Message: `feat(core): add ExtractHost/PathAfterHost remote helpers`

---

### Task 3: Make daemon `RepoMatcher` host-agnostic

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/RepoMatcher.cs:30,57`
- Test: `test/Capacitor.Cli.Tests.Unit/RepoMatcherTests.cs`

**Interfaces:**
- Consumes: `RemoteMatcher.PathAfterHost` (Task 2).
- Produces: `RepoMatcher.FindAsync(string owner, string repo, string[] serverCandidates, CancellationToken)` — behavior change only (matches any host).

- [ ] **Step 1: Write the failing test**

Add to `RepoMatcherTests.cs` (follow the file's existing setup for creating a temp git repo with an origin remote — reuse its helper; if it seeds a `github.com` origin, add a variant that seeds a `gitlab.com` origin for the same `owner/repo`):

```csharp
[Test]
public async Task Matches_gitlab_remote_for_same_owner_repo() {
    using var tmp = new TempGitRepo(origin: "git@gitlab.com:group/project.git"); // mirror existing helper's shape
    var matcher = new RepoMatcher(DaemonConfigForPath(tmp.Path), NullLogger<RepoMatcher>.Instance);

    var matches = await matcher.FindAsync("group", "project", [tmp.Path], CancellationToken.None);

    await Assert.That(matches).Contains(tmp.Path);
}
```

> If `RepoMatcherTests.cs` has no reusable temp-repo helper with a settable origin, add a minimal one in this task (a private helper that `git init`s a temp dir and sets `origin`), then use it here and leave existing tests untouched.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/RepoMatcherTests/*"`
Expected: FAIL — current `target = $"github.com/{owner}/{repo}"` never equals the normalized `gitlab.com/group/project`.

- [ ] **Step 3: Implement host-agnostic matching**

In `FindAsync`, replace the target line:

```csharp
// was: var target = $"github.com/{owner}/{repo}";
var target = $"{owner}/{repo}";
```

And the comparison (line ~57):

```csharp
// was: if (string.Equals(remote, target, StringComparison.OrdinalIgnoreCase))
if (RemoteMatcher.PathAfterHost(remote) is { } path
    && string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) {
    matches.Add(root);
}
```

Update the XML doc on the class: note matching is host-agnostic on `owner/repo` (consistent with the host-agnostic `repo_hash`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/RepoMatcherTests/*"`
Expected: PASS (new GitLab test + all existing GitHub tests still pass).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/RepoMatcher.cs test/Capacitor.Cli.Tests.Unit/RepoMatcherTests.cs
git commit
```
Message: `fix(daemon): match repos host-agnostically on owner/repo`

---

### Task 4: Add `Host` to payload/cache and version-invalidate the cache

**Files:**
- Modify: `src/Capacitor.Cli.Core/Models.cs:52-102` (`RepositoryPayload`, `GitCacheEntry`)
- Modify: `src/Capacitor.Cli/RepositoryDetection.cs` (compute host; version guard; emit `host`)
- Test: `test/Capacitor.Cli.Tests.Unit/RepositoryDetectionCacheTests.cs` (create)

**Interfaces:**
- Consumes: `RemoteMatcher.ExtractHost` (Task 2).
- Produces: `RepositoryPayload.Host` (`string?`, JSON `host`); `GitCacheEntry.Host` (`string?`) and `GitCacheEntry.SchemaVersion` (`int`, JSON `schema_version`); `RepositoryDetection` const `CacheSchemaVersion = 2`.

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/RepositoryDetectionCacheTests.cs`:

```csharp
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class RepositoryDetectionCacheTests {
    [Test]
    public async Task GitCacheEntry_without_schema_version_deserializes_as_stale() {
        // A pre-upgrade entry (no schema_version, no host) must be treated as stale.
        const string legacy = """{"owner":"o","repo_name":"r","cached_at":"2020-01-01T00:00:00+00:00"}""";
        var entry = JsonSerializer.Deserialize(legacy, CapacitorJsonContext.Default.GitCacheEntry);
        await Assert.That(entry!.SchemaVersion).IsEqualTo(0);        // absent → default 0
        await Assert.That(entry.SchemaVersion == RepositoryDetection.CacheSchemaVersion).IsFalse();
    }

    [Test]
    public async Task GitCacheEntry_roundtrips_host_and_version() {
        var entry = new GitCacheEntry {
            RemoteUrl = "git@gitlab.com:group/project.git",
            Owner = "group", RepoName = "project", Host = "gitlab.com",
            SchemaVersion = RepositoryDetection.CacheSchemaVersion, CachedAt = DateTimeOffset.UnixEpoch
        };
        var json = JsonSerializer.Serialize(entry, CapacitorJsonContext.Default.GitCacheEntry);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.GitCacheEntry);
        await Assert.That(back!.Host).IsEqualTo("gitlab.com");
        await Assert.That(back.SchemaVersion).IsEqualTo(RepositoryDetection.CacheSchemaVersion);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/RepositoryDetectionCacheTests/*"`
Expected: FAIL — `Host`/`SchemaVersion`/`CacheSchemaVersion` don't exist (compile error).

- [ ] **Step 3: Add the fields**

In `Models.cs`, add to `RepositoryPayload` (after `RemoteUrl`):

```csharp
[JsonPropertyName("host")]
public string? Host { get; init; }
```

Add to `GitCacheEntry` (after `RemoteUrl`, and a version field):

```csharp
[JsonPropertyName("host")]
public string? Host { get; init; }

[JsonPropertyName("schema_version")]
public int SchemaVersion { get; init; }
```

- [ ] **Step 4: Version-guard the cache and populate host in `RepositoryDetection.cs`**

In `LoadCache` (the TTL check ~line 233), also reject on schema mismatch:

```csharp
if (entry is null || entry.SchemaVersion != CacheSchemaVersion) {
    return null;
}
return DateTimeOffset.UtcNow - entry.CachedAt > TimeSpan.FromHours(1) ? null : entry;
```

Add the constant near the top of the class:

```csharp
// Bump whenever the cached shape/derivation changes so stale entries are ignored.
// v2: added Host + provider-aware parsing.
internal const int CacheSchemaVersion = 2;
```

In `DetectRepositoryAsync`, compute host and thread it through. In the cache-hit branch add `host = cache.Host;`; in the cache-miss branch after parsing owner/repo:

```csharp
(owner, repoName) = GitUrlParser.ParseRemoteUrl(remoteUrl);
host = remoteUrl is null ? null : RemoteMatcher.ExtractHost(remoteUrl);

SaveCache(cwd, new() {
    UserName = userName, UserEmail = userEmail, RemoteUrl = remoteUrl,
    Owner = owner, RepoName = repoName, Host = host,
    SchemaVersion = CacheSchemaVersion, CachedAt = DateTimeOffset.UtcNow
});
```

Declare `string? ... host;` alongside the other locals (line ~86). Add `Host = host` to the returned `RepositoryPayload`. In `EnrichWithRepositoryInfo`, emit it with the other fields:

```csharp
if (repo.Host is not null) repoNode["host"] = repo.Host;
```

> `host` is not consumed for routing yet — that lands in Phase 2. It is stored now so the cache is correct when routing arrives, and the version bump discards pre-upgrade entries. The emitted `host` field is ignored by the server (tolerant deserialization); no server change.

- [ ] **Step 5: Run tests + full unit suite + AOT check**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/RepositoryDetectionCacheTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo "no AOT warnings"
```
Expected: cache tests PASS, full suite PASS, AOT grep prints "no AOT warnings".

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/Models.cs src/Capacitor.Cli/RepositoryDetection.cs test/Capacitor.Cli.Tests.Unit/RepositoryDetectionCacheTests.cs
git commit
```
Message: `feat: carry repo host and version-invalidate the detection cache`

---

## Phase 2 — Auto-detection

> The first user-visible GitLab release must include this phase (Open decision #3). Parse-only Phase 1 tags nothing, so `kcap review <gitlab-url>` would 404 until the detector lands.

### Task 5: Provider seam + `GitHubPrDetector` (refactor the existing gh call)

**Files:**
- Create: `src/Capacitor.Cli/PrDetection/PrDetector.cs`
- Modify: `src/Capacitor.Cli/RepositoryDetection.cs` (expose `RunCommandAsync` as a `CommandRunner`; route GitHub through the detector)
- Test: `test/Capacitor.Cli.Tests.Unit/GitHubPrDetectorTests.cs` (create)

**Interfaces:**
- Produces:
  - `internal delegate Task<string?> CommandRunner(string cmd, string arguments, string cwd, TimeSpan timeout);`
  - `internal sealed record PrInfo(int Number, string? Title, string? Url, string? HeadRef);`
  - `internal static Task<PrInfo?> GitHubPrDetector.DetectAsync(string cwd, TimeSpan cap, CommandRunner run);`

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/GitHubPrDetectorTests.cs`:

```csharp
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit;

public class GitHubPrDetectorTests {
    [Test]
    public async Task Parses_gh_pr_view_json() {
        CommandRunner fake = (cmd, args, cwd, _) => {
            Assert.That(cmd).IsEqualTo("gh");
            Assert.That(args).Contains("pr view");
            return Task.FromResult<string?>(
                """{"number":12,"title":"Add thing","url":"https://github.com/o/r/pull/12","headRefName":"feat/x"}""");
        };
        var pr = await GitHubPrDetector.DetectAsync("/cwd", TimeSpan.FromSeconds(2), fake);
        await Assert.That(pr!.Number).IsEqualTo(12);
        await Assert.That(pr.Title).IsEqualTo("Add thing");
        await Assert.That(pr.Url).IsEqualTo("https://github.com/o/r/pull/12");
        await Assert.That(pr.HeadRef).IsEqualTo("feat/x");
    }

    [Test]
    public async Task Null_output_yields_null() {
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>(null); // no PR / gh failed
        await Assert.That(await GitHubPrDetector.DetectAsync("/cwd", TimeSpan.FromSeconds(2), fake)).IsNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GitHubPrDetectorTests/*"`
Expected: FAIL — namespace/types don't exist.

- [ ] **Step 3: Create the seam and detector**

Create `src/Capacitor.Cli/PrDetection/PrDetector.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli.PrDetection;

/// <summary>Spawns a CLI and returns trimmed stdout, or null on failure/timeout.</summary>
internal delegate Task<string?> CommandRunner(string cmd, string arguments, string cwd, TimeSpan timeout);

internal sealed record PrInfo(int Number, string? Title, string? Url, string? HeadRef);

/// <summary>GitHub / GitHub Enterprise detection via `gh` (auto-targets the remote's host).</summary>
internal static class GitHubPrDetector {
    public static async Task<PrInfo?> DetectAsync(string cwd, TimeSpan cap, CommandRunner run) {
        var json = await run("gh", "pr view --json number,title,url,headRefName", cwd, cap);
        if (json is null) return null;
        try {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var number = o["number"]?.GetValue<int>();
            if (number is null) return null;
            return new PrInfo(number.Value, o["title"]?.GetValue<string>(),
                              o["url"]?.GetValue<string>(), o["headRefName"]?.GetValue<string>());
        } catch {
            return null; // best-effort
        }
    }
}
```

- [ ] **Step 4: Expose `RunCommandAsync` as a `CommandRunner` and route GitHub through the detector**

In `RepositoryDetection.cs`, change `RunCommandAsync` from `static async Task<string?>` to keep the same body but ensure it is assignable to `CommandRunner` (same parameter types/order — it already is). Add a field the detection path can use:

```csharp
internal static CommandRunner DefaultRunner => RunCommandAsync;
```

Replace the inline `gh pr view` block (lines ~143-160) with:

```csharp
if (ghCap > TimeSpan.Zero) {
    var pr = await PrDetection.GitHubPrDetector.DetectAsync(cwd, ghCap, DefaultRunner);
    if (pr is not null) {
        prNumber = pr.Number; prTitle = pr.Title; prUrl = pr.Url; prHeadRef = pr.HeadRef;
    }
}
```

> This is a pure refactor: GitHub behavior is unchanged. GitLab routing arrives in Task 8.

- [ ] **Step 5: Run tests + full suite**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GitHubPrDetectorTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli/PrDetection/PrDetector.cs src/Capacitor.Cli/RepositoryDetection.cs test/Capacitor.Cli.Tests.Unit/GitHubPrDetectorTests.cs
git commit
```
Message: `refactor: extract GitHubPrDetector behind a CommandRunner seam`

---

### Task 6: `GitLabPrDetector` (glab api + MR selection guards)

**Files:**
- Create: `src/Capacitor.Cli/PrDetection/GitLabPrDetector.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/GitLabPrDetectorTests.cs` (create)

**Interfaces:**
- Consumes: `CommandRunner`, `PrInfo` (Task 5).
- Produces: `internal static Task<PrInfo?> GitLabPrDetector.DetectAsync(string host, string owner, string repo, string? branch, string cwd, TimeSpan cap, CommandRunner run);`

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Tests.Unit/GitLabPrDetectorTests.cs`:

```csharp
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit;

public class GitLabPrDetectorTests {
    const string TwoOpenMrs = """
    [
      {"iid":5,"title":"old","web_url":"https://gitlab.com/g/p/-/merge_requests/5","source_branch":"feat/x","updated_at":"2026-06-01T00:00:00Z"},
      {"iid":9,"title":"new","web_url":"https://gitlab.com/g/p/-/merge_requests/9","source_branch":"feat/x","updated_at":"2026-06-30T00:00:00Z"},
      {"iid":7,"title":"other","web_url":"https://gitlab.com/g/p/-/merge_requests/7","source_branch":"feat/other","updated_at":"2026-07-01T00:00:00Z"}
    ]
    """;

    [Test]
    public async Task Picks_matching_branch_most_recently_updated() {
        CommandRunner fake = (cmd, args, _, _) => {
            Assert.That(cmd).IsEqualTo("glab");
            Assert.That(args).Contains("--hostname gitlab.com");
            Assert.That(args).Contains("projects/g%2Fp/merge_requests");
            Assert.That(args).Contains("source_branch=feat%2Fx");
            return Task.FromResult<string?>(TwoOpenMrs);
        };
        var pr = await GitLabPrDetector.DetectAsync("gitlab.com", "g", "p", "feat/x", "/cwd", TimeSpan.FromSeconds(2), fake);
        await Assert.That(pr!.Number).IsEqualTo(9);              // iid, newest updated_at, branch match
        await Assert.That(pr.Url).IsEqualTo("https://gitlab.com/g/p/-/merge_requests/9");
        await Assert.That(pr.HeadRef).IsEqualTo("feat/x");
    }

    [Test]
    public async Task Empty_branch_never_calls_glab_and_returns_null() {
        var called = false;
        CommandRunner fake = (_, _, _, _) => { called = true; return Task.FromResult<string?>("[]"); };
        var pr = await GitLabPrDetector.DetectAsync("gitlab.com", "g", "p", "", "/cwd", TimeSpan.FromSeconds(2), fake);
        await Assert.That(pr).IsNull();
        await Assert.That(called).IsFalse();                    // guard: no branch → no query (would return all MRs)
    }

    [Test]
    public async Task Encodes_branch_with_ampersand() {
        string seen = "";
        CommandRunner fake = (_, args, _, _) => { seen = args; return Task.FromResult<string?>("[]"); };
        await GitLabPrDetector.DetectAsync("gitlab.com", "g", "p", "feat/a&b", "/cwd", TimeSpan.FromSeconds(2), fake);
        await Assert.That(seen).Contains("source_branch=feat%2Fa%26b");
    }

    [Test]
    public async Task No_matching_branch_yields_null() {
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>(
            """[{"iid":5,"title":"x","web_url":"u","source_branch":"different","updated_at":"2026-06-01T00:00:00Z"}]""");
        await Assert.That(await GitLabPrDetector.DetectAsync("gitlab.com", "g", "p", "feat/x", "/cwd", TimeSpan.FromSeconds(2), fake)).IsNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GitLabPrDetectorTests/*"`
Expected: FAIL — type doesn't exist.

- [ ] **Step 3: Implement the GitLab detector**

Create `src/Capacitor.Cli/PrDetection/GitLabPrDetector.cs`:

```csharp
using System.Globalization;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.PrDetection;

/// <summary>
/// GitLab MR detection via `glab api` (raw JSON passthrough using glab's own auth —
/// kcap manages no token). Single-level owner/repo only.
/// </summary>
internal static class GitLabPrDetector {
    public static async Task<PrInfo?> DetectAsync(
            string host, string owner, string repo, string? branch, string cwd, TimeSpan cap, CommandRunner run) {
        // Guard: GitLab ignores an empty source_branch filter and returns ALL open MRs,
        // which would mis-tag the session (e.g. detached HEAD). No branch → no detection.
        if (string.IsNullOrEmpty(branch)) return null;

        var project = Uri.EscapeDataString($"{owner}/{repo}");
        var branchEnc = Uri.EscapeDataString(branch);
        var args = $"api --hostname {host} projects/{project}/merge_requests?source_branch={branchEnc}&state=opened";

        var json = await run("glab", args, cwd, cap);
        if (json is null) return null;

        try {
            if (JsonNode.Parse(json) is not JsonArray arr) return null;

            PrInfo? best = null;
            DateTimeOffset bestUpdated = DateTimeOffset.MinValue;

            foreach (var node in arr) {
                if (node is not JsonObject mr) continue;
                // Defense in depth: only accept an exact source_branch match.
                if (mr["source_branch"]?.GetValue<string>() != branch) continue;
                var iid = mr["iid"]?.GetValue<int>();
                if (iid is null) continue;

                var updated = ParseTimestamp(mr["updated_at"]?.GetValue<string>());
                if (best is null || updated > bestUpdated) {
                    bestUpdated = updated;
                    best = new PrInfo(iid.Value, mr["title"]?.GetValue<string>(),
                                      mr["web_url"]?.GetValue<string>(), mr["source_branch"]?.GetValue<string>());
                }
            }
            return best;
        } catch {
            return null; // best-effort
        }
    }

    static DateTimeOffset ParseTimestamp(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t : DateTimeOffset.MinValue;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GitLabPrDetectorTests/*"`
Expected: PASS (branch guard, encoding, selection, no-match).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/PrDetection/GitLabPrDetector.cs test/Capacitor.Cli.Tests.Unit/GitLabPrDetectorTests.cs
git commit
```
Message: `feat: add GitLabPrDetector via glab api with MR selection guards`

---

### Task 7: `GitProviderRouter` (host routing + custom-host probe + memo)

**Files:**
- Create: `src/Capacitor.Cli/PrDetection/GitProviderRouter.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/GitProviderRouterTests.cs` (create)

**Interfaces:**
- Consumes: `CommandRunner` (Task 5).
- Produces:
  - `internal enum GitProviderKind { GitHub, GitLab, Unknown }`
  - `internal static Task<GitProviderKind> GitProviderRouter.ResolveAsync(string? host, string cwd, TimeSpan cap, CommandRunner run);`
  - `internal static void GitProviderRouter.ResetMemoForTests();`

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Tests.Unit/GitProviderRouterTests.cs`:

```csharp
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit;

public class GitProviderRouterTests {
    [Before(Test)]
    public void Reset() => GitProviderRouter.ResetMemoForTests();

    static CommandRunner Never => (_, _, _, _) => throw new InvalidOperationException("probe should not run for SaaS hosts");

    [Test]
    public async Task Saas_hosts_route_without_probing() {
        await Assert.That(await GitProviderRouter.ResolveAsync("github.com", "/c", TimeSpan.FromSeconds(2), Never)).IsEqualTo(GitProviderKind.GitHub);
        await Assert.That(await GitProviderRouter.ResolveAsync("gitlab.com", "/c", TimeSpan.FromSeconds(2), Never)).IsEqualTo(GitProviderKind.GitLab);
    }

    [Test]
    public async Task Custom_host_in_gh_auth_status_is_github() {
        CommandRunner fake = (cmd, args, _, _) => {
            Assert.That(cmd).IsEqualTo("gh");
            Assert.That(args).IsEqualTo("auth status --json hosts");
            return Task.FromResult<string?>("""{"hosts":{"github.com":[],"ghe.corp.com":[]}}""");
        };
        await Assert.That(await GitProviderRouter.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), fake)).IsEqualTo(GitProviderKind.GitHub);
    }

    [Test]
    public async Task Custom_host_not_in_gh_falls_back_to_gitlab() {
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>("""{"hosts":{"github.com":[]}}""");
        await Assert.That(await GitProviderRouter.ResolveAsync("gitlab.corp.com", "/c", TimeSpan.FromSeconds(2), fake)).IsEqualTo(GitProviderKind.GitLab);
    }

    [Test]
    public async Task Probe_result_is_memoized_per_host() {
        var calls = 0;
        CommandRunner fake = (_, _, _, _) => { calls++; return Task.FromResult<string?>("""{"hosts":{"ghe.corp.com":[]}}"""); };
        await GitProviderRouter.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), fake);
        await GitProviderRouter.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), fake);
        await Assert.That(calls).IsEqualTo(1); // memoized: bulk-import loop can't multiply the probe
    }

    [Test]
    public async Task Null_host_is_unknown() {
        await Assert.That(await GitProviderRouter.ResolveAsync(null, "/c", TimeSpan.FromSeconds(2), Never)).IsEqualTo(GitProviderKind.Unknown);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GitProviderRouterTests/*"`
Expected: FAIL — type doesn't exist.

- [ ] **Step 3: Implement the router**

Create `src/Capacitor.Cli/PrDetection/GitProviderRouter.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.PrDetection;

internal enum GitProviderKind { GitHub, GitLab, Unknown }

/// <summary>
/// Maps a remote host to a provider. SaaS hosts route directly; a custom host is
/// probed once via `gh auth status --json hosts` (GitHub if listed, else best-effort
/// GitLab). The decision is memoized per host for the process lifetime so the
/// ImportCommand bulk loop can't multiply the probe.
/// </summary>
internal static class GitProviderRouter {
    static readonly ConcurrentDictionary<string, GitProviderKind> Memo = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<GitProviderKind> ResolveAsync(string? host, string cwd, TimeSpan cap, CommandRunner run) {
        if (string.IsNullOrEmpty(host)) return GitProviderKind.Unknown;
        if (host == "github.com") return GitProviderKind.GitHub;
        if (host == "gitlab.com") return GitProviderKind.GitLab;

        if (Memo.TryGetValue(host, out var cached)) return cached;

        var kind = await ProbeAsync(host, cwd, cap, run);
        Memo[host] = kind;
        return kind;
    }

    static async Task<GitProviderKind> ProbeAsync(string host, string cwd, TimeSpan cap, CommandRunner run) {
        // `gh auth status --json hosts` forces exit 0 even on auth failure, so decide
        // from the JSON payload, not the exit code.
        var json = await run("gh", "auth status --json hosts", cwd, cap);
        if (json is not null) {
            try {
                if (JsonNode.Parse(json)?["hosts"] is JsonObject hosts && hosts.ContainsKey(host)) {
                    return GitProviderKind.GitHub;
                }
            } catch { /* fall through */ }
        }
        // Not a known GitHub host → assume GitLab and let the detector no-op if unauthenticated.
        return GitProviderKind.GitLab;
    }

    internal static void ResetMemoForTests() => Memo.Clear();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GitProviderRouterTests/*"`
Expected: PASS (incl. memoization).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/PrDetection/GitProviderRouter.cs test/Capacitor.Cli.Tests.Unit/GitProviderRouterTests.cs
git commit
```
Message: `feat: add GitProviderRouter with custom-host probe and per-host memo`

---

### Task 8: Wire the router into `DetectRepositoryAsync` (effective provider budget)

**Files:**
- Modify: `src/Capacitor.Cli/RepositoryDetection.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/RepositoryDetectionCacheTests.cs` (extend) — plus manual smoke.

**Interfaces:**
- Consumes: `GitProviderRouter.ResolveAsync`, `GitHubPrDetector.DetectAsync`, `GitLabPrDetector.DetectAsync`, and `host`/`owner`/`repo`/`branch` already computed in `DetectRepositoryAsync`.

- [ ] **Step 1: Replace the GitHub-only block with router-driven dispatch**

In `DetectRepositoryAsync`, the effective provider budget already exists as `ghCap` (post-git remainder when a budget is passed, else the fixed 2s cap). Rename intent in a comment and dispatch by provider:

```csharp
// Effective provider budget: the post-git remainder when the caller passed a budget
// (only ClaudeHookCommand does), else the historical 2s cap. Covers probe + detector.
var providerCap = ghCap;

if (providerCap > TimeSpan.Zero && host is not null) {
    var kind = await PrDetection.GitProviderRouter.ResolveAsync(host, cwd, providerCap, DefaultRunner);
    var pr = kind switch {
        PrDetection.GitProviderKind.GitHub => await PrDetection.GitHubPrDetector.DetectAsync(cwd, providerCap, DefaultRunner),
        PrDetection.GitProviderKind.GitLab when owner is not null && repoName is not null
            => await PrDetection.GitLabPrDetector.DetectAsync(host, owner, repoName, branch, cwd, providerCap, DefaultRunner),
        _ => null
    };
    if (pr is not null) {
        prNumber = pr.Number; prTitle = pr.Title; prUrl = pr.Url; prHeadRef = pr.HeadRef;
    }
}
```

Remove the now-dead direct `GitHubPrDetector.DetectAsync` block added in Task 5 (this supersedes it). Keep the whole thing inside the existing `try`/`catch` so any failure degrades to an untagged session.

> The `gh auth status` probe only runs for non-SaaS hosts (the router returns early for `github.com`/`gitlab.com`) and shares `providerCap`. "No explicit budget" still yields the 2s default cap — detection runs on every surface; it is never silently skipped.

- [ ] **Step 2: Add a routing smoke test**

Extend `RepositoryDetectionCacheTests.cs` with a test that exercises `DetectRepositoryAsync` against a temp git repo whose origin is `gitlab.com` — assert it does not throw and returns owner/repo `group`/`project` even when `glab` is absent (PR fields null). Reuse the temp-repo helper from Task 3.

```csharp
[Test]
public async Task Detects_gitlab_repo_base_info_without_glab() {
    using var tmp = new TempGitRepo(origin: "git@gitlab.com:group/project.git");
    var repo = await RepositoryDetection.DetectRepositoryAsync(tmp.Path);
    await Assert.That(repo!.Owner).IsEqualTo("group");
    await Assert.That(repo.RepoName).IsEqualTo("project");
    await Assert.That(repo.Host).IsEqualTo("gitlab.com");
    // No glab installed/authenticated in CI → PR fields stay null (best-effort).
}
```

- [ ] **Step 3: Run the full unit suite + AOT check**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo "no AOT warnings"
```
Expected: full suite PASS; AOT grep prints "no AOT warnings".

- [ ] **Step 4: Commit**

```bash
git add src/Capacitor.Cli/RepositoryDetection.cs test/Capacitor.Cli.Tests.Unit/RepositoryDetectionCacheTests.cs
git commit
```
Message: `feat: route PR detection by provider (GitHub/GitLab) with effective budget`

---

### Task 9: Docs — announce GitLab support

**Files:**
- Modify: `README.md` (`## Getting started` prerequisites + the `kcap review` section under `## CLI commands`)
- Modify: `src/Capacitor.Cli.Core/Resources/help-*.txt` (the `review` command help, if it enumerates URL formats)
- Modify: `src/Capacitor.Cli/Commands/ReviewCommand.cs:13-14` (usage text)

**Interfaces:** none (documentation).

- [ ] **Step 1: Update `ReviewCommand` usage text**

In `ReviewCommand.HandleReview`, extend the "Expected formats" block:

```csharp
await Console.Error.WriteLineAsync("Expected formats:");
await Console.Error.WriteLineAsync("  GitHub URL:  https://github.com/owner/repo/pull/123");
await Console.Error.WriteLineAsync("  GitLab URL:  https://gitlab.com/owner/repo/-/merge_requests/123");
await Console.Error.WriteLineAsync("  Shorthand:   owner/repo#123");
```

- [ ] **Step 2: Update `README.md`**

- In `## Getting started` prerequisites: note that PR/MR auto-tagging uses the provider CLI — `gh` for GitHub/GitHub Enterprise, `glab` for GitLab — and is best-effort (a session is simply untagged if the matching CLI isn't installed/authenticated).
- In the `kcap review` section under `## CLI commands`: add the GitLab MR URL form alongside the GitHub URL and `owner/repo#123` shorthand, and mention nested GitLab groups are not yet supported.

- [ ] **Step 3: Update `help-*.txt` if applicable**

`grep -n "pull/" src/Capacitor.Cli.Core/Resources/help-*.txt` — if the review help lists the GitHub URL form, add the GitLab MR URL form the same way. If no help file mentions PR URL formats, skip (note it in the commit body).

- [ ] **Step 4: Build + verify usage text renders**

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
```
Expected: builds clean. (Usage text is only printed on a parse failure; a manual `kcap review not-a-ref` would show the three formats.)

- [ ] **Step 5: Commit**

```bash
git add README.md src/Capacitor.Cli/Commands/ReviewCommand.cs src/Capacitor.Cli.Core/Resources/
git commit
```
Message: `docs: document GitLab MR support for kcap review and auto-detection`

---

## Self-Review

**Spec coverage** (each spec section → task):
- §1 provider routing → Tasks 5–8. §1 GitLab field mapping (`iid`→`pr_number`, etc.) → Task 6. §1 encoding of path+branch → Task 6.
- §2 custom/self-hosted host probe (`gh auth status --json hosts`, read JSON not exit code) → Task 7.
- §3 host extraction → Task 2 + Task 4; nested-group deferral → Task 1 (parser rejects) + Global Constraints; **Cache** requirement → Task 4.
- §4 PrRefParser (GitHub any-host, GitLab MR URL + suffix tolerance, shorthand single-level) → Task 1.
- §5 daemon `RepoMatcher` host-agnostic suffix match → Task 3.
- §6a repo_hash kept host-agnostic → no code change (honored by Task 3's suffix match); §6b nested-group server routing → out of scope (deferred), enforced by Task 1 rejection.
- §7 docs → Task 9.
- Error handling / effective provider budget → Task 8; best-effort degradation → Global Constraints + every detector's `try/catch`.
- Testing list → Tasks 1–8 test steps (parser incl. nested-group rejection & suffix; GitUrlParser via cache test host; router probe reads JSON; cache invalidation; encoded branch; MR selection/guards; field mapping).

**Placeholder scan:** No TBD/TODO. The only conditional is Task 3/Task 9 step 3 ("if the file has no helper" / "if help lists URL formats") — both give the concrete action for each branch, not a deferral.

**Type consistency:** `CommandRunner(string cmd, string arguments, string cwd, TimeSpan timeout)` matches `RepositoryDetection.RunCommandAsync`'s signature (Task 5 relies on this). `PrInfo(int Number, string? Title, string? Url, string? HeadRef)` is produced by both detectors and consumed identically in Task 8. `GitProviderKind { GitHub, GitLab, Unknown }` used consistently in Tasks 7–8. `GitCacheEntry.SchemaVersion` / `RepositoryDetection.CacheSchemaVersion` names match across Task 4 and the test. `RemoteMatcher.ExtractHost`/`PathAfterHost` defined in Task 2, consumed in Tasks 3–4.

**Note on ordering:** Task 5 introduces a GitHub-only dispatch that Task 8 replaces. This is intentional (Task 5 is a behavior-preserving refactor mergeable on its own; Task 8 generalizes it). The plan calls out removing the Task-5 block in Task 8 Step 1.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-01-multi-provider-pr-detection.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
