# Beta Release Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish internal-first CLI releases to a `beta` npm dist-tag so `stable`/`micro` users on `latest` aren't dragged ahead of their server, with an opt-in `kcap update --beta` channel.

**Architecture:** A prerelease git tag (`vX.Y.Z-beta.N`) publishes every npm package with `--tag beta`, leaving `latest` untouched; non-prerelease tags publish to `latest` as today. The CLI gains an opt-in update channel (persisted in config, overridable per-invocation with `--beta`/`--stable`) that decides which npm dist-tag `kcap update` reads and installs. A new SemVer-2.0-precedence comparator orders prereleases (the existing `SemverCompare` strips them and can't).

**Tech Stack:** .NET 10 NativeAOT (C#), TUnit + WireMock.Net tests, GitHub Actions (bash), Node.js launcher.

**Scope note:** This is Phase 1 of the CLI↔server version-skew design (`docs/superpowers/specs/2026-07-03-cli-server-version-skew-design.md`), which is intentionally independent. Phases 2–4 (version cache, proactive guards, capability endpoint) are separate plans. This plan does **not** implement server-version guards — the beta channel is a noise-reducer, not the safety mechanism.

## Global Constraints

- .NET 10 NativeAOT — no dynamic codegen. Use source-generated `JsonSerializerContext` for all JSON. Never use `JsonArray` collection-expression literals (`[a, b]`); use `new JsonArray(a, b)`. Verify no `IL3050`/`IL2026` warnings with `dotnet publish -c Release`.
- TUnit runs on Microsoft Testing Platform; run test projects as executables (`dotnet run --project ...`). Filter with `--treenode-filter` glob, NOT `--filter`.
- README sync is mandatory for user-facing CLI changes: update `README.md` **quick-start (`## Getting started`) and the per-command section under `## CLI commands`** in the same PR — not just help text.
- Default update behavior must stay `latest`. Beta is strictly opt-in.
- Frequent commits: one per task.

---

### Task 1: Prerelease-aware SemVer comparator

**Files:**
- Create: `src/Capacitor.Cli.Core/PrereleaseSemver.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/PrereleaseSemverTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `public static class PrereleaseSemver`
  - `public static bool PrereleaseSemver.IsNewer(string? candidate, string? current)` — true iff `candidate` has strictly higher SemVer-2.0 precedence than `current`; false if either is null/unparseable/`"unknown"` ("don't know → don't claim newer").
  - `public static int PrereleaseSemver.Compare(string? a, string? b)` — `<0/0/>0`; unparseable sorts lowest.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/Capacitor.Cli.Tests.Unit/PrereleaseSemverTests.cs
using Capacitor.Cli.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit;

public class PrereleaseSemverTests {
    [Test]
    public async Task Orders_stable_versions() {
        await Assert.That(PrereleaseSemver.IsNewer("0.8.0", "0.7.9")).IsTrue();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.9", "0.8.0")).IsFalse();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0", "0.7.0")).IsFalse();
    }

    [Test]
    public async Task Prerelease_is_lower_than_its_release() {
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0", "0.7.0-beta.1")).IsTrue();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0-beta.1", "0.7.0")).IsFalse();
    }

    [Test]
    public async Task Orders_prereleases_numerically_not_lexically() {
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0-beta.2", "0.7.0-beta.1")).IsTrue();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0-beta.10", "0.7.0-beta.2")).IsTrue();
    }

    [Test]
    public async Task Numeric_identifier_ranks_below_alphanumeric() {
        // SemVer 2.0: 0.7.0-alpha < 0.7.0-alpha.1 ; numeric < alphanumeric at same slot
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0-alpha.1", "0.7.0-alpha")).IsTrue();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0-alpha.beta", "0.7.0-alpha.1")).IsTrue();
    }

    [Test]
    public async Task Ignores_build_metadata() {
        await Assert.That(PrereleaseSemver.Compare("0.7.0+aaa", "0.7.0+bbb")).IsEqualTo(0);
        await Assert.That(PrereleaseSemver.IsNewer("0.7.1+x", "0.7.0+y")).IsTrue();
    }

    [Test]
    public async Task Unparseable_or_unknown_never_claims_newer() {
        await Assert.That(PrereleaseSemver.IsNewer("unknown", "0.7.0")).IsFalse();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0", "unknown")).IsFalse();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0", null)).IsFalse();
        await Assert.That(PrereleaseSemver.IsNewer("garbage", "0.7.0")).IsFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/PrereleaseSemverTests/*"`
Expected: FAIL — `PrereleaseSemver` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

```csharp
// src/Capacitor.Cli.Core/PrereleaseSemver.cs
namespace Capacitor.Cli.Core;

/// <summary>
/// SemVer 2.0 precedence comparator that ORDERS prereleases
/// (0.7.0-beta.1 &lt; 0.7.0-beta.2 &lt; 0.7.0). Build metadata (+…) is ignored.
/// Unlike <see cref="SemverCompare"/> (which strips -prerelease and cannot order
/// betas), this is required by the opt-in beta update channel. Null / empty /
/// "unknown" / unparseable inputs sort lowest, and <see cref="IsNewer"/> returns
/// false when either side is unparseable ("don't know → don't claim newer").
/// </summary>
public static class PrereleaseSemver {
    public static bool IsNewer(string? candidate, string? current) {
        var c   = Parse(candidate);
        var cur = Parse(current);
        if (c is null || cur is null) return false;
        return CompareParsed(c.Value, cur.Value) > 0;
    }

    public static int Compare(string? a, string? b) {
        var pa = Parse(a);
        var pb = Parse(b);
        if (pa is null && pb is null) return 0;
        if (pa is null) return -1;
        if (pb is null) return 1;
        return CompareParsed(pa.Value, pb.Value);
    }

    readonly record struct V(int Major, int Minor, int Patch, string[] Pre);

    static V? Parse(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (string.Equals(s, "unknown", StringComparison.Ordinal)) return null;

        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];                 // drop build metadata

        string core;
        string[] pre;
        var dash = s.IndexOf('-');
        if (dash >= 0) {
            core = s[..dash];
            var preStr = s[(dash + 1)..];
            if (preStr.Length == 0) return null;
            pre = preStr.Split('.');
            foreach (var id in pre) if (id.Length == 0) return null;
        } else {
            core = s;
            pre  = [];                                 // Array.Empty<string>() — AOT-safe
        }

        var parts = core.Split('.');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch)) return null;
        if (major < 0 || minor < 0 || patch < 0) return null;

        return new V(major, minor, patch, pre);
    }

    static int CompareParsed(V a, V b) {
        var c = a.Major.CompareTo(b.Major); if (c != 0) return c;
        c     = a.Minor.CompareTo(b.Minor); if (c != 0) return c;
        c     = a.Patch.CompareTo(b.Patch); if (c != 0) return c;

        var aPre = a.Pre.Length > 0;
        var bPre = b.Pre.Length > 0;
        if (!aPre && !bPre) return 0;
        if (aPre && !bPre)  return -1;                 // 0.7.0-beta.1 < 0.7.0
        if (!aPre && bPre)  return 1;

        var len = Math.Min(a.Pre.Length, b.Pre.Length);
        for (var i = 0; i < len; i++) {
            var ai = a.Pre[i];
            var bi = b.Pre[i];
            var aNum = int.TryParse(ai, out var an);
            var bNum = int.TryParse(bi, out var bn);
            if (aNum && bNum) {
                var nc = an.CompareTo(bn); if (nc != 0) return nc;
            } else if (aNum) {
                return -1;                             // numeric < alphanumeric
            } else if (bNum) {
                return 1;
            } else {
                var sc = string.CompareOrdinal(ai, bi);
                if (sc != 0) return sc < 0 ? -1 : 1;
            }
        }
        return a.Pre.Length.CompareTo(b.Pre.Length);   // more identifiers wins
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/PrereleaseSemverTests/*"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/PrereleaseSemver.cs test/Capacitor.Cli.Tests.Unit/PrereleaseSemverTests.cs
git commit -m "feat(core): prerelease-aware SemVer 2.0 comparator for the beta channel"
```

---

### Task 2: Release workflow publishes prereleases to `beta`

**Files:**
- Create: `scripts/npm-dist-tag.sh`
- Create: `scripts/npm-dist-tag.test.sh`
- Create: `RELEASING.md`
- Modify: `.github/workflows/release.yml` (the `publish-npm` job — per-package publish loop ~`:288-289` and the wrapper publish ~`:320`)

**Interfaces:**
- Consumes: nothing.
- Produces: `scripts/npm-dist-tag.sh <version>` prints `beta` for a SemVer prerelease, else `latest`. Consumed by `release.yml`.

- [ ] **Step 1: Write the failing test**

```bash
# scripts/npm-dist-tag.test.sh
#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/npm-dist-tag.sh"

fail=0
assert() {
  local got; got="$(bash "$sh" "$1")"
  if [ "$got" != "$2" ]; then echo "FAIL: '$1' -> '$got' (want '$2')"; fail=1; fi
}
assert "0.7.0"            latest
assert "0.7.0-beta.1"     beta
assert "0.7.0-beta.10"    beta
assert "1.2.3+build.5"    latest
assert "1.2.3-rc.1+build" beta
[ "$fail" -eq 0 ] && echo "ok" || exit 1
```

- [ ] **Step 2: Run it to verify it fails**

Run: `bash scripts/npm-dist-tag.test.sh`
Expected: FAIL — `scripts/npm-dist-tag.sh` does not exist.

- [ ] **Step 3: Write the script**

```bash
# scripts/npm-dist-tag.sh
#!/usr/bin/env bash
# Prints the npm dist-tag for a version string: `beta` for a SemVer prerelease
# (a hyphen in the core, ignoring +build metadata), else `latest`.
set -euo pipefail
version="${1:?usage: npm-dist-tag.sh <version>}"
core="${version%%+*}"                 # strip +build metadata
if [[ "$core" == *-* ]]; then echo beta; else echo latest; fi
```

Then make both executable:

```bash
chmod +x scripts/npm-dist-tag.sh scripts/npm-dist-tag.test.sh
```

- [ ] **Step 4: Run it to verify it passes**

Run: `bash scripts/npm-dist-tag.test.sh`
Expected: `ok`

- [ ] **Step 5: Wire it into `release.yml`**

In the `publish-npm` job, after the `Extract version from tag` step, add a step:

```yaml
      - name: Derive npm dist-tag
        id: disttag
        run: echo "TAG=$(bash scripts/npm-dist-tag.sh '${{ steps.version.outputs.VERSION }}')" >> "$GITHUB_OUTPUT"
```

In the per-package publish loop, change the publish line from:

```bash
            npm publish --access public
```

to:

```bash
            npm publish --access public --tag "${{ steps.disttag.outputs.TAG }}"
```

And in the `Publish wrapper package` step, change:

```bash
          npm publish --access public
```

to:

```bash
          npm publish --access public --tag "${{ steps.disttag.outputs.TAG }}"
```

- [ ] **Step 6: Document the release invariant**

The dist-tag mapping is only half the guarantee — the human release process must not cut a bare `vN` tag for an internal-first rollout. Create `RELEASING.md`:

```markdown
# Releasing kcap

The CLI version tracks the server version (consolidated bump). npm dist-tags
gate who receives a release:

- **Prerelease tag** `vX.Y.Z-beta.N` → published to the **`beta`** dist-tag.
  `latest` is untouched. Use this for anything deployed **internal-tenants-first**.
- **Release tag** `vX.Y.Z` → published to **`latest`**. Everyone on `stable`/`micro`
  who runs `kcap update` (or `npm i -g @kurrent/kcap`) gets it.

## Invariant (do not break)

Cut a bare `vX.Y.Z` tag **only once the matching server version is available to
the cohorts that consume `latest`** (stable + micro). For an internal-first
rollout, cut `vX.Y.Z-beta.N` instead — otherwise `@kurrent/kcap@latest` moves
ahead of those users' servers and reintroduces version skew.

Internal-tenant users opt into the beta CLI with `kcap update --beta`.
```

- [ ] **Step 7: Commit**

```bash
git add scripts/npm-dist-tag.sh scripts/npm-dist-tag.test.sh RELEASING.md .github/workflows/release.yml
git commit -m "ci: publish prerelease tags to the npm beta dist-tag, document release invariant"
```

---

### Task 3: `update_channel` config field (on the v2 `Profile`)

> **Design correction (2026-07-03):** the live config is v2 profile-based. Update
> settings belong on the per-profile `Profile` record (which already has
> `update_check`, `ProfileConfig.cs:74`), read via `AppConfig.GetActiveProfileAsync()`
> and persisted via `AppConfig.SaveProfileConfig`. The legacy flat `CapacitorConfig`
> (read by `AppConfig.Load()`) is v1-only and effectively dead for real users, so
> `update_channel` must NOT go there.

**Files:**
- Modify: `src/Capacitor.Cli.Core/Config/ProfileConfig.cs` (`Profile` record — add `UpdateChannel` right after `UpdateCheck` at `:74-75`)
- Test: `test/Capacitor.Cli.Tests.Unit/UpdateChannelConfigTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Profile.UpdateChannel` — `public string UpdateChannel { get; init; } = "latest";`, JSON name `update_channel`.

**Note on STJ source-gen defaults:** the source-generated context does NOT apply the
`= "latest"` member-initializer when the property is absent from the JSON — an absent
`update_channel` deserializes to `null`, not `"latest"` (same quirk the existing
`DefaultVisibility ?? "org_public"` normalization works around). So the "default"
test must assert on direct construction (`new Profile().UpdateChannel == "latest"`),
and Task 4's read site must apply `?? "latest"` explicitly. The round-trip test sets
the value explicitly on both sides and does not depend on the default path.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.Cli.Tests.Unit/UpdateChannelConfigTests.cs
using System.Text.Json;
using Capacitor.Cli.Core.Config;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit;

public class UpdateChannelConfigTests {
    // Default asserted via direct construction: STJ source-gen does NOT apply the
    // `= "latest"` member-initializer for a property absent from the JSON, so a
    // Deserialize("{}") would yield null here — that is expected, and Task 4's read
    // site applies `?? "latest"`. This test verifies the record default itself.
    [Test]
    public async Task Defaults_to_latest_on_new_profile() {
        await Assert.That(new Profile().UpdateChannel).IsEqualTo("latest");
    }

    // Round-trip through the SAME serialization context the profile config uses on
    // disk (ProfileConfigJsonContext[Indented]). Confirm the exact context type names
    // by reading ProfileConfig.cs — SaveProfileConfig uses
    // ProfileConfigJsonContextIndented.Default.ProfileConfig; there is a matching
    // non-indented ProfileConfigJsonContext for reads.
    [Test]
    public async Task Round_trips_beta_through_profile_config() {
        var config = new ProfileConfig {
            Profiles = new() { ["default"] = new Profile { UpdateChannel = "beta" } }
        };
        var json = JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig);
        await Assert.That(json).Contains("update_channel");
        await Assert.That(json).Contains("beta");
        var back = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;
        await Assert.That(back.Profiles["default"].UpdateChannel).IsEqualTo("beta");
    }
}
```

Note: the profile serialization contexts are `internal`. `Capacitor.Cli.Core` already
has `<InternalsVisibleTo Include="Capacitor.Cli.Tests.Unit" />` in its csproj (verified
in Task 3's investigation), so no change needed. If `ProfileConfigJsonContext` is named
differently, use the exact name from `ProfileConfig.cs`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/UpdateChannelConfigTests/*"`
Expected: FAIL — `Profile` has no `UpdateChannel`.

- [ ] **Step 3: Add the property to `Profile`**

In `src/Capacitor.Cli.Core/Config/ProfileConfig.cs`, inside `record Profile`, right after the `UpdateCheck` property (`:74-75`):

```csharp
    [JsonPropertyName("update_channel")]
    public string UpdateChannel { get; init; } = "latest";
```

Do NOT add it to `CapacitorConfig`.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/UpdateChannelConfigTests/*"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Config/ProfileConfig.cs test/Capacitor.Cli.Tests.Unit/UpdateChannelConfigTests.cs
git commit -m "feat(config): add opt-in update_channel on v2 Profile (default latest)"
```

---

### Task 4: Channel-aware `UpdateCommand`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/UpdateCommand.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/UpdateChannelResolveTests.cs`

**Interfaces:**
- Consumes: `PrereleaseSemver.IsNewer` (Task 1); `Profile.UpdateChannel` (Task 3), read via `AppConfig.GetActiveProfileAsync()` and persisted via `AppConfig.SaveProfileConfig`.
- Produces:
  - `internal static string UpdateCommand.ResolveChannel(string[] args, string? configuredChannel)` — returns `"beta"` if `--beta` in args, `"latest"` if `--stable` in args, else `configuredChannel` if non-empty, else `"latest"`.
  - The `--check` JSON gains `["channel"]` and `["install_tag"]` (both the resolved channel string) — consumed by the launcher in Task 5.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.Cli.Tests.Unit/UpdateChannelResolveTests.cs
using Capacitor.Cli.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit;

public class UpdateChannelResolveTests {
    [Test]
    public async Task Defaults_to_latest() =>
        await Assert.That(UpdateCommand.ResolveChannel([], null)).IsEqualTo("latest");

    [Test]
    public async Task Config_beta_is_honoured() =>
        await Assert.That(UpdateCommand.ResolveChannel([], "beta")).IsEqualTo("beta");

    [Test]
    public async Task Beta_flag_overrides_config() =>
        await Assert.That(UpdateCommand.ResolveChannel(["--beta"], "latest")).IsEqualTo("beta");

    [Test]
    public async Task Stable_flag_overrides_config_beta() =>
        await Assert.That(UpdateCommand.ResolveChannel(["--stable"], "beta")).IsEqualTo("latest");
}
```

Requires `UpdateCommand` and its members visible to the test project. `UpdateCommand`
is in the `Capacitor.Cli` assembly; ensure the Unit test project references it and add
`[assembly: InternalsVisibleTo("Capacitor.Cli.Tests.Unit")]` to `Capacitor.Cli` if not
present (check first). If `Capacitor.Cli` is an executable the test project can't
reference, mark `ResolveChannel` `public`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/UpdateChannelResolveTests/*"`
Expected: FAIL — `ResolveChannel` does not exist.

- [ ] **Step 3: Implement channel resolution + channel-aware check**

In `src/Capacitor.Cli/Commands/UpdateCommand.cs`:

(a) Add the resolver:

```csharp
    internal static string ResolveChannel(string[] args, string? configuredChannel) {
        if (args.Contains("--stable")) return "latest";
        if (args.Contains("--beta"))   return "beta";
        return string.IsNullOrWhiteSpace(configuredChannel) ? "latest" : configuredChannel;
    }
```

(b) Thread the channel through the check. Change `CheckForUpdateAsync` to take a
`channel` parameter and build the registry URL and cache path from it (a separate
cache file per channel so `beta` and `latest` don't clobber each other):

```csharp
    static string CachePathFor(string channel) =>
        PathHelpers.ConfigPath($"update-check-{channel}.json");

    static async Task<(string? latest, string? current)> CheckForUpdateAsync(bool forceCheck, string channel) {
        var current   = GetCurrentVersion();
        var cachePath = CachePathFor(channel);
        // ... existing cache read/write, but use `cachePath` instead of the old CachePath field ...
        var resp = await http.GetAsync($"https://registry.npmjs.org/@kurrent/kcap/{channel}");
        // ... unchanged parse/cache ...
    }
```

Delete the old `static readonly string CachePath = ...` field (replaced by `CachePathFor`).

(c) Replace the `IsNewer` helper to use the prerelease-aware comparator:

```csharp
    static bool IsNewer(string? latest, string? current) => PrereleaseSemver.IsNewer(latest, current);
```

(d) In `HandleAsync`, resolve the channel from the ACTIVE v2 profile and, when
`--beta`/`--stable` was passed explicitly, persist it onto that profile via the v2
saver. At the top of `HandleAsync`:

```csharp
        var profile   = await AppConfig.GetActiveProfileAsync();
        var channel   = ResolveChannel(args, profile?.UpdateChannel);
        var checkOnly = args.Contains("--check");

        // Persist an explicit channel switch onto the active profile so future
        // auto-updates track it. Update the profile inside ProfileConfig and save
        // the whole v2 config via SaveProfileConfig — NEVER write a flat
        // CapacitorConfig, which would overwrite the user's v2 profile config.
        if (args.Contains("--beta") || args.Contains("--stable")) {
            var pc         = await AppConfig.LoadProfileConfig();
            var activeName = pc.ActiveProfile;
            if (pc.Profiles.TryGetValue(activeName, out var active)
             && active.UpdateChannel != channel) {
                var profiles = new Dictionary<string, Profile>(pc.Profiles) {
                    [activeName] = active with { UpdateChannel = channel }
                };
                await AppConfig.SaveProfileConfig(pc with { Profiles = profiles });
            }
        }
```

Use `channel` in the `CheckForUpdateAsync(forceCheck: true, channel)` call. Then extend
the `--check` JSON object:

```csharp
            var obj = new JsonObject {
                ["current"]     = current,
                ["latest"]      = latest,
                ["newer"]       = newer,
                ["channel"]     = channel,
                ["install_tag"] = channel,
            };
```

And in `PrintUpdateHintIfAvailable`, resolve the channel from the active profile:
`var profile = await AppConfig.GetActiveProfileAsync(); var channel = ResolveChannel([], profile?.UpdateChannel);` and pass it to `CheckForUpdateAsync`. Do NOT change how the
existing `update_check` gate is read (it reads the legacy `AppConfig.Load()` and is a
known pre-existing issue — out of scope here; note it for follow-up).

No new `AppConfig.Save` method is needed — persistence goes through the existing
`AppConfig.SaveProfileConfig`.

- [ ] **Step 4: Run to verify unit tests pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/UpdateChannelResolveTests/*"`
Expected: PASS (4 tests).

- [ ] **Step 5: Add a WireMock integration test for the channel query**

```csharp
// test/Capacitor.Cli.Tests.Integration/UpdateChannelQueryTests.cs
// Serve a fake npm registry for both dist-tags and assert the CLI reads the
// right one. If CheckForUpdateAsync's registry base URL is hard-coded, refactor
// it to an internal seam (e.g. an internal static string RegistryBaseUrl = "https://registry.npmjs.org"
// that the test overrides) so the WireMock base URL can be injected. Map:
//   GET /@kurrent/kcap/latest -> {"version":"0.8.0"}
//   GET /@kurrent/kcap/beta   -> {"version":"0.9.0-beta.1"}
// Assert: channel "beta" reports latest="0.9.0-beta.1"; channel "latest" reports "0.8.0".
```

Model this on existing WireMock.Net tests in `test/Capacitor.Cli.Tests.Integration`
(match their server setup/teardown and assertion style). Run:
`dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj --treenode-filter "/*/*/UpdateChannelQueryTests/*"`
Expected: PASS.

- [ ] **Step 6: Verify no AOT warnings**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli/Commands/UpdateCommand.cs test/Capacitor.Cli.Tests.Unit/UpdateChannelResolveTests.cs test/Capacitor.Cli.Tests.Integration/UpdateChannelQueryTests.cs
git commit -m "feat(update): channel-aware update check (--beta/--stable), prerelease-aware comparison"
```

---

### Task 5: Launcher installs the resolved channel

**Files:**
- Modify: `npm/kcap/bin/kcap.js` (the `runUpdate` function, install line ~`:180`)
- Test: `npm/kcap/bin/kcap.test.js`

**Interfaces:**
- Consumes: the `--check` JSON `install_tag` field (Task 4).
- Produces: `function resolveInstallSpec(info)` (exported) → `@kurrent/kcap@<tag>`.

- [ ] **Step 1: Write the failing test**

```js
// npm/kcap/bin/kcap.test.js
const assert = require("node:assert");
const { resolveInstallSpec } = require("./kcap.js");

assert.strictEqual(resolveInstallSpec({ install_tag: "beta" }), "@kurrent/kcap@beta");
assert.strictEqual(resolveInstallSpec({ install_tag: "latest" }), "@kurrent/kcap@latest");
assert.strictEqual(resolveInstallSpec({}), "@kurrent/kcap@latest");          // missing → latest
assert.strictEqual(resolveInstallSpec(null), "@kurrent/kcap@latest");        // no probe → latest
assert.strictEqual(resolveInstallSpec({ install_tag: "" }), "@kurrent/kcap@latest");
console.log("ok");
```

- [ ] **Step 2: Run to verify it fails**

Run: `node npm/kcap/bin/kcap.test.js`
Expected: FAIL — `resolveInstallSpec` is not exported / not defined.

- [ ] **Step 3: Implement and use it**

In `npm/kcap/bin/kcap.js`, add the pure helper near the top (after the requires):

```js
function resolveInstallSpec(info) {
  const tag = info && typeof info.install_tag === "string" && info.install_tag
    ? info.install_tag
    : "latest";
  return `@kurrent/kcap@${tag}`;
}
```

In `runUpdate`, the probe already parses the `--check` JSON into `info` (around
`:135`). Reuse it: replace the hard-coded install spec at `:180`:

```js
  const res = spawnSync("npm", ["install", "-g", "@kurrent/kcap@latest"], {
```

with:

```js
  const res = spawnSync("npm", ["install", "-g", resolveInstallSpec(info)], {
```

Note: `info` is declared inside the `try` at `:130`. Hoist it so it's in scope at the
install call — declare `let info = null;` before the `try`, assign `info = JSON.parse(line);`
inside, and keep the `catch` falling through (so a failed probe → `resolveInstallSpec(null)`
→ `@latest`, preserving today's fallback behavior).

At the end of the file, export the helper for the test (guard so requiring the module
doesn't run the launcher — the launcher body must only execute when run directly):

```js
module.exports = { resolveInstallSpec };
```

Ensure the launcher's top-level exec logic is guarded by `if (require.main === module) { … }`
so `require("./kcap.js")` in the test doesn't spawn the binary. If wrapping the whole
body is too invasive, move only `resolveInstallSpec` above a `require.main === module`
guard around the executable portion.

- [ ] **Step 4: Run to verify it passes**

Run: `node npm/kcap/bin/kcap.test.js`
Expected: `ok`

- [ ] **Step 5: Manually verify the launcher still runs**

Run: `node npm/kcap/bin/kcap.js --version`
Expected: prints the kcap version (launcher still execs the native binary; the export/guard didn't break normal invocation).

- [ ] **Step 6: Commit**

```bash
git add npm/kcap/bin/kcap.js npm/kcap/bin/kcap.test.js
git commit -m "feat(launcher): install the update channel's dist-tag from the probe (beta opt-in)"
```

---

### Task 6: Docs — README + `update` help

**Files:**
- Modify: `README.md` (`## Getting started` and the `kcap update` entry under `## CLI commands`)
- Modify: `src/Capacitor.Cli.Core/Resources/help-update.txt` (verify exact filename with `ls src/Capacitor.Cli.Core/Resources/help-*.txt`)

**Interfaces:**
- Consumes: the `--beta`/`--stable` flags and channel behavior (Task 4).
- Produces: user-facing docs. No code interface.

- [ ] **Step 1: Update the `update` help text**

In the update help resource, add the two flags and the channel note, e.g.:

```
kcap update [--check] [--beta | --stable]

  Update the kcap CLI to the latest published version.

  --beta     Switch to the beta release channel and update to the latest beta.
             The choice is remembered; future updates track beta until --stable.
  --stable   Switch back to the stable channel (the default).
  --check    Print a machine-readable JSON status line and exit.

  Beta releases match server versions rolled out to internal tenants first.
  Most users should stay on the default stable channel.
```

- [ ] **Step 2: Update README**

Under `## CLI commands` → the `kcap update` section, document `--beta`/`--stable`, that
the channel is persisted, and that the default is stable. In `## Getting started`, add a
one-line note that internal-tenant testers can opt into betas with `kcap update --beta`.
Keep wording consistent with the help text from Step 1.

- [ ] **Step 3: Verify the help resource is embedded and builds**

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
Expected: build succeeds (embedded `help-update.txt` change compiled in).

- [ ] **Step 4: Commit**

```bash
git add README.md src/Capacitor.Cli.Core/Resources/help-update.txt
git commit -m "docs: document kcap update --beta/--stable and the beta channel"
```

---

## Self-Review

**Spec coverage (Component 5 + release invariant):**
- release.yml prerelease → `--tag beta`, `latest` untouched, non-prerelease → `latest` → Task 2 ✓
- Channel-aware opt-in update path (launcher + UpdateCommand read the beta dist-tag) → Tasks 4, 5 ✓; persisted config opt-in → Tasks 3, 4 ✓
- Prerelease-aware SemVer 2.0 comparator → Task 1 ✓ (used in Task 4)
- Release invariant (internal-first → prerelease; `latest` only when stable/micro ready) → Task 2 `RELEASING.md` ✓ (documented process; workflow guarantees the tag→dist-tag half)
- README/help sync → Task 6 ✓
- Testing bullets (comparator, release workflow mapping, channel query) → Tasks 1, 2, 4 ✓
- AOT clean → Task 4 Step 6 ✓
- Out of scope for Phase 1 (correctly deferred): the shared version cache, MinServerVersion table, MCP tool filtering, daemon pre-connect probe, `/auth/config` version field, reactive backstop — these are Phase 2/3 plans.

**Placeholder scan:** No "TBD"/"handle edge cases"/"write tests for the above" — every code step has concrete code. The two soft spots are (a) the WireMock test (Task 4 Step 5), which points at existing integration-test patterns rather than inventing a server harness, and (b) exact filenames for the help resource / `InternalsVisibleTo` presence — both call out an explicit verification command rather than guessing.

**Type consistency:** `PrereleaseSemver.IsNewer` (Task 1) is the name used in Task 4 Step 3(c). `CapacitorConfig.UpdateChannel` (Task 3) is read in Task 4 Step 3(d). `ResolveChannel(string[], string?)` (Task 4) matches its tests. The `--check` JSON key `install_tag` (Task 4 Step 3) is exactly the key `resolveInstallSpec` reads (Task 5). Channel strings are the literal npm dist-tags `"latest"`/`"beta"` throughout.
