# TokenStore Corruption Resilience Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `kcap` from crashing on a corrupt local token file, and stop concurrent writers from producing one.

**Architecture:** Two centralized changes in `TokenStore` (`src/Capacitor.Cli.Core/Auth/TokenStore.cs`). (1) A private `ReadTokensAsync(path)` reader wraps the deserialize in `try { … } catch (JsonException) { return null; }`; both `LoadAsync` overloads route through it, so a corrupt/empty/hand-edited file degrades to "not authenticated" across every caller. (2) `SaveAsync` writes to a per-write **unique** temp filename (`{path}.{pid}.{guid}.tmp`) instead of a shared `{path}.tmp`, with best-effort temp cleanup in `finally`; the existing atomic `File.Move` then guarantees the destination is always one complete document. No call sites change.

**Tech Stack:** .NET 10, NativeAOT, `System.Text.Json` source-gen (`CapacitorJsonContext`), TUnit on Microsoft Testing Platform.

**Issue:** [AI-1082](https://linear.app/kurrent/issue/AI-1082/kcap-crashes-on-corrupt-token-file-harden-tokenstore-load-save)
**Spec:** `docs/superpowers/specs/2026-06-30-token-store-corruption-resilience-design.md`

## Global Constraints

- Build/test/publish with `~/.dotnet/dotnet` — the PATH `dotnet` is 8.0 and cannot target .NET 10.
- Catch **`JsonException` only** on the read path — not `IOException`/`UnauthorizedAccessException` (those are real faults that must not be masked as "not authenticated"). Empty files surface as `JsonException`, so they are covered.
- AOT: no new reflection/dynamic codegen. After changes, `dotnet publish -c Release` must show **no** IL2026/IL3050 warnings (`CLAUDE.md`).
- No public API or call-site changes; no `README.md` change required (internal robustness only — no user-facing CLI surface change).
- TUnit filtering uses `--treenode-filter` glob syntax, NOT `--filter`.

---

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `src/Capacitor.Cli.Core/Auth/TokenStore.cs` | Add `ReadTokensAsync` reader; route both `LoadAsync` overloads through it; unique temp name + `finally` cleanup in `SaveAsync` | Modify |
| `test/Capacitor.Cli.Tests.Unit/TokenStoreProfileTests.cs` | Add resilience + concurrency regression tests (reuses the class's `KCAP_CONFIG_DIR` temp-dir harness, `[Before(Test)]` cleanup, and `MakeTokens`) | Modify |

**Reference files to read before starting:**
- `src/Capacitor.Cli.Core/Auth/TokenStore.cs` — current `LoadAsync` (`:55`, `:86`) and `SaveAsync` (`:62`).
- `test/Capacitor.Cli.Tests.Unit/TokenStoreProfileTests.cs` — existing harness, cleanup, and `MakeTokens` helper to reuse.
- `src/Capacitor.Cli.Core/HttpClientExtensions.cs:47` — the hottest caller (Bearer header) that currently crashes on a corrupt file.

**Build / test commands:**
- Build: `~/.dotnet/dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
- These tests: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TokenStoreProfileTests/*"`
- Full unit suite: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
- AOT check: `~/.dotnet/dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` (expect no output)

---

## Task 1: Tolerate a corrupt token file on read

Make `LoadAsync` return `null` for an unparseable file instead of throwing. This is the customer-facing fix; the regression test (Step 1c) reproduces the exact crash through the public entry point.

**Files:**
- Modify: `src/Capacitor.Cli.Core/Auth/TokenStore.cs` (`LoadAsync(string)` at `:55`, legacy branch of `LoadAsync()` at `:92-94`)
- Test: `test/Capacitor.Cli.Tests.Unit/TokenStoreProfileTests.cs`

**Interfaces:**
- Produces: `static Task<StoredTokens?> TokenStore.ReadTokensAsync(string path)` (private) — returns `null` if the file is missing or its contents are not valid `StoredTokens` JSON; otherwise the deserialized tokens.
- Consumes: existing `CapacitorJsonContext.Default.StoredTokens`, `ProfileTokenPath(profile)`, `LegacyTokenPath`, and the test class's `MakeTokens(string)` + `TokensDir`/`LegacyPath` helpers.

- [ ] **Step 1: Write the failing tests**

Add to `test/Capacitor.Cli.Tests.Unit/TokenStoreProfileTests.cs` (inside the class; reuses its `[Before(Test)]` cleanup, `TokensDir`, `LegacyPath`, and `MakeTokens`):

```csharp
[Test]
public async Task LoadAsync_with_corrupt_json_returns_null() {
    Directory.CreateDirectory(TokensDir);
    var valid   = System.Text.Json.JsonSerializer.Serialize(MakeTokens("alice"), CapacitorJsonContext.Default.StoredTokens);
    var corrupt = valid + ",\"provider\":\"workos\"}"; // complete object, then stray comma + tail (the customer's signature)
    await File.WriteAllTextAsync(Path.Combine(TokensDir, "acme.json"), corrupt);

    var loaded = await TokenStore.LoadAsync("acme");

    await Assert.That(loaded).IsNull();
}

[Test]
public async Task LoadAsync_with_empty_file_returns_null() {
    Directory.CreateDirectory(TokensDir);
    await File.WriteAllTextAsync(Path.Combine(TokensDir, "acme.json"), "");

    await Assert.That(await TokenStore.LoadAsync("acme")).IsNull();
}

[Test]
[NotInParallel(nameof(TokenStoreProfileTests))]
public async Task LoadAsync_legacy_corrupt_file_returns_null() {
    Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
    await File.WriteAllTextAsync(LegacyPath, "{\"access_token\":\"x\"},garbage");

    // No per-profile file exists, so the parameterless LoadAsync() falls back to the legacy path.
    await Assert.That(await TokenStore.LoadAsync()).IsNull();
}

[Test]
[NotInParallel(nameof(TokenStoreProfileTests))]
public async Task GetValidTokensAsync_with_corrupt_file_returns_null() {
    // Reproduces the customer crash through the public entry point StatusCommand uses.
    Directory.CreateDirectory(TokensDir);
    var valid   = System.Text.Json.JsonSerializer.Serialize(MakeTokens("alice"), CapacitorJsonContext.Default.StoredTokens);
    await File.WriteAllTextAsync(Path.Combine(TokensDir, "default.json"), valid + ",\"x\":1}");

    await Assert.That(await TokenStore.GetValidTokensAsync()).IsNull();
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TokenStoreProfileTests/*"`
Expected: the four new tests FAIL — each throws `System.Text.Json.JsonException` ("',' is invalid after a single JSON value" / "input does not contain any JSON tokens") out of `LoadAsync`, exactly as the customer saw.

- [ ] **Step 3: Add the resilient reader and route both load paths through it**

In `src/Capacitor.Cli.Core/Auth/TokenStore.cs`, add the private reader (place it just above the profile-aware `LoadAsync(string)`):

```csharp
// A corrupt, empty, partially-written, or hand-edited token file is equivalent to
// "no usable credentials": return null so the CLI degrades to "run kcap login"
// instead of throwing JsonException out of every command, hook, the daemon, and MCP.
// The next successful login/refresh overwrites the file. Catch JsonException only —
// IO/permission errors are real faults that must not be masked as unauthenticated.
static async Task<StoredTokens?> ReadTokensAsync(string path) {
    if (!File.Exists(path)) return null;
    var json = await File.ReadAllTextAsync(path);
    try {
        return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.StoredTokens);
    } catch (JsonException) {
        return null;
    }
}
```

Replace the body of `LoadAsync(string profile)` (`:55-60`) with:

```csharp
public static async Task<StoredTokens?> LoadAsync(string profile) {
    return await ReadTokensAsync(ProfileTokenPath(profile));
}
```

Replace the legacy fallback in `LoadAsync()` (`:91-94`) — the two lines that read `LegacyTokenPath` and deserialize — with:

```csharp
        // Fall back to legacy single-file layout for pre-upgrade installs
        return await ReadTokensAsync(LegacyTokenPath);
```

(`ProfileTokenPath` still runs `ValidateProfileName`, so the profile-name guard is preserved.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TokenStoreProfileTests/*"`
Expected: PASS — all four new tests plus the pre-existing `TokenStoreProfileTests` (the happy-path `LoadAsync` tests still return real tokens through the new reader).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Auth/TokenStore.cs test/Capacitor.Cli.Tests.Unit/TokenStoreProfileTests.cs
git commit -m "fix(auth): tolerate corrupt token file on read (AI-1082)

LoadAsync now returns null for unparseable token files instead of
throwing JsonException, which previously crashed kcap status, hooks,
the daemon, MCP, and all authenticated HTTP."
```

---

## Task 2: Unique temp filename on write

Stop concurrent `SaveAsync` calls from corrupting the shared temp file — the source of the on-disk corruption in the first place.

**Files:**
- Modify: `src/Capacitor.Cli.Core/Auth/TokenStore.cs` (`SaveAsync(string, StoredTokens)` at `:62`)
- Test: `test/Capacitor.Cli.Tests.Unit/TokenStoreProfileTests.cs`

**Interfaces:**
- Consumes: `ProfileTokenPath`, `TokenDir`, `CapacitorJsonContext.Default.StoredTokens`, `MakeTokens`. No signature change to `SaveAsync`.

- [ ] **Step 1: Write the failing tests**

Add to `TokenStoreProfileTests`:

```csharp
[Test]
[NotInParallel(nameof(TokenStoreProfileTests))]
public async Task SaveAsync_concurrent_writes_never_corrupt() {
    // Alternate long (WorkOS-JWT-sized) and short (GitHub-token-sized) payloads so a
    // shorter write landing over a longer one would splice — the byte-492 signature.
    var longTok  = MakeTokens("alice") with { AccessToken = new string('A', 1200) };
    var shortTok = MakeTokens("bob")   with { AccessToken = "gho_short" };

    var writers = Enumerable.Range(0, 64)
        .Select(i => TokenStore.SaveAsync("race", i % 2 == 0 ? longTok : shortTok));
    await Task.WhenAll(writers);

    // Whoever wrote last wins, but the file must always be a single complete document.
    var loaded = await TokenStore.LoadAsync("race");
    await Assert.That(loaded).IsNotNull();
}

[Test]
[NotInParallel(nameof(TokenStoreProfileTests))]
public async Task SaveAsync_leaves_no_temp_residue() {
    await TokenStore.SaveAsync("acme", MakeTokens("alice"));

    var stray = Directory.EnumerateFiles(TokensDir, "*.tmp").ToArray();
    await Assert.That(stray).IsEmpty();
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TokenStoreProfileTests/SaveAsync_concurrent_writes_never_corrupt" --treenode-filter "/*/*/TokenStoreProfileTests/SaveAsync_leaves_no_temp_residue"`
Expected: `SaveAsync_concurrent_writes_never_corrupt` FAILS intermittently against the shared `{path}.tmp` (a spliced file → `LoadAsync` returns null after Task 1, or threw pre-Task-1). Run it a few times to observe the race. (`SaveAsync_leaves_no_temp_residue` passes today because the shared temp is always renamed away — it's the guard that the new `finally` cleanup doesn't regress.)

- [ ] **Step 3: Implement the unique temp name + finally cleanup**

In `src/Capacitor.Cli.Core/Auth/TokenStore.cs`, replace the body of `SaveAsync(string profile, StoredTokens tokens)` (`:62-77`) with:

```csharp
public static async Task SaveAsync(string profile, StoredTokens tokens) {
    Directory.CreateDirectory(TokenDir);
    var path     = ProfileTokenPath(profile);
    // Unique per write so concurrent writers (hooks/watcher/daemon/MCP/login share this
    // store) never write the same temp file and splice each other's bytes. The atomic
    // File.Move then publishes one complete document, last-writer-wins.
    var tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

    try {
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(tokens, CapacitorJsonContext.Default.StoredTokens));
        File.Move(tempPath, path, overwrite: true);
    } finally {
        // Success renames the temp away; only a failed write/move leaves it. Unlike the
        // old shared name, a leaked unique temp never gets reused, so clean it up.
        if (File.Exists(tempPath)) {
            try { File.Delete(tempPath); } catch { /* best-effort */ }
        }
    }

    if (!OperatingSystem.IsWindows()) {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // Migration: remove the pre-upgrade single-file token if it still exists
    if (File.Exists(LegacyTokenPath)) {
        try { File.Delete(LegacyTokenPath); } catch { /* best-effort */ }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TokenStoreProfileTests/*"`
Expected: PASS, including repeated runs of `SaveAsync_concurrent_writes_never_corrupt` (no splicing) and `SaveAsync_leaves_no_temp_residue` (the unique temp is renamed away; the `finally` is a no-op on success).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Auth/TokenStore.cs test/Capacitor.Cli.Tests.Unit/TokenStoreProfileTests.cs
git commit -m "fix(auth): unique temp filename per token write (AI-1082)

Concurrent SaveAsync calls shared {path}.tmp and could splice a shorter
token over a longer one, producing the corrupt file behind AI-1082. Each
write now uses a {pid}.{guid} temp, cleaned up on failure; the atomic
rename publishes one complete document."
```

---

## Task 3: Full-suite + AOT verification gate

No new code — the mandatory gates from `CLAUDE.md` before the PR.

- [ ] **Step 1: Run the full unit suite**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: all tests PASS (no regressions in the broader suite).

- [ ] **Step 2: AOT publish + warning grep**

Run: `~/.dotnet/dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: **no output** (no IL2026/IL3050 trimming/AOT warnings).

- [ ] **Step 3: Open the PR**

```bash
gh pr create --title "fix(auth): harden TokenStore against corrupt token files (AI-1082)" \
  --body "Fixes the unhandled JsonException crash a customer hit on \`kcap status\`. LoadAsync now degrades a corrupt token file to \"not authenticated\" instead of crashing every command/hook/daemon/MCP; SaveAsync uses a unique temp file per write so concurrent writers can't splice one. See docs/superpowers/specs/2026-06-30-token-store-corruption-resilience-design.md.

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

---

## Self-Review

- **Spec coverage:** Fix 1 (resilient read) → Task 1; Fix 2 (unique temp write) → Task 2; AOT/README/suite gates → Task 3. Test plan items 1–6 → Task 1 Steps 1 (items 1–4) and Task 2 Step 1 (items 5–6). All spec sections covered.
- **Placeholder scan:** no TBD/TODO/"handle edge cases"; every code step shows full code; every run step states expected output.
- **Type consistency:** `ReadTokensAsync(string)` defined in Task 1 and used by both `LoadAsync` overloads; `MakeTokens`, `TokensDir`, `LegacyPath` are the existing test-class members; `StoredTokens` `with`-expressions use real properties (`AccessToken`). `SaveAsync` signature unchanged, so no caller updates needed.
