# Profiles Settings PR 1 — List, Status, Sign in, Remove — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Profiles tab to the desktop app's Settings window that lists every profile with its credential status, offers Sign in and Remove, and make the removal and credential-write paths in `Capacitor.Cli.Core` safe for both the app and the CLI to share.

**Architecture:** The tab is a third `TabItem` hosting a `ProfilesSettingsView` with its own `ProfilesSettingsViewModel`, which reads `config.json` with `ConfigMutator.TryLoadPure` and grades each profile with the refresh-free `OnboardingGate.EvaluateResolvedAsync`. Removal lives in Core as `ProfileRemoval` and is called by both the app and `kcap profile remove`. Every new config mutation goes through a strict variant that refuses an unreadable file, and every write to a token file goes through the profile's cross-process lock with an optional guard evaluated under it. The sign-in commit boundary gains a commit-time precondition and reports whether the credential was actually saved.

**Tech Stack:** .NET 10, NativeAOT; Avalonia 12 + ReactiveUI for the app; TUnit on Microsoft Testing Platform; WireMock.Net / `AuthHttp.Script` for auth HTTP scripting.

**Spec:** `docs/superpowers/specs/2026-09-21-ai3072-desktop-profiles-settings-design.md` — this plan implements its "Delivery" item 1 (List, status, Sign in, Remove). Sections "Profiles tab", "Sign in", "Strict config mutation", "Commit guards", "Remove" and "Token lock" are the requirements; "Add" and "Switch" are PRs 2 and 3 and are out of scope here except where a seam is shared.

## Global Constraints

- Every PR references both issues in its description: `Closes` is NOT used here (PR 3 closes); the reference line reads `Part of #1093` and `AI-3072`. The title carries no reference.
- Commit subjects: one imperative clause, at most 80 characters including the trailing `(#1093)`. Body ≤ 5 lines, only for a constraint the diff does not show. End every commit message with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Comments are scarce: no design/spec coordinates, no change narration, no ticket ids. A comment names a trap or a deliberate decision, or is not written. Do not imitate the density of existing comments.
- One type per file, named after the type. Exceptions used here: a closely related record hierarchy in one file (`CommitPrecondition` and its nested `ExpectServer`).
- Return `FrozenDictionary<K,V>.Empty` / `FrozenSet<T>.Empty` for an empty read-only collection. `Environment.GetFolderPath` is banned.
- Tests: one test project per prod project; throwaway config roots come from `[TempConfigRoot] public required TempConfigRoot Config { get; init; }`; `AuthFixtures.NewTokenStore(Config.Root)` builds a `TokenStore`; every Avalonia UI test carries `[NotInParallel("AvaloniaSession")]` and runs inside `AvaloniaSession.RunOnUiAsync`; the throws idiom is `await Assert.That(() => ...).Throws<T>()` and counts are `Assert.That(x.Count).IsEqualTo(n)`.
- Run one test class with `--treenode-filter "/*/*/<ClassName>/*"`, never `--filter`.
- Desktop status colours: `KcapSuccess*`/`KcapWarning*` only for outcome or attention. The Active and This-app marks use `KcapPurpleBrush` (location). Controls opt into `kcapChip`, `kcapPrimary`, `kcapField`, `kcapHint`, `kcapLabel`.
- CLI surface changes update `README.md` and the `help-*.txt` in the same PR.
- The daemon assembly never calls `Process.Kill(bool)`; not touched here.
- After the last task: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` must print nothing.
- Git in this worktree: run as `/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 <args>`; never bare `git stash`.

---

## File structure

**Core (`src/Capacitor.Cli.Core/`)**
- `Config/ConfigUnreadableException.cs` — create. Thrown by the strict mutation.
- `Config/ConfigMutator.cs` — modify: `MutateStrict` / `MutateStrictAsync`.
- `Config/ProfileRemovalOutcome.cs`, `Config/ProfileRemovalResult.cs`, `Config/ProfileRemoval.cs` — create. Shared removal.
- `Auth/GuardedWriteOutcome.cs` — create. Result of a guarded token write.
- `Auth/TokenStore.cs` — modify: lock helper, `SaveLockedAsync`, `SaveGuardedAsync`, `DeleteGuardedAsync`, `MigrateLegacyAsync`, locked `DeleteAsync()`, pure `IsLegacyOwner`, reload under the lock, `TokenPath`; remove `Delete(string)`.
- `Auth/CommitPrecondition.cs`, `Auth/CommitPreconditionFailedException.cs` — create.
- `Auth/AuthResult.cs` — modify: `Committed.CredentialSaved`.
- `Auth/OnboardingFacade.cs` — modify: `CommitRequest.Precondition`, strict commit, guarded saves, `LoginTarget.ExistedAtRead`/`Precondition`/`SaveGuard`, `LoginAsync(precondition:)`, legacy settlement before discovery.
- `Auth/WorkOSDiscovery.cs` — modify: legacy settlement and guarded save.
- `Resources/help-profile.txt` — modify.

**CLI (`src/Capacitor.Cli/`)**
- `Commands/ProfileCommand.cs` — modify: `remove` through `ProfileRemoval`; ctor takes `TokenStore`.
- `Commands/UseCommand.cs` — modify: strict decision, legacy migration; ctor takes `TokenStore`.
- `Commands/LoginCommand.cs` — modify: exit 1 when the credential was not saved.

**App (`src/Capacitor.App/`)**
- `ViewModels/ProfileCredentialStatus.cs`, `ViewModels/ProfileRow.cs`, `ViewModels/ProfilesSettingsViewModel.cs` — create.
- `ViewModels/SettingsViewModel.cs` — modify: `Profiles` property.
- `Views/ProfilesSettingsView.axaml` + `.axaml.cs` — create.
- `Views/SettingsWindow.axaml` — modify: third tab.
- `Services/ILifecycleSurface.cs` — modify: `LifecyclePrompt.KindRemoveProfile`.
- `ViewModels/LifecyclePromptViewModel.cs` — modify: title and button for the new kind.
- `Services/Onboarding/WizardComposition.cs`, `Services/Onboarding/WizardAuthBridges.cs`, `Services/Onboarding/ReauthComposition.cs` — modify: thread `CommitPrecondition`.
- `ViewModels/Onboarding/SignInStepViewModel.cs` — modify: gate success on `CredentialSaved`.
- `App.axaml.cs` — modify: `OpenSignInDialog(profile, serverUrl, refreshAppState, notifier)`, `OpenSettings` builds the profiles view model.

**Docs**
- `README.md` (Profiles section), `docs/CHANGES.md`.

**Tests** — mirrored paths under `test/Capacitor.Cli.Core.Tests.Unit/`, `test/Capacitor.Cli.Tests.Unit/`, `test/Capacitor.App.Tests.Unit/`.

---

### Task 1: `ConfigMutator.MutateStrictAsync`

**Files:**
- Create: `src/Capacitor.Cli.Core/Config/ConfigUnreadableException.cs`
- Modify: `src/Capacitor.Cli.Core/Config/ConfigMutator.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Config/ConfigMutatorTests.cs`

**Interfaces:**
- Produces: `public static Task<ProfileConfig> ConfigMutator.MutateStrictAsync(ConfigRoot config, Func<ProfileConfig, ProfileConfig> mutate, CancellationToken ct = default)`, `public static ProfileConfig ConfigMutator.MutateStrict(ConfigRoot, Func<ProfileConfig, ProfileConfig>)`, `public sealed class ConfigUnreadableException : IOException { string ConfigPath }`.

- [ ] **Step 1: Write the failing tests**

Append to `ConfigMutatorTests`:

```csharp
    [Test]
    public async Task MutateStrict_refuses_a_malformed_file_and_leaves_it_untouched() {
        var path = AppConfig.GetConfigPath(Config.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ not json");
        var invoked = false;

        await Assert.That(() => ConfigMutator.MutateStrictAsync(Config.Root, c => { invoked = true; return c with { MachineId = "m" }; }))
            .Throws<ConfigUnreadableException>();

        await Assert.That(invoked).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("{ not json");
    }

    [Test]
    public async Task MutateStrict_publishes_into_a_fresh_config_when_the_file_is_absent() {
        var next = await ConfigMutator.MutateStrictAsync(Config.Root, c => c with { MachineId = "m" });

        await Assert.That(next.MachineId).IsEqualTo("m");
        await Assert.That((await AppConfig.LoadProfileConfig(Config.Root)).MachineId).IsEqualTo("m");
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ConfigMutatorTests/*"`
Expected: compile error — `MutateStrictAsync` and `ConfigUnreadableException` do not exist.

- [ ] **Step 3: Implement**

Create `src/Capacitor.Cli.Core/Config/ConfigUnreadableException.cs`:

```csharp
namespace Capacitor.Cli.Core.Config;

/// Thrown by <see cref="ConfigMutator.MutateStrict"/> for a config.json that exists but cannot be
/// read: deciding a mutation against the fresh default <see cref="ConfigMutator.TryLoadPure"/> hands
/// back would publish that default over the user's file.
public sealed class ConfigUnreadableException(string configPath)
    : IOException($"The configuration file at {configPath} exists but could not be read.") {
    public string ConfigPath { get; } = configPath;
}
```

In `ConfigMutator.cs`, after `Mutate`:

```csharp
    /// <see cref="MutateAsync"/> for a mutation whose decision depends on what the file says: an
    /// unreadable file aborts before the callback runs and nothing is published. An absent file is a
    /// fresh config, as in <see cref="Mutate"/>.
    public static Task<ProfileConfig> MutateStrictAsync(
            ConfigRoot config, Func<ProfileConfig, ProfileConfig> mutate, CancellationToken ct = default) =>
        Task.Run(() => MutateStrict(config, mutate), ct);

    public static ProfileConfig MutateStrict(ConfigRoot config, Func<ProfileConfig, ProfileConfig> mutate) {
        var path = AppConfig.GetConfigPath(config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (config.AcquireLock(AppConfig.ConfigFileName)) {
            if (!TryLoadPure(path, out var current)) throw new ConfigUnreadableException(path);
            var next = mutate(current);
            Publish(path, next);
            return next;
        }
    }
```

- [ ] **Step 4: Run to verify they pass**

Same command. Expected: all `ConfigMutatorTests` pass.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.Cli.Core/Config/ConfigUnreadableException.cs src/Capacitor.Cli.Core/Config/ConfigMutator.cs test/Capacitor.Cli.Core.Tests.Unit/Config/ConfigMutatorTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Add a config mutation that refuses an unreadable file (#1093)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Token lock seams — guarded save, owner-aware legacy delete

**Files:**
- Create: `src/Capacitor.Cli.Core/Auth/GuardedWriteOutcome.cs`
- Modify: `src/Capacitor.Cli.Core/Auth/TokenStore.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Auth/TokenStoreProfileTests.cs`

**Interfaces:**
- Produces: `public enum GuardedWriteOutcome { Written, GuardRefused, ConfigUnreadable }`; `public Task TokenStore.SaveAsync(string profile, StoredTokens tokens, CancellationToken ct = default)` (unchanged signature, now locked); `public Task<GuardedWriteOutcome> TokenStore.SaveGuardedAsync(string profile, StoredTokens tokens, Func<ProfileConfig, bool>? guard, CancellationToken ct = default)`; `public string TokenStore.TokenPath(string profile)`; private `Task<FileStream?> TryAcquireProfileLockAsync(string, CancellationToken)`, `Task<FileStream> AcquireProfileLockAsync(string, CancellationToken)`, `Task SaveLockedAsync(string, StoredTokens, CancellationToken)`, `bool IsLegacyOwner(string)`, `GuardedWriteOutcome Evaluate(Func<ProfileConfig,bool>?)`.

- [ ] **Step 1: Write the failing tests**

In `TokenStoreProfileTests`, change the existing `Legacy_tokens_json_is_migrated_on_first_profile_save` so the saving profile is the owner (the legacy file follows the active profile, and `acme` is not active in that test today). Replace its body's first lines with:

```csharp
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = "acme",
            Profiles = new Dictionary<string, Profile> { ["acme"] = new() { ServerUrl = "https://acme.example" } }
        });
```

(keep the rest: write the legacy file, save `acme`, assert the legacy file is gone and `acme.json` exists). Then add:

```csharp
    [Test]
    public async Task Save_for_another_profile_leaves_the_active_profiles_legacy_credential() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = "a",
            Profiles = new Dictionary<string, Profile> {
                ["a"] = new() { ServerUrl = "https://a.example" }, ["b"] = new() { ServerUrl = "https://b.example" }
            }
        });
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        await File.WriteAllTextAsync(LegacyPath,
            System.Text.Json.JsonSerializer.Serialize(MakeTokens("legacy-a"), CapacitorJsonContext.Default.StoredTokens));

        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("b", MakeTokens("bob"));

        await Assert.That(File.Exists(LegacyPath)).IsTrue();
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadForProfileAsync("a"))!.GitHubUsername).IsEqualTo("legacy-a");
    }

    [Test]
    public async Task SaveGuardedAsync_writes_only_when_the_guard_passes() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles = new Dictionary<string, Profile> { ["acme"] = new() { ServerUrl = "https://acme.example" } }
        });
        var store = AuthFixtures.NewTokenStore(Config.Root);

        var refused = await store.SaveGuardedAsync("ghost", MakeTokens("x"), cfg => cfg.Profiles.ContainsKey("ghost"));
        var written = await store.SaveGuardedAsync("acme", MakeTokens("alice"), cfg => cfg.Profiles.ContainsKey("acme"));

        await Assert.That(refused).IsEqualTo(GuardedWriteOutcome.GuardRefused);
        await Assert.That(File.Exists(Path.Combine(TokensDir, "ghost.json"))).IsFalse();
        await Assert.That(written).IsEqualTo(GuardedWriteOutcome.Written);
        await Assert.That(File.Exists(Path.Combine(TokensDir, "acme.json"))).IsTrue();
    }

    [Test]
    public async Task SaveGuardedAsync_reports_an_unreadable_config_and_writes_nothing() {
        var configPath = AppConfig.GetConfigPath(Config.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, "{ not json");

        var outcome = await AuthFixtures.NewTokenStore(Config.Root).SaveGuardedAsync("acme", MakeTokens("alice"), _ => true);

        await Assert.That(outcome).IsEqualTo(GuardedWriteOutcome.ConfigUnreadable);
        await Assert.That(File.Exists(Path.Combine(TokensDir, "acme.json"))).IsFalse();
    }

    [Test]
    public async Task SaveAsync_waits_for_a_peer_holding_the_profile_lock() {
        Directory.CreateDirectory(TokensDir);
        var store = AuthFixtures.NewTokenStore(Config.Root);
        Task save;
        using (new FileStream(Path.Combine(TokensDir, "acme.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            save = store.SaveAsync("acme", MakeTokens("alice"));
            await Task.Delay(200);
            await Assert.That(save.IsCompleted).IsFalse();
        }
        await save;

        await Assert.That(File.Exists(Path.Combine(TokensDir, "acme.json"))).IsTrue();
    }
```

Add `using Capacitor.Cli.Core.Config;` if missing (it is present).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/TokenStoreProfileTests/*"`
Expected: compile error on `SaveGuardedAsync` / `GuardedWriteOutcome`.

- [ ] **Step 3: Implement**

Create `src/Capacitor.Cli.Core/Auth/GuardedWriteOutcome.cs`:

```csharp
namespace Capacitor.Cli.Core.Auth;

/// What a guarded token write did. An unreadable config is its own outcome: the fresh default
/// <c>TryLoadPure</c> hands back would pass most guards for the wrong reason.
public enum GuardedWriteOutcome { Written, GuardRefused, ConfigUnreadable }
```

In `TokenStore.cs`:

1. Add `using Capacitor.Cli.Core.Config;` if not present.
2. Replace the existing `public async Task SaveAsync(string profile, StoredTokens tokens, CancellationToken ct = default) { ... }` with:

```csharp
    /// The file a profile's credential lives in, for messages that name it.
    public string TokenPath(string profile) => ProfileTokenPath(profile);

    public Task SaveAsync(string profile, StoredTokens tokens, CancellationToken ct = default) =>
        SaveGuardedAsync(profile, tokens, guard: null, ct);

    /// Saves under the profile's cross-process lock when <paramref name="guard"/> holds against the
    /// config as it is at that moment; a null guard always writes.
    public async Task<GuardedWriteOutcome> SaveGuardedAsync(
            string profile, StoredTokens tokens, Func<ProfileConfig, bool>? guard, CancellationToken ct = default) {
        using var lockStream = await AcquireProfileLockAsync(profile, ct);
        var outcome = Evaluate(guard);
        if (outcome == GuardedWriteOutcome.Written) await SaveLockedAsync(profile, tokens, ct);
        return outcome;
    }

    // The write itself, for a caller that already holds the profile's lock.
    async Task SaveLockedAsync(string profile, StoredTokens tokens, CancellationToken ct) {
        Directory.CreateDirectory(TokenDir);
        var path     = ProfileTokenPath(profile);
        // Unique per write so concurrent writers never splice each other's bytes; the atomic
        // File.Move then publishes one complete document, last-writer-wins.
        var tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        // Owner-only from the first byte: a chmod after writing would leave a window where the
        // secret is group/world-readable under the process umask.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try {
            await using (var stream = new FileStream(tempPath, options))
            await using (var writer = new StreamWriter(stream)) {
                await writer.WriteAsync(
                    JsonSerializer.Serialize(tokens, CapacitorJsonContext.Default.StoredTokens).AsMemory(), ct);
            }
            await ReplaceWithRetryAsync(tempPath, path, time, ct);
        } finally {
            if (File.Exists(tempPath)) {
                try { File.Delete(tempPath); } catch { /* best-effort */ }
            }
        }

        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Only the owner's save retires the legacy file: another profile's save leaving it is what
        // keeps the active profile's only credential alive.
        if (File.Exists(LegacyTokenPath) && IsLegacyOwner(profile)) {
            try { File.Delete(LegacyTokenPath); } catch { /* best-effort */ }
        }
    }

    // A guard reads config without the migrating loader; a read that fails is not a pass.
    GuardedWriteOutcome Evaluate(Func<ProfileConfig, bool>? guard) {
        if (guard is null) return GuardedWriteOutcome.Written;
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var cfg)) return GuardedWriteOutcome.ConfigUnreadable;
        return guard(cfg) ? GuardedWriteOutcome.Written : GuardedWriteOutcome.GuardRefused;
    }

    // One lock file per profile, exclusive while open. Null once the holder has kept it past LockWait.
    async Task<FileStream?> TryAcquireProfileLockAsync(string profile, CancellationToken ct) {
        ValidateProfileName(profile);
        Directory.CreateDirectory(TokenDir);
        var lockPath = Path.Combine(TokenDir, $"{profile}.lock");
        var deadline = time.GetUtcNow() + LockWait;

        while (true) {
            ct.ThrowIfCancellationRequested();
            try {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            } catch (IOException) {
                if (time.GetUtcNow() >= deadline) return null;
                await Task.Delay(LockPollGap, time, ct);
            }
        }
    }

    async Task<FileStream> AcquireProfileLockAsync(string profile, CancellationToken ct) =>
        await TryAcquireProfileLockAsync(profile, ct)
        ?? throw new TimeoutException($"The token file for profile '{profile}' is locked by another process.");
```

3. Replace `async Task<bool> IsLegacyOwnerAsync(string profile, CancellationToken ct)` with a pure read, and update its one caller in `LoadWithLegacyFallbackAsync` (`await IsLegacyOwnerAsync(profile, ct)` → `IsLegacyOwner(profile)`):

```csharp
    // The legacy credential's owner is the on-disk active profile, with an absent/empty value
    // normalizing to "default". Deliberately NOT "profile == active || profile == default": with
    // active profile Y, a resolution landing on "default" must not pick up Y's legacy credential.
    // A pure read: the migrating loader could take the config lock under the token lock.
    bool IsLegacyOwner(string profile) {
        ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var cfg);
        return string.Equals(profile, cfg.ActiveName, StringComparison.Ordinal);
    }
```

4. In `RefreshWithCrossProcessLockAsync`, replace the block from `// Validate before building the lock path` through the end of the `while (lockStream is null) { ... }` loop with:

```csharp
        var lockStream = await TryAcquireProfileLockAsync(profile, cancellationToken);

        if (lockStream is null) {
            var latest = await LoadAsync(profile);

            // A peer refreshed while we waited → return their fresh token.
            if (latest is not null && !needsRefresh(latest)) {
                return latest;
            }

            // Gave up still-due: a peer holds the lock (likely mid-refresh). Signal contention so
            // the proactive caller doesn't report this as a refresh failure.
            onLockContended?.Invoke();

            return null;
        }
```

   and inside the `try` block change `await SaveAsync(profile, refreshed, cancellationToken);` to `await SaveLockedAsync(profile, refreshed, cancellationToken);` — the refresh already holds the lock, and `SaveAsync` now takes it.

- [ ] **Step 4: Run to verify they pass**

Run the `TokenStoreProfileTests` filter, then the whole Core suite: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj`. Expected: green. (`CrossProcessRefreshTests` and `RefreshIfExpiringTests` exercise the refactored lock path.)

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.Cli.Core/Auth/GuardedWriteOutcome.cs src/Capacitor.Cli.Core/Auth/TokenStore.cs test/Capacitor.Cli.Core.Tests.Unit/Auth/TokenStoreProfileTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Take the profile lock for every token save (#1093)" -m "A save for another profile no longer deletes the active profile's legacy tokens.json." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Reload under the lock in the refresh path

**Files:**
- Modify: `src/Capacitor.Cli.Core/Auth/TokenStore.cs` (`RefreshWithCrossProcessLockAsync`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Auth/CrossProcessRefreshTests.cs`

**Interfaces:**
- Consumes: `TryAcquireProfileLockAsync`, `SaveLockedAsync`, `IsLegacyOwner` from Task 2.

- [ ] **Step 1: Write the failing tests**

Append to `CrossProcessRefreshTests`:

```csharp
    [Test]
    public async Task A_token_deleted_while_waiting_for_the_lock_is_not_resurrected() {
        Directory.CreateDirectory(TokensDir);
        var current = Token("old", DateTimeOffset.UtcNow.AddMinutes(-10));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("alpha", current);
        var refreshCalled = false;

        Task<StoredTokens?> refresh;
        using (new FileStream(Path.Combine(TokensDir, "alpha.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            refresh = AuthFixtures.NewTokenStore(Config.Root).RefreshWithCrossProcessLockAsync(
                "alpha", current, _ => { refreshCalled = true; return Task.FromResult<StoredTokens?>(Token("new", DateTimeOffset.UtcNow.AddHours(1))); });
            await Task.Delay(200);
            File.Delete(Path.Combine(TokensDir, "alpha.json"));
        }

        await Assert.That(await refresh).IsNull();
        await Assert.That(refreshCalled).IsFalse();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "alpha.json"))).IsFalse();
    }

    [Test]
    public async Task A_legacy_only_token_is_refreshed_and_migrated_under_the_lock() {
        var legacyPath = Config.PathTo("tokens.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        var current = Token("legacy", DateTimeOffset.UtcNow.AddMinutes(-10));
        await File.WriteAllTextAsync(legacyPath,
            System.Text.Json.JsonSerializer.Serialize(current, CapacitorJsonContext.Default.StoredTokens));

        var result = await AuthFixtures.NewTokenStore(Config.Root).RefreshWithCrossProcessLockAsync(
            "default", current, _ => Task.FromResult<StoredTokens?>(Token("fresh", DateTimeOffset.UtcNow.AddHours(1))));

        await Assert.That(result!.AccessToken).IsEqualTo("fresh");
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("default"))!.AccessToken).IsEqualTo("fresh");
        await Assert.That(File.Exists(legacyPath)).IsFalse();
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/CrossProcessRefreshTests/*"`
Expected: the first test fails (today the pre-lock copy is refreshed and saved; `refreshCalled` is true). The second passes already and stays as a regression pin.

- [ ] **Step 3: Implement**

In `RefreshWithCrossProcessLockAsync`, inside the `try`, replace `var latest = await LoadAsync(profile) ?? current;` with:

```csharp
            // Re-read under the lock. A file deleted since the pre-lock read is a sign-out, not a
            // credential to bring back from the copy in hand; a file the pre-lock read parsed but
            // is corrupt now is refreshed from that copy, as a corrupt file always was.
            var (state, onDisk) = await ReadTokenFileAsync(ProfileTokenPath(profile));
            StoredTokens latest;
            if (state == TokenFileState.Loaded) {
                latest = onDisk!;
            } else if (state == TokenFileState.Missing) {
                var legacy = IsLegacyOwner(profile) ? (await ReadTokenFileAsync(LegacyTokenPath)).Tokens : null;
                if (legacy is null) return null;
                latest = legacy;
            } else {
                latest = current;
            }
```

- [ ] **Step 4: Run to verify they pass**

Same filter, then the whole Core suite. Expected: green.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.Cli.Core/Auth/TokenStore.cs test/Capacitor.Cli.Core.Tests.Unit/Auth/CrossProcessRefreshTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Treat a token file absent under the lock as a sign-out (#1093)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Guarded delete and locked logout

**Files:**
- Modify: `src/Capacitor.Cli.Core/Auth/TokenStore.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Auth/TokenStoreProfileTests.cs`

**Interfaces:**
- Produces: `public Task<GuardedWriteOutcome> TokenStore.DeleteGuardedAsync(string profile, Func<ProfileConfig, bool>? guard, CancellationToken ct = default)`; `public Task TokenStore.DeleteAsync(CancellationToken ct = default)` (logout, all profiles). Removes `public void Delete(string profile)`.

- [ ] **Step 1: Update the two existing callers of `Delete("acme")` in `TokenStoreProfileTests` (lines ~80 and ~211)**

Change `AuthFixtures.NewTokenStore(Config.Root).Delete("acme");` to `await AuthFixtures.NewTokenStore(Config.Root).DeleteGuardedAsync("acme", guard: null);`.

- [ ] **Step 2: Write the failing tests**

Append to `TokenStoreProfileTests`:

```csharp
    [Test]
    public async Task DeleteGuardedAsync_deletes_only_when_the_guard_passes() {
        var store = AuthFixtures.NewTokenStore(Config.Root);
        await store.SaveAsync("acme", MakeTokens("alice"));
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles = new Dictionary<string, Profile> { ["acme"] = new() { ServerUrl = "https://acme.example" } }
        });

        var refused = await store.DeleteGuardedAsync("acme", cfg => !cfg.Profiles.ContainsKey("acme"));
        await Assert.That(refused).IsEqualTo(GuardedWriteOutcome.GuardRefused);
        await Assert.That(File.Exists(Path.Combine(TokensDir, "acme.json"))).IsTrue();

        await ConfigMutator.MutateAsync(Config.Root, c => c with { Profiles = new Dictionary<string, Profile>() });
        var deleted = await store.DeleteGuardedAsync("acme", cfg => !cfg.Profiles.ContainsKey("acme"));
        await Assert.That(deleted).IsEqualTo(GuardedWriteOutcome.Written);
        await Assert.That(File.Exists(Path.Combine(TokensDir, "acme.json"))).IsFalse();
    }

    [Test]
    public async Task Logout_waits_for_a_refresh_holding_the_lock_and_then_deletes_its_result() {
        var store = AuthFixtures.NewTokenStore(Config.Root);
        await store.SaveAsync("alpha", MakeTokens("alice"));
        var alphaPath = Path.Combine(TokensDir, "alpha.json");

        Task logout;
        using (new FileStream(Path.Combine(TokensDir, "alpha.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            logout = store.DeleteAsync();
            await Task.Delay(200);
            await Assert.That(File.Exists(alphaPath)).IsTrue();
            // The refresh that holds the lock persists its result before releasing.
            await File.WriteAllTextAsync(alphaPath,
                System.Text.Json.JsonSerializer.Serialize(MakeTokens("refreshed"), CapacitorJsonContext.Default.StoredTokens));
        }
        await logout;

        await Assert.That(File.Exists(alphaPath)).IsFalse();
    }

    [Test]
    public async Task Logout_deletes_a_legacy_only_credential_under_the_active_profiles_lock() {
        Directory.CreateDirectory(TokensDir);
        await File.WriteAllTextAsync(LegacyPath,
            System.Text.Json.JsonSerializer.Serialize(MakeTokens("legacy"), CapacitorJsonContext.Default.StoredTokens));

        Task logout;
        using (new FileStream(Path.Combine(TokensDir, "default.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            logout = AuthFixtures.NewTokenStore(Config.Root).DeleteAsync();
            await Task.Delay(200);
            await Assert.That(File.Exists(LegacyPath)).IsTrue();
        }
        await logout;

        await Assert.That(File.Exists(LegacyPath)).IsFalse();
    }
```

- [ ] **Step 3: Run to verify they fail**

Run the `TokenStoreProfileTests` filter. Expected: compile error on `DeleteGuardedAsync`.

- [ ] **Step 4: Implement**

In `TokenStore.cs`, replace `public void Delete(string profile) { ... }` and `public Task DeleteAsync() { ... }` with:

```csharp
    /// Deletes the profile's credential under its lock when <paramref name="guard"/> holds against
    /// the config as it is at that moment; a null guard always deletes. A lock that cannot be taken
    /// or a delete that fails throws, so a caller can report the file it could not remove.
    public async Task<GuardedWriteOutcome> DeleteGuardedAsync(
            string profile, Func<ProfileConfig, bool>? guard, CancellationToken ct = default) {
        using var lockStream = await AcquireProfileLockAsync(profile, ct);
        var outcome = Evaluate(guard);
        if (outcome == GuardedWriteOutcome.Written) DeleteLocked(profile);
        return outcome;
    }

    void DeleteLocked(string profile) {
        var path = ProfileTokenPath(profile);
        if (File.Exists(path)) File.Delete(path);
        SweepLeakedTemps(profile);
    }

    /// Logout. Each credential goes under its own lock, so a refresh holding one finishes and its
    /// result is deleted rather than recreated after the fact; the legacy file goes under the active
    /// profile's lock, the one a legacy-only refresh holds.
    public async Task DeleteAsync(CancellationToken ct = default) {
        ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var cfg);
        var names = new HashSet<string>(cfg.Profiles.Keys, StringComparer.Ordinal);
        if (Directory.Exists(TokenDir)) {
            foreach (var file in Directory.EnumerateFiles(TokenDir, "*.json"))
                names.Add(Path.GetFileNameWithoutExtension(file));
        }

        foreach (var name in names) {
            try { await DeleteGuardedAsync(name, guard: null, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort, per file */ }
        }

        try {
            using var lockStream = await AcquireProfileLockAsync(cfg.ActiveName, ct);
            if (File.Exists(LegacyTokenPath)) File.Delete(LegacyTokenPath);
        } catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        SweepLeakedTemps();
    }
```

Then search the solution for other callers of the removed `Delete(string)` on `TokenStore`: `rtk proxy grep -rn -E 'TokenStore>\(\)\.Delete\(|tokens\.Delete\(|store\.Delete\("' src test --include='*.cs'` — the daemon's `store.Delete(...)` hits are `AgentPidRecordStore`, not `TokenStore`; only the two test sites from Step 1 need changing.

- [ ] **Step 5: Run to verify they pass**

`TokenStoreProfileTests` filter, then the whole Core suite. Expected: green.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.Cli.Core/Auth/TokenStore.cs test/Capacitor.Cli.Core.Tests.Unit/Auth/TokenStoreProfileTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Delete token files under their profile lock, logout included (#1093)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: `TokenStore.MigrateLegacyAsync`

**Files:**
- Modify: `src/Capacitor.Cli.Core/Auth/TokenStore.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Auth/TokenStoreProfileTests.cs`

**Interfaces:**
- Produces: `public Task TokenStore.MigrateLegacyAsync(string owner, CancellationToken ct = default)` — moves `tokens.json` to `tokens/<owner>.json` when that is absent, deletes it when the per-profile file exists, no-op without a legacy file; throws on a failed move or delete.

- [ ] **Step 1: Write the failing tests**

Append to `TokenStoreProfileTests`:

```csharp
    static string Json(StoredTokens tokens) =>
        System.Text.Json.JsonSerializer.Serialize(tokens, CapacitorJsonContext.Default.StoredTokens);

    [Test]
    public async Task MigrateLegacyAsync_moves_the_file_into_the_owners_slot_when_it_is_empty() {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        await File.WriteAllTextAsync(LegacyPath, Json(MakeTokens("legacy")));

        await AuthFixtures.NewTokenStore(Config.Root).MigrateLegacyAsync("a");

        await Assert.That(File.Exists(LegacyPath)).IsFalse();
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("a"))!.GitHubUsername).IsEqualTo("legacy");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MigrateLegacyAsync_deletes_the_file_when_the_owner_already_has_one(bool ownerFileValid) {
        Directory.CreateDirectory(TokensDir);
        await File.WriteAllTextAsync(Path.Combine(TokensDir, "a.json"), ownerFileValid ? Json(MakeTokens("own")) : "{ corrupt");
        await File.WriteAllTextAsync(LegacyPath, Json(MakeTokens("legacy")));

        await AuthFixtures.NewTokenStore(Config.Root).MigrateLegacyAsync("a");

        await Assert.That(File.Exists(LegacyPath)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(TokensDir, "a.json")))
            .IsEqualTo(ownerFileValid ? Json(MakeTokens("own")) : "{ corrupt");
    }

    [Test]
    public async Task MigrateLegacyAsync_is_a_no_op_without_a_legacy_file() {
        await AuthFixtures.NewTokenStore(Config.Root).MigrateLegacyAsync("a");

        await Assert.That(File.Exists(Path.Combine(TokensDir, "a.json"))).IsFalse();
    }

    [Test]
    public async Task MigrateLegacyAsync_throws_when_the_token_directory_cannot_be_created() {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        await File.WriteAllTextAsync(LegacyPath, Json(MakeTokens("legacy")));
        await File.WriteAllTextAsync(TokensDir, "a file where the directory should be");

        await Assert.That(() => AuthFixtures.NewTokenStore(Config.Root).MigrateLegacyAsync("a")).Throws<IOException>();
        await Assert.That(File.Exists(LegacyPath)).IsTrue();
    }

    [Test]
    public async Task After_migration_a_newly_selected_profile_reads_no_credential() {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        await File.WriteAllTextAsync(LegacyPath, Json(MakeTokens("legacy-a")));
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = "a",
            Profiles = new Dictionary<string, Profile> { ["a"] = new() { ServerUrl = "https://a.example" }, ["b"] = new() { ServerUrl = "https://b.example" } }
        });

        await AuthFixtures.NewTokenStore(Config.Root).MigrateLegacyAsync("a");
        await ConfigMutator.MutateAsync(Config.Root, c => c with { ActiveProfile = "b" });

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).LoadForProfileAsync("b")).IsNull();
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadForProfileAsync("a"))!.GitHubUsername).IsEqualTo("legacy-a");
    }
```

`Directory.CreateDirectory` throws `IOException` when a file sits at the path on every platform .NET 10 supports; that is what the throwing test relies on.

- [ ] **Step 2: Run to verify they fail**

`TokenStoreProfileTests` filter. Expected: compile error on `MigrateLegacyAsync`.

- [ ] **Step 3: Implement**

Add to `TokenStore.cs`, after `DeleteAsync`:

```csharp
    /// Settles the legacy <c>tokens.json</c> under <paramref name="owner"/>'s lock: moved into the
    /// owner's own file when that is absent, deleted when the owner already has one (valid or
    /// corrupt, the legacy copy is superseded either way), untouched when there is none. Throws on
    /// a failed move or delete, so a caller about to change the active profile can refuse instead
    /// of leaving the file for the next profile to claim.
    public async Task MigrateLegacyAsync(string owner, CancellationToken ct = default) {
        if (!File.Exists(LegacyTokenPath)) return;
        using var lockStream = await AcquireProfileLockAsync(owner, ct);
        if (!File.Exists(LegacyTokenPath)) return;

        var target = ProfileTokenPath(owner);
        if (File.Exists(target)) {
            File.Delete(LegacyTokenPath);
            return;
        }

        File.Move(LegacyTokenPath, target);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
```

- [ ] **Step 4: Run to verify they pass**

`TokenStoreProfileTests` filter, then the Core suite. Expected: green.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.Cli.Core/Auth/TokenStore.cs test/Capacitor.Cli.Core.Tests.Unit/Auth/TokenStoreProfileTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Settle the legacy credential under its owner's lock (#1093)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: `ProfileRemoval` in Core

**Files:**
- Create: `src/Capacitor.Cli.Core/Config/ProfileRemovalOutcome.cs`, `src/Capacitor.Cli.Core/Config/ProfileRemovalResult.cs`, `src/Capacitor.Cli.Core/Config/ProfileRemoval.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Config/ProfileRemovalTests.cs` (create)

**Interfaces:**
- Consumes: `ConfigMutator.MutateStrictAsync` (Task 1), `TokenStore.DeleteGuardedAsync`, `TokenStore.TokenPath` (Tasks 2, 4).
- Produces: `public enum ProfileRemovalOutcome { Removed, RemovedTokenRetained, NotFound, IsDefault, IsActive, ConfigUnreadable }`; `public sealed record ProfileRemovalResult(ProfileRemovalOutcome Outcome, string? Detail = null)`; `public static Task<ProfileRemovalResult> ProfileRemoval.RemoveAsync(ConfigRoot config, TokenStore tokens, string name, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Core.Tests.Unit/Config/ProfileRemovalTests.cs`:

```csharp
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

public class ProfileRemovalTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string TokensDir  => Config.PathTo("tokens");
    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

    static StoredTokens Tokens(string user) => new() {
        AccessToken = "at", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GitHubUsername = user, Provider = AuthProvider.GitHubApp
    };

    async Task Seed(string active = "keep") =>
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = active,
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new(),
                ["keep"]    = new() { ServerUrl = "https://keep.example" },
                ["gone"]    = new() { ServerUrl = "https://gone.example" }
            },
            ProfileBindings = new Dictionary<string, string> { ["/repo/a"] = "gone", ["/repo/b"] = "keep" }
        });

    [Test]
    public async Task Removes_the_profile_its_bindings_and_its_token() {
        await Seed();
        var store = AuthFixtures.NewTokenStore(Config.Root);
        await store.SaveAsync("gone", Tokens("g"));
        await store.SaveAsync("keep", Tokens("k"));

        var result = await ProfileRemoval.RemoveAsync(Config.Root, store, "gone");

        await Assert.That(result.Outcome).IsEqualTo(ProfileRemovalOutcome.Removed);
        var config = ConfigMutator.LoadPure(ConfigPath);
        await Assert.That(config.Profiles.ContainsKey("gone")).IsFalse();
        await Assert.That(config.Profiles.ContainsKey("keep")).IsTrue();
        await Assert.That(config.ProfileBindings.ContainsKey("/repo/a")).IsFalse();
        await Assert.That(config.ProfileBindings["/repo/b"]).IsEqualTo("keep");
        await Assert.That(File.Exists(Path.Combine(TokensDir, "gone.json"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "keep.json"))).IsTrue();
    }

    [Test]
    public async Task Refuses_default_a_missing_profile_and_the_active_profile() {
        await Seed(active: "gone");
        var store = AuthFixtures.NewTokenStore(Config.Root);
        await store.SaveAsync("gone", Tokens("g"));
        var before = await File.ReadAllTextAsync(ConfigPath);

        await Assert.That((await ProfileRemoval.RemoveAsync(Config.Root, store, "default")).Outcome).IsEqualTo(ProfileRemovalOutcome.IsDefault);
        await Assert.That((await ProfileRemoval.RemoveAsync(Config.Root, store, "nope")).Outcome).IsEqualTo(ProfileRemovalOutcome.NotFound);
        await Assert.That((await ProfileRemoval.RemoveAsync(Config.Root, store, "gone")).Outcome).IsEqualTo(ProfileRemovalOutcome.IsActive);

        await Assert.That(await File.ReadAllTextAsync(ConfigPath)).IsEqualTo(before);
        await Assert.That(File.Exists(Path.Combine(TokensDir, "gone.json"))).IsTrue();
    }

    [Test]
    public async Task An_unreadable_config_removes_nothing() {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, "{ not json");
        var store = AuthFixtures.NewTokenStore(Config.Root);
        await store.SaveAsync("gone", Tokens("g"));

        var result = await ProfileRemoval.RemoveAsync(Config.Root, store, "gone");

        await Assert.That(result.Outcome).IsEqualTo(ProfileRemovalOutcome.ConfigUnreadable);
        await Assert.That(await File.ReadAllTextAsync(ConfigPath)).IsEqualTo("{ not json");
        await Assert.That(File.Exists(Path.Combine(TokensDir, "gone.json"))).IsTrue();
    }

    [Test]
    public async Task A_case_alias_keeps_the_shared_token_file() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new(),
                ["acme"]    = new() { ServerUrl = "https://acme.example" },
                ["Acme"]    = new() { ServerUrl = "https://acme.example" }
            }
        });
        var store = AuthFixtures.NewTokenStore(Config.Root);
        await store.SaveAsync("acme", Tokens("a"));

        var result = await ProfileRemoval.RemoveAsync(Config.Root, store, "acme");

        await Assert.That(result.Outcome).IsEqualTo(ProfileRemovalOutcome.Removed);
        await Assert.That(ConfigMutator.LoadPure(ConfigPath).Profiles.ContainsKey("acme")).IsFalse();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "acme.json"))).IsTrue();
    }

    [Test]
    public async Task A_token_that_cannot_be_deleted_is_reported_with_its_path() {
        await Seed();
        await File.WriteAllTextAsync(TokensDir, "a file where the directory should be");
        var store = AuthFixtures.NewTokenStore(Config.Root);

        var result = await ProfileRemoval.RemoveAsync(Config.Root, store, "gone");

        await Assert.That(result.Outcome).IsEqualTo(ProfileRemovalOutcome.RemovedTokenRetained);
        await Assert.That(result.Detail!).Contains(Path.Combine(TokensDir, "gone.json"));
        await Assert.That(ConfigMutator.LoadPure(ConfigPath).Profiles.ContainsKey("gone")).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ProfileRemovalTests/*"`
Expected: compile errors — the three types do not exist.

- [ ] **Step 3: Implement**

`src/Capacitor.Cli.Core/Config/ProfileRemovalOutcome.cs`:

```csharp
namespace Capacitor.Cli.Core.Config;

public enum ProfileRemovalOutcome { Removed, RemovedTokenRetained, NotFound, IsDefault, IsActive, ConfigUnreadable }
```

`src/Capacitor.Cli.Core/Config/ProfileRemovalResult.cs`:

```csharp
namespace Capacitor.Cli.Core.Config;

/// <param name="Detail">For <see cref="ProfileRemovalOutcome.RemovedTokenRetained"/>: the token file
/// that stayed and why.</param>
public sealed record ProfileRemovalResult(ProfileRemovalOutcome Outcome, string? Detail = null);
```

`src/Capacitor.Cli.Core/Config/ProfileRemoval.cs`:

```csharp
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Config;

/// Removes a profile, its bindings and its credential. The one removal both
/// <c>kcap profile remove</c> and the desktop app run.
public static class ProfileRemoval {
    public static async Task<ProfileRemovalResult> RemoveAsync(
            ConfigRoot config, TokenStore tokens, string name, CancellationToken ct = default) {
        if (name == ProfileConfig.DefaultName) return new(ProfileRemovalOutcome.IsDefault);

        // Decided on the locked config: a concurrent `kcap use --global` cannot make the profile
        // active between the check and the write.
        try {
            await ConfigMutator.MutateStrictAsync(config, current => {
                if (!current.Profiles.ContainsKey(name)) throw new Refusal(ProfileRemovalOutcome.NotFound);
                if (current.ActiveName == name) throw new Refusal(ProfileRemovalOutcome.IsActive);

                var profiles = new Dictionary<string, Profile>(current.Profiles);
                profiles.Remove(name);
                var bindings = current.ProfileBindings
                    .Where(kv => kv.Value != name)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                return current with { Profiles = profiles, ProfileBindings = bindings };
            }, ct);
        } catch (Refusal refusal) {
            return new(refusal.Outcome);
        } catch (ConfigUnreadableException) {
            return new(ProfileRemovalOutcome.ConfigUnreadable);
        }

        // Deleted only when no remaining profile can read the file: a same-name recreation, or a
        // case-alias on a case-insensitive filesystem, owns it now.
        try {
            var outcome = await tokens.DeleteGuardedAsync(name,
                cfg => !cfg.Profiles.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)), ct);

            return outcome == GuardedWriteOutcome.ConfigUnreadable
                ? new(ProfileRemovalOutcome.RemovedTokenRetained, "config unreadable")
                : new(ProfileRemovalOutcome.Removed);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) {
            return new(ProfileRemovalOutcome.RemovedTokenRetained, $"{tokens.TokenPath(name)}: {ex.Message}");
        }
    }

    sealed class Refusal(ProfileRemovalOutcome outcome) : Exception {
        public ProfileRemovalOutcome Outcome { get; } = outcome;
    }
}
```

- [ ] **Step 4: Run to verify they pass**

`ProfileRemovalTests` filter. Expected: all five pass.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.Cli.Core/Config/ProfileRemovalOutcome.cs src/Capacitor.Cli.Core/Config/ProfileRemovalResult.cs src/Capacitor.Cli.Core/Config/ProfileRemoval.cs test/Capacitor.Cli.Core.Tests.Unit/Config/ProfileRemovalTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Add the shared profile removal that deletes the credential (#1093)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: `kcap profile remove` through `ProfileRemoval`, help and README

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ProfileCommand.cs`, `src/Capacitor.Cli.Core/Resources/help-profile.txt`, `README.md`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/ProfileCommandTests.cs`

**Interfaces:**
- Consumes: `ProfileRemoval.RemoveAsync` (Task 6).
- Produces: `public sealed class ProfileCommand(ConfigRoot config, ICapacitorHttpClient http, TokenStore tokens)` — DI constructs it, so no registration change.

- [ ] **Step 1: Update the constructor calls and add tests**

In `ProfileCommandTests`, replace every `new ProfileCommand(Config.Root, new FixedCapacitorHttpClient())` with `new ProfileCommand(Config.Root, new FixedCapacitorHttpClient(), AuthFixtures.NewTokenStore(Config.Root))` and add `using Capacitor.Cli.Core.Auth;`. Then append:

```csharp
    [Test]
    public async Task RemoveProfile_DeletesTheToken() {
        var configPath = AppConfig.GetConfigPath(Config.Root);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new ProfileConfig {
            Profiles = new() { ["default"] = new() { ServerUrl = "https://default.com" }, ["contoso"] = new() { ServerUrl = "https://contoso.com" } }
        }, ProfileConfigJsonContextIndented.Default.ProfileConfig));
        var tokens = AuthFixtures.NewTokenStore(Config.Root);
        await tokens.SaveAsync("contoso", new StoredTokens { AccessToken = "at", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Provider = AuthProvider.GitHubApp });

        var result = await new ProfileCommand(Config.Root, new FixedCapacitorHttpClient(), tokens).RemoveProfile("contoso");

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(File.Exists(Config.PathTo("tokens", "contoso.json"))).IsFalse();
    }

    [Test]
    public async Task RemoveProfile_RefusesTheActiveProfile() {
        var configPath = AppConfig.GetConfigPath(Config.Root);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new ProfileConfig {
            ActiveProfile = "contoso",
            Profiles = new() { ["default"] = new() { ServerUrl = "https://default.com" }, ["contoso"] = new() { ServerUrl = "https://contoso.com" } }
        }, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await new ProfileCommand(Config.Root, new FixedCapacitorHttpClient(), AuthFixtures.NewTokenStore(Config.Root)).RemoveProfile("contoso");

        await Assert.That(result).IsEqualTo(1);
        var config = JsonSerializer.Deserialize(await File.ReadAllTextAsync(configPath), ProfileConfigJsonContext.Default.ProfileConfig)!;
        await Assert.That(config.Profiles).ContainsKey("contoso");
        await Assert.That(config.ActiveProfile).IsEqualTo("contoso");
    }

    [Test]
    public async Task RemoveProfile_UnknownProfileFails() {
        var result = await new ProfileCommand(Config.Root, new FixedCapacitorHttpClient(), AuthFixtures.NewTokenStore(Config.Root)).RemoveProfile("nope");

        await Assert.That(result).IsEqualTo(1);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ProfileCommandTests/*"`
Expected: compile error — the constructor has two parameters.

- [ ] **Step 3: Implement**

In `ProfileCommand.cs`: change the declaration to `public sealed class ProfileCommand(ConfigRoot config, ICapacitorHttpClient http, TokenStore tokens)`, add `using Capacitor.Cli.Core.Auth;`, and replace `RemoveProfile`:

```csharp
    internal async Task<int> RemoveProfile(string name) {
        var result = await ProfileRemoval.RemoveAsync(config, tokens, name);

        switch (result.Outcome) {
            case ProfileRemovalOutcome.Removed:
                await Console.Out.WriteLineAsync($"Profile '{name}' removed.");
                return 0;
            case ProfileRemovalOutcome.RemovedTokenRetained:
                await Console.Out.WriteLineAsync($"Profile '{name}' removed.");
                await Console.Error.WriteLineAsync($"Its saved sign-in could not be deleted ({result.Detail}); remove the file by hand.");
                return 0;
            case ProfileRemovalOutcome.IsDefault:
                await Console.Error.WriteLineAsync("Cannot remove the default profile.");
                return 1;
            case ProfileRemovalOutcome.NotFound:
                await Console.Error.WriteLineAsync($"Profile '{name}' not found.");
                return 1;
            case ProfileRemovalOutcome.IsActive:
                await Console.Error.WriteLineAsync($"Profile '{name}' is the active profile. Select another first: kcap use <other> --global");
                return 1;
            default:
                await Console.Error.WriteLineAsync("The configuration file could not be read; nothing was removed.");
                return 1;
        }
    }
```

`help-profile.txt`: change the `remove` line to

```
  remove <name>             Remove a profile and its saved sign-in (not the active or default profile)
```

`README.md`, after the code block in `### Profiles` (the block ending `kcap profile remove work`), add a paragraph:

```markdown
`kcap profile remove` also deletes the profile's saved sign-in (`~/.config/kcap/tokens/<name>.json`). The active profile cannot be removed — select another with `kcap use <name> --global` first.
```

- [ ] **Step 4: Run to verify they pass**

`ProfileCommandTests` filter, then `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`. Expected: green, no warnings.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.Cli/Commands/ProfileCommand.cs src/Capacitor.Cli.Core/Resources/help-profile.txt README.md test/Capacitor.Cli.Tests.Unit/Commands/ProfileCommandTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Refuse removing the active profile and delete the token (#1093)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: `kcap use` decides inside a strict mutation and settles the legacy credential

**Files:**
- Modify: `src/Capacitor.Cli/Commands/UseCommand.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/UseCommandTests.cs`

**Interfaces:**
- Consumes: `ConfigMutator.MutateStrictAsync` (Task 1), `TokenStore.MigrateLegacyAsync` (Task 5).
- Produces: `public sealed class UseCommand(ConfigRoot config, WorkingDirectory workdir, TokenStore tokens)`.

- [ ] **Step 1: Update constructor calls and add tests**

In `UseCommandTests`, replace every `new UseCommand(Config.Root, workdir: new WorkingDirectory(AppContext.BaseDirectory))` with `new UseCommand(Config.Root, new WorkingDirectory(AppContext.BaseDirectory), AuthFixtures.NewTokenStore(Config.Root))`, add `using Capacitor.Cli.Core.Auth;`, then append:

```csharp
    UseCommand Command() => new(Config.Root, new WorkingDirectory(AppContext.BaseDirectory), AuthFixtures.NewTokenStore(Config.Root));

    async Task SeedTwo(string active) =>
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = active,
            Profiles = new Dictionary<string, Profile> { ["a"] = new() { ServerUrl = "https://a.example" }, ["b"] = new() { ServerUrl = "https://b.example" } }
        });

    [Test]
    public async Task Use_UnknownProfile_RefusesAndChangesNothing() {
        await SeedTwo("a");
        var before = await File.ReadAllTextAsync(AppConfig.GetConfigPath(Config.Root));

        var result = await Command().SetProfile("zzz", repoPath: null, global: true, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(AppConfig.GetConfigPath(Config.Root))).IsEqualTo(before);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Use_Global_MovesTheLegacyCredentialToTheOutgoingProfile(bool withGlobalFlag) {
        await SeedTwo("a");
        var legacy = Config.PathTo("tokens.json");
        await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(
            new StoredTokens { AccessToken = "at", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GitHubUsername = "legacy-a", Provider = AuthProvider.GitHubApp },
            CapacitorJsonContext.Default.StoredTokens));

        // Without --global, a null repo path is the global arm too.
        var result = await Command().SetProfile("b", repoPath: null, global: withGlobalFlag, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(File.Exists(legacy)).IsFalse();
        await Assert.That(File.Exists(Config.PathTo("tokens", "a.json"))).IsTrue();
        await Assert.That(ConfigMutator.LoadPure(AppConfig.GetConfigPath(Config.Root)).ActiveProfile).IsEqualTo("b");

        await Command().SetProfile("a", repoPath: null, global: true, save: false, savePath: null);
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadForProfileAsync("a"))!.GitHubUsername).IsEqualTo("legacy-a");
    }

    [Test]
    public async Task Use_Global_RefusesWhenTheMigrationFails() {
        await SeedTwo("a");
        await File.WriteAllTextAsync(Config.PathTo("tokens.json"), "{}");
        await File.WriteAllTextAsync(Config.PathTo("tokens"), "a file where the directory should be");

        var result = await Command().SetProfile("b", repoPath: null, global: true, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(ConfigMutator.LoadPure(AppConfig.GetConfigPath(Config.Root)).ActiveProfile).IsEqualTo("a");
    }

    [Test]
    public async Task Use_RefusesAnUnreadableConfig() {
        var path = AppConfig.GetConfigPath(Config.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ not json");

        var result = await Command().SetProfile("b", repoPath: "/repos/x", global: false, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("{ not json");
    }
```

`Config.PathTo("tokens.json")`'s parent directory exists once `SeedTwo` has written config.json; the migration-failure test writes the `tokens` file after that.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/UseCommandTests/*"`
Expected: compile error — the constructor has two parameters.

- [ ] **Step 3: Implement**

Replace the body of `UseCommand.cs` from the class declaration down (keep the usings; add `using Capacitor.Cli.Core.Auth;`):

```csharp
public sealed class UseCommand(ConfigRoot config, WorkingDirectory workdir, TokenStore tokens) {
    public async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await Console.Error.WriteLineAsync("Usage: kcap use <profile-name> [--global] [--save]");
            return 1;
        }

        var name = args[1];
        var global = args.Contains("--global");
        var save = args.Contains("--save");
        // Resolved at most once, and not at all for a global selection that saves nothing:
        // RepoRootOf shells out to git, which a change needing no repository must not wait on.
        var repoRoot = !global || save ? AppConfig.RepoRootOf(workdir) : null;
        var repoPath = global ? null : repoRoot;

        return await SetProfile(name, repoPath, global, save, save ? repoRoot : null);
    }

    internal async Task<int> SetProfile(
        string name, string? repoPath, bool global, bool save, string? savePath
    ) {
        var selectsGlobally = global || repoPath is null;

        // The outgoing active profile's legacy credential is settled before the name it follows moves.
        if (selectsGlobally) {
            if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var before)) {
                await Console.Error.WriteLineAsync("The configuration file could not be read; nothing was changed.");
                return 1;
            }
            try {
                await tokens.MigrateLegacyAsync(before.ActiveName);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) {
                await Console.Error.WriteLineAsync(
                    $"Could not move the saved sign-in of profile '{before.ActiveName}' ({ex.Message}); nothing was changed.");
                return 1;
            }
        }

        Profile? profile = null;
        try {
            await ConfigMutator.MutateStrictAsync(config, c => {
                if (!c.Profiles.TryGetValue(name, out profile)) throw new UnknownProfile();
                return selectsGlobally
                    ? c with { ActiveProfile = name }
                    : c with { ProfileBindings = new Dictionary<string, string>(c.ProfileBindings) { [repoPath!] = name } };
            });
        } catch (UnknownProfile) {
            await Console.Error.WriteLineAsync($"Profile '{name}' not found. Run `kcap profile list` to see available profiles.");
            return 1;
        } catch (ConfigUnreadableException) {
            await Console.Error.WriteLineAsync("The configuration file could not be read; nothing was changed.");
            return 1;
        }

        await Console.Out.WriteLineAsync(selectsGlobally
            ? $"Active profile set to '{name}' (global)."
            : $"Profile '{name}' bound to {repoPath}.");

        if (save && savePath is not null) {
            var repoConfig = new RepoConfig {
                Profile = name,
                ServerUrl = profile!.ServerUrl
            };
            var repoConfigPath = Path.Combine(savePath, ".kcap.json");
            await File.WriteAllBytesAsync(repoConfigPath,
                JsonSerializer.SerializeToUtf8Bytes(repoConfig, RepoConfigJsonContextIndented.Default.RepoConfig));
            await Console.Out.WriteLineAsync($"Wrote {repoConfigPath} — commit this to share with your team.");
        }

        return 0;
    }

    sealed class UnknownProfile : Exception;
}
```

Delete the old `LoadConfig()` method.

- [ ] **Step 4: Run to verify they pass**

`UseCommandTests` filter, then `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`. Expected: green.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.Cli/Commands/UseCommand.cs test/Capacitor.Cli.Tests.Unit/Commands/UseCommandTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Decide kcap use on the locked config and settle the legacy token (#1093)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Commit guards — `ExpectServer`, strict commit, `CredentialSaved`, guarded saves

**Files:**
- Create: `src/Capacitor.Cli.Core/Auth/CommitPrecondition.cs`, `src/Capacitor.Cli.Core/Auth/CommitPreconditionFailedException.cs`
- Modify: `src/Capacitor.Cli.Core/Auth/AuthResult.cs`, `src/Capacitor.Cli.Core/Auth/OnboardingFacade.cs`, `src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs`, `src/Capacitor.Cli/Commands/LoginCommand.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Auth/OnboardingFacadeTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Auth/CommitBoundaryTests.cs`

**Interfaces:**
- Consumes: `ConfigMutator.MutateStrictAsync`, `TokenStore.SaveGuardedAsync`, `GuardedWriteOutcome`.
- Produces: `public abstract record CommitPrecondition` with `public sealed record CommitPrecondition.ExpectServer(string Url)`; `AuthResult.Committed` gains `bool CredentialSaved = true`; `OnboardingFacade.LoginAsync(string serverUrl, bool forceDevice, string profile, CancellationToken ct, bool adoptServer = false, CommitPrecondition? precondition = null)`; internal `CommitRequest.Precondition`, `LoginTarget.ExistedAtRead`, `LoginTarget.Precondition`, `LoginTarget.SaveGuard`.

- [ ] **Step 1: Write the failing tests**

Append to `OnboardingFacadeTests`:

```csharp
    // ── commit-time preconditions ────────────────────────────────────────────

    Task Seed(string profile, string url) => ConfigMutator.MutateAsync(Config.Root, c => c with {
        Profiles = new Dictionary<string, Profile> { [profile] = new() { ServerUrl = url } }
    });

    [Test]
    public async Task LoginAsync_with_ExpectServer_commits_and_reports_the_saved_credential_when_nothing_changed() {
        await Seed("acme", "https://acme.kcap.ai");
        using var handler = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");
        var facade = NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        var result = await facade.LoginAsync("https://acme.kcap.ai", forceDevice: true, profile: "acme", CancellationToken.None,
            adoptServer: true, precondition: new CommitPrecondition.ExpectServer("https://acme.kcap.ai"));

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(((AuthResult.Committed)result).CredentialSaved).IsTrue();
        await Assert.That(TokenFileExists("acme")).IsTrue();
    }

    [Test]
    public async Task LoginAsync_with_ExpectServer_refuses_a_profile_repointed_before_commit() {
        await Seed("acme", "https://acme.kcap.ai");
        using var handler  = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");
        var       progress = new RecordingAuthProgress();
        // The hook runs after authentication and before the config write: the window the guard closes.
        var facade = NewFacade(Config.Root, progress, handler,
            beforeCommit: (_, _) => ConfigMutator.MutateAsync(Config.Root, c => c with {
                Profiles = new Dictionary<string, Profile> { ["acme"] = new() { ServerUrl = "https://elsewhere.example" } }
            }));

        var result = await facade.LoginAsync("https://acme.kcap.ai", forceDevice: true, profile: "acme", CancellationToken.None,
            adoptServer: true, precondition: new CommitPrecondition.ExpectServer("https://acme.kcap.ai"));

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(TokenFileExists("acme")).IsFalse();
        await Assert.That(ReadConfig().Profiles["acme"].ServerUrl).IsEqualTo("https://elsewhere.example");
        await Assert.That(ReadConfig().Profiles["acme"].AuthProvider).IsNull();
    }

    [Test]
    public async Task LoginAsync_with_ExpectServer_refuses_a_profile_removed_before_commit() {
        await Seed("acme", "https://acme.kcap.ai");
        using var handler = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");
        var facade = NewFacade(Config.Root, new RecordingAuthProgress(), handler,
            beforeCommit: (_, _) => ConfigMutator.MutateAsync(Config.Root, c => c with { Profiles = new Dictionary<string, Profile>() }));

        var result = await facade.LoginAsync("https://acme.kcap.ai", forceDevice: true, profile: "acme", CancellationToken.None,
            adoptServer: true, precondition: new CommitPrecondition.ExpectServer("https://acme.kcap.ai"));

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(TokenFileExists("acme")).IsFalse();
        await Assert.That(ReadConfig().Profiles.ContainsKey("acme")).IsFalse();
    }

    [Test]
    public async Task LoginAsync_with_ExpectServer_refuses_a_config_corrupted_before_commit() {
        await Seed("acme", "https://acme.kcap.ai");
        using var handler = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");
        var facade = NewFacade(Config.Root, new RecordingAuthProgress(), handler,
            beforeCommit: (_, _) => File.WriteAllTextAsync(ConfigPath, "{ not json"));

        var result = await facade.LoginAsync("https://acme.kcap.ai", forceDevice: true, profile: "acme", CancellationToken.None,
            adoptServer: true, precondition: new CommitPrecondition.ExpectServer("https://acme.kcap.ai"));

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(await File.ReadAllTextAsync(ConfigPath)).IsEqualTo("{ not json");
        await Assert.That(TokenFileExists("acme")).IsFalse();
    }

    [Test]
    public async Task LoginAsync_foreign_for_a_never_existing_profile_still_saves_the_credential() {
        using var handler = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");
        var facade = NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        var result = await facade.LoginAsync("https://acme.kcap.ai", forceDevice: true, profile: "ghost", CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(((AuthResult.Committed)result).CredentialSaved).IsTrue();
        await Assert.That(TokenFileExists("ghost")).IsTrue();
        await Assert.That(ReadConfig().Profiles.ContainsKey("ghost")).IsFalse();
    }
```

Also add to the existing `LoginAsync_without_adopt_keeps_todays_behaviour_on_a_foreign_profile` one line after the type assertion: `await Assert.That(((AuthResult.Committed)result).CredentialSaved).IsTrue();`.

Append to `CommitBoundaryTests`:

```csharp
    [Test]
    public async Task A_login_paused_before_its_token_save_does_not_revive_a_removed_profile() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles = new Dictionary<string, Profile> { ["acme"] = new() { ServerUrl = "https://acme.kcap.ai" } }
        });
        var store  = NewTokenStore(Config.Root);
        var tokens = new StoredTokens { AccessToken = "at", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Provider = AuthProvider.GitHubApp };

        var request = new CommitRequest(
            [new AuthIdentity("acme", "https://acme.kcap.ai:443")], AuthProvider.GitHubApp, "acme", "https://acme.kcap.ai:443",
            ConfigMutation: null,
            PublishTokens: async saved => {
                // Another process removes the profile between the config commit and this save.
                await ConfigMutator.MutateAsync(Config.Root, c => c with { Profiles = new Dictionary<string, Profile>() });
                var outcome = await store.SaveGuardedAsync("acme", tokens, cfg => cfg.Profiles.ContainsKey("acme"), CancellationToken.None);
                if (outcome == GuardedWriteOutcome.Written) saved();
                return "alice";
            });

        var result = await CommitBoundary.CommitAsync(Config.Root, request, null, new RecordingAuthProgress(), CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(((AuthResult.Committed)result).CredentialSaved).IsFalse();
        await Assert.That(TokenFileExists("acme")).IsFalse();
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/OnboardingFacadeTests/*"`
Expected: compile errors on `CommitPrecondition`, `precondition:`, `CredentialSaved`.

- [ ] **Step 3: Implement**

`src/Capacitor.Cli.Core/Auth/CommitPreconditionFailedException.cs`:

```csharp
namespace Capacitor.Cli.Core.Auth;

public sealed class CommitPreconditionFailedException(string message) : InvalidOperationException(message);
```

`src/Capacitor.Cli.Core/Auth/CommitPrecondition.cs`:

```csharp
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Auth;

/// What must still hold about the target profile when the commit boundary writes config. Checked
/// inside the strict config mutation, so a change made while the browser was open is refused
/// rather than committed over.
public abstract record CommitPrecondition {
    internal abstract void Check(ProfileConfig config, string profile);

    /// The profile exists and still names <paramref name="Url"/>.
    public sealed record ExpectServer(string Url) : CommitPrecondition {
        internal override void Check(ProfileConfig config, string profile) {
            if (!config.Profiles.TryGetValue(profile, out var existing))
                throw new CommitPreconditionFailedException($"profile '{profile}' was removed during sign-in; nothing saved.");
            if (!ServerIdentity.SameServer(existing.ServerUrl, Url))
                throw new CommitPreconditionFailedException($"profile '{profile}' no longer points at {Url}; nothing saved.");
        }
    }
}
```

`AuthResult.cs`: change `Committed` to

```csharp
    /// <param name="CredentialSaved">False when the boundary published config but the token save
    /// was refused or failed, so the profile exists without a usable sign-in.</param>
    public sealed record Committed(
        string                      ActiveProfile,
        string                      CanonicalServer,
        string                      Provider,
        string?                     Username,
        IReadOnlyList<AuthIdentity> Published,
        bool                        CredentialSaved = true) : AuthResult;
```

`OnboardingFacade.cs`:

1. `CommitRequest`: add a final parameter `CommitPrecondition? Precondition = null`.
2. In `CommitBoundary.CommitAsync`, replace from `var configPublished = ...` through the end of the `if (configPublished) { ... }` block with:

```csharp
        // A token-only arm has no config commit to make the boundary durable, so what landed is tracked rather than assumed.
        var configPublished = request.ConfigMutation is not null || request.WriteStamp || request.Precondition is not null;

        if (configPublished) {
            try {
                ProfileConfig Mutation(ProfileConfig config) {
                    request.Precondition?.Check(config, request.ActiveProfile);
                    // Profile write and stamp are ONE mutation: no window where a profile exists unstamped.
                    return Stamp(request.ConfigMutation?.Invoke(config) ?? config, request);
                }

                // A precondition is decided on what the file says, so an unreadable file must abort
                // rather than be read as a fresh default and then published over.
                if (request.Precondition is null) await ConfigMutator.MutateAsync(root, Mutation, CancellationToken.None);
                else await ConfigMutator.MutateStrictAsync(root, Mutation, CancellationToken.None);
            } catch (CommitPreconditionFailedException ex) {
                progress.Error($"Error: {ex.Message}");

                return new AuthResult.Failed(ex.Message);
            } catch (ConfigUnreadableException) {
                progress.Error("Error: sign-in could not be saved: the configuration file could not be read.");

                return new AuthResult.Failed("config unreadable");
            } catch (Exception ex) {
                // The config commit is the boundary's first durable step: if it threw, nothing was published.
                progress.Error($"Error: sign-in could not be saved: {ex.Message}");

                return new AuthResult.Failed(ex.Message);
            }
        }
```

3. Change the final `return new AuthResult.Committed(...)` to

```csharp
        return new AuthResult.Committed(
            request.ActiveProfile, request.CanonicalServer, request.Provider, username, request.Identities,
            CredentialSaved: request.PublishTokens is null || tokenSaved);
```

4. `LoginTarget`:

```csharp
sealed record LoginTarget(
    string Profile, string CanonicalServer, string ServerUrl, bool PointsAtServer, bool AdoptServer,
    bool ExistedAtRead = true, CommitPrecondition? Precondition = null) {
    internal bool Adopting  => !PointsAtServer && AdoptServer;
    internal bool Foreign   => !PointsAtServer && !AdoptServer;
    internal bool WriteStamp => !Foreign;

    internal Func<ProfileConfig, ProfileConfig>? ConfigMutation =>
        Adopting ? config => CommitBoundary.PointProfileAtServer(config, Profile, ServerUrl) : null;

    /// Evaluated under the profile's token lock: the profile must still exist, and with a
    /// precondition still name the server. A foreign login for a profile that never existed has
    /// nothing that could have been removed, so it saves unguarded.
    internal Func<ProfileConfig, bool>? SaveGuard =>
        Foreign && !ExistedAtRead
            ? null
            : config => config.Profiles.TryGetValue(Profile, out var existing)
                     && (Precondition is null || ServerIdentity.SameServer(existing.ServerUrl, ServerUrl));
}
```

5. `LoginAsync` / `LoginCoreAsync`: add `CommitPrecondition? precondition = null` after `adoptServer` on both, pass it through, and build the target as

```csharp
        var target     = new LoginTarget(
            profile, canonical, serverUrl,
            PointsAtServer: ServerIdentity.SameServer(configured?.ServerUrl, serverUrl),
            AdoptServer: adoptServer,
            ExistedAtRead: configured is not null,
            Precondition: precondition);
```

6. `LoginNoneAsync`: add `Precondition: target.Precondition` to its `CommitRequest`.
7. `CommitTokensAsync`:

```csharp
        var request = new CommitRequest(
            [new AuthIdentity(target.Profile, target.CanonicalServer)], provider, target.Profile, target.CanonicalServer,
            ConfigMutation: target.ConfigMutation,
            PublishTokens: async saved => {
                var outcome = await store.SaveGuardedAsync(target.Profile, tokens, target.SaveGuard, CancellationToken.None);
                if (outcome == GuardedWriteOutcome.Written) saved();
                else progress.Error(outcome == GuardedWriteOutcome.ConfigUnreadable
                    ? $"Error: the configuration file could not be read; the sign-in for '{target.Profile}' was not saved."
                    : $"Error: profile '{target.Profile}' was removed or repointed during sign-in; nothing saved.");

                return username;
            },
            WriteStamp: target.WriteStamp,
            Precondition: target.Precondition);

        var result = await CommitBoundary.CommitAsync(root, request, beforeCommit, progress, ct);

        if (result is AuthResult.Committed { CredentialSaved: true }) progress.Notice($"Logged in as {username}");

        return result;
```

8. `ExchangeEveryTenantAsync`: replace `await store.SaveAsync(tenant.ProfileName, exchanged.Value.Tokens, CancellationToken.None); saved();` with

```csharp
                var outcome = await store.SaveGuardedAsync(
                    tenant.ProfileName, exchanged.Value.Tokens, cfg => cfg.Profiles.ContainsKey(tenant.ProfileName), CancellationToken.None);
                if (outcome == GuardedWriteOutcome.Written) saved();
                else WarnExchangeFailed(tenant.ProfileName);
```

`WorkOSDiscovery.PublishAsync`: replace `await store.SaveAsync(picked.ProfileName, tokens, CancellationToken.None); saved();` with

```csharp
                var outcome = await store.SaveGuardedAsync(
                    picked.ProfileName, tokens, cfg => cfg.Profiles.ContainsKey(picked.ProfileName), CancellationToken.None);
                if (outcome == GuardedWriteOutcome.Written) saved();
                else progress.Error($"Error: profile '{picked.ProfileName}' was removed during sign-in; nothing saved.");
```

and change its notice to `if (result is AuthResult.Committed { CredentialSaved: true }) progress.Notice(...)`.

`LoginCommand.cs`: change the known-server return to `return result is AuthResult.Committed { CredentialSaved: true } ? 0 : 1;`.

- [ ] **Step 4: Run to verify they pass**

`OnboardingFacadeTests` and `CommitBoundaryTests` filters, then the whole Core suite and `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`. Expected: green. (`LoginFacadeParityTests` constructs `Committed` with five positional arguments; the default keeps it compiling.)

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.Cli.Core/Auth/CommitPrecondition.cs src/Capacitor.Cli.Core/Auth/CommitPreconditionFailedException.cs src/Capacitor.Cli.Core/Auth/AuthResult.cs src/Capacitor.Cli.Core/Auth/OnboardingFacade.cs src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs src/Capacitor.Cli/Commands/LoginCommand.cs test/Capacitor.Cli.Core.Tests.Unit/Auth/OnboardingFacadeTests.cs test/Capacitor.Cli.Core.Tests.Unit/Auth/CommitBoundaryTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Check the profile at commit and report whether the token landed (#1093)" -m "The pre-authentication read decides LoginTarget; a profile removed or repointed while the browser was open is refused inside the strict mutation, and the token save is guarded under the profile lock." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: Settle the legacy credential before a discovery changes the selection

**Files:**
- Modify: `src/Capacitor.Cli.Core/Auth/OnboardingFacade.cs`, `src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Auth/CommitBoundaryTests.cs`

**Interfaces:**
- Consumes: `TokenStore.MigrateLegacyAsync` (Task 5).
- Produces: `internal static Task<string?> CommitBoundary.SettleLegacyCredentialAsync(ConfigRoot root, TokenStore store, CancellationToken ct)` — null on success, else the error message.

- [ ] **Step 1: Write the failing test**

Append to `CommitBoundaryTests`:

```csharp
    [Test]
    public async Task GitHub_discovery_leaves_the_outgoing_profiles_legacy_credential_in_its_own_file() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = "old",
            Profiles = new Dictionary<string, Profile> { ["old"] = new() { ServerUrl = "https://old.example" } }
        });
        var legacyPath = Config.PathTo("tokens.json");
        await File.WriteAllTextAsync(legacyPath, System.Text.Json.JsonSerializer.Serialize(
            new StoredTokens { AccessToken = "legacy", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GitHubUsername = "old-user", Provider = AuthProvider.GitHubApp },
            CapacitorJsonContext.Default.StoredTokens));
        using var handler = GitHubDiscoveryScript();
        var facade = NewFacade(Config.Root, new RecordingAuthProgress(), handler, PickerReturningFirst());

        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(ReadConfig().ActiveProfile).IsEqualTo("acme");
        await Assert.That(File.Exists(legacyPath)).IsFalse();
        await Assert.That((await NewTokenStore(Config.Root).LoadAsync("old"))!.GitHubUsername).IsEqualTo("old-user");
    }

    ProfileConfig ReadConfig() => ConfigMutator.LoadPure(ConfigPath);
```

- [ ] **Step 2: Run to verify it fails**

`CommitBoundaryTests` filter. Expected: fails — `old.json` does not exist (today's save for `acme` deletes the legacy file outright, or after Task 2 leaves it in place but never migrates it).

- [ ] **Step 3: Implement**

In `OnboardingFacade.cs`, add to `CommitBoundary`:

```csharp
    /// Every writer of <c>active_profile</c> settles the outgoing profile's legacy credential first,
    /// so the name the legacy file follows never moves away from it. Null on success, else the line
    /// to report.
    internal static async Task<string?> SettleLegacyCredentialAsync(ConfigRoot root, TokenStore store, CancellationToken ct) {
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(root), out var before))
            return "Error: the configuration file could not be read.";
        try {
            await store.MigrateLegacyAsync(before.ActiveName, ct);
            return null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) {
            return $"Error: could not move the saved sign-in of profile '{before.ActiveName}': {ex.Message}";
        }
    }
```

In `DiscoverGitHubAsync`, immediately before `var request = new CommitRequest(` add:

```csharp
        if (await CommitBoundary.SettleLegacyCredentialAsync(root, store, ct) is { } settleError)
            return Fail(settleError, settleError, ct);
```

In `WorkOSDiscovery.PublishAsync`, immediately before `var request = new CommitRequest(` add:

```csharp
        if (await CommitBoundary.SettleLegacyCredentialAsync(root, store, ct) is { } settleError) {
            progress.Error(settleError);

            return new AuthResult.Failed(settleError);
        }
```

- [ ] **Step 4: Run to verify it passes**

`CommitBoundaryTests` filter, then the whole Core suite (`WorkOSDiscoveryTests` and `TenantDiscoveryTests` cover the WorkOS path). Expected: green.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.Cli.Core/Auth/OnboardingFacade.cs src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs test/Capacitor.Cli.Core.Tests.Unit/Auth/CommitBoundaryTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Settle the legacy token before discovery moves the selection (#1093)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: `ProfilesSettingsViewModel` and `ProfileRow`

**Files:**
- Create: `src/Capacitor.App/ViewModels/ProfileCredentialStatus.cs`, `src/Capacitor.App/ViewModels/ProfileRow.cs`, `src/Capacitor.App/ViewModels/ProfilesSettingsViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/ProfilesSettingsViewModelTests.cs` (create)

**Interfaces:**
- Consumes: `ProfileRemoval.RemoveAsync`, `ConfigMutator.TryLoadPure`, `OnboardingGate.EvaluateResolvedAsync(string?, Profile?, CancellationToken)`, `OnboardingGate.ValidServerUrl`, `ServerIdentity.SameServer`, `AuthProvider.None`.
- Produces: `public enum ProfileCredentialStatus { SignedIn, Expired, SignedOut, OtherServer, NoSignInNeeded, NoServer, Unreadable }`; `public sealed record ProfileRow(string Name, string? ServerUrl, bool IsActive, bool IsBound, ProfileCredentialStatus Status)` with `StatusLabel`, `ServerLabel`, `CanSignIn`, `CanRemove`; `public sealed class ProfilesSettingsViewModel(ConfigRoot config, TokenStore tokens, OnboardingGate gate, string boundProfile, Func<string, string, CancellationToken, Task> openSignIn, Func<string, CancellationToken, Task<bool>> confirmRemove, CancellationToken appLifetime = default)` with `IReadOnlyList<ProfileRow> Rows`, `string? Message`, `bool IsBusy`, `ReactiveCommand<ProfileRow, System.Reactive.Unit> SignInCommand`, `RemoveCommand`, `Task RefreshAsync()`.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.App.Tests.Unit/ProfilesSettingsViewModelTests.cs`:

```csharp
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class ProfilesSettingsViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string TokensDir => Config.PathTo("tokens");

    static StoredTokens Token(string provider, DateTimeOffset expires, string? server = null, string? refresh = null) => new() {
        AccessToken = "at", ExpiresAt = expires, Provider = provider, ServerUrl = server, RefreshToken = refresh, ClientId = refresh is null ? null : "cid"
    };

    async Task<TokenStore> Seed() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = "work",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new(),
                ["work"]    = new() { ServerUrl = "https://work.example" },
                ["other"]   = new() { ServerUrl = "https://other.example" },
                ["stale"]   = new() { ServerUrl = "https://stale.example" },
                ["moved"]   = new() { ServerUrl = "https://moved.example" },
                ["open"]    = new() { ServerUrl = "https://open.example", AuthProvider = new AuthProviderStamp(AuthProvider.None, "https://open.example") },
                ["bad"]     = new() { ServerUrl = "https://bad.example" }
            }
        });
        var tokens = AuthFixtures.NewTokenStore(Config.Root);
        await tokens.SaveAsync("work",  Token(AuthProvider.GitHubApp, DateTimeOffset.UtcNow.AddHours(1), "https://work.example"));
        await tokens.SaveAsync("stale", Token(AuthProvider.WorkOS, DateTimeOffset.UtcNow.AddHours(-1), "https://stale.example"));
        await tokens.SaveAsync("moved", Token(AuthProvider.GitHubApp, DateTimeOffset.UtcNow.AddHours(1), "https://elsewhere.example"));
        Directory.CreateDirectory(Path.Combine(TokensDir, "bad.json")); // a directory where the file should be: the read throws
        return tokens;
    }

    ProfilesSettingsViewModel Make(TokenStore tokens, string bound = "work",
            Func<string, string, CancellationToken, Task>? openSignIn = null,
            Func<string, CancellationToken, Task<bool>>? confirm = null) =>
        new(Config.Root, tokens, new OnboardingGate(Config.Root, tokens, ProfileOverrides.None, TimeProvider.System), bound,
            openSignIn ?? ((_, _, _) => Task.CompletedTask), confirm ?? ((_, _) => Task.FromResult(true)));

    [Test]
    public Task Rows_reflect_config_marks_and_credential_status() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed());
        await vm.RefreshAsync();

        var rows = vm.Rows.ToDictionary(r => r.Name);
        await Assert.That(rows.Count).IsEqualTo(7);
        await Assert.That(rows["work"].Status).IsEqualTo(ProfileCredentialStatus.SignedIn);
        await Assert.That(rows["work"].IsActive).IsTrue();
        await Assert.That(rows["work"].IsBound).IsTrue();
        await Assert.That(rows["work"].CanRemove).IsFalse();
        await Assert.That(rows["other"].Status).IsEqualTo(ProfileCredentialStatus.SignedOut);
        await Assert.That(rows["other"].CanRemove).IsTrue();
        await Assert.That(rows["other"].CanSignIn).IsTrue();
        await Assert.That(rows["stale"].Status).IsEqualTo(ProfileCredentialStatus.Expired);
        await Assert.That(rows["moved"].Status).IsEqualTo(ProfileCredentialStatus.OtherServer);
        await Assert.That(rows["open"].Status).IsEqualTo(ProfileCredentialStatus.NoSignInNeeded);
        await Assert.That(rows["open"].CanSignIn).IsFalse();
        await Assert.That(rows["default"].Status).IsEqualTo(ProfileCredentialStatus.NoServer);
        await Assert.That(rows["default"].CanSignIn).IsFalse();
        await Assert.That(rows["default"].CanRemove).IsFalse();
        await Assert.That(rows["bad"].Status).IsEqualTo(ProfileCredentialStatus.Unreadable);
        await Assert.That(rows["bad"].CanSignIn).IsTrue();
    });

    [Test]
    public Task Active_and_bound_are_marked_separately() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed(), bound: "other");
        await vm.RefreshAsync();

        var rows = vm.Rows.ToDictionary(r => r.Name);
        await Assert.That(rows["work"].IsActive).IsTrue();
        await Assert.That(rows["work"].IsBound).IsFalse();
        await Assert.That(rows["work"].CanRemove).IsFalse();
        await Assert.That(rows["other"].IsBound).IsTrue();
        await Assert.That(rows["other"].CanRemove).IsFalse();
    });

    [Test]
    public Task Remove_deletes_the_profile_and_its_token_after_confirmation() => AvaloniaSession.RunOnUiAsync(async () => {
        var tokens = await Seed();
        string? asked = null;
        var vm = Make(tokens, confirm: (name, _) => { asked = name; return Task.FromResult(true); });
        await vm.RefreshAsync();

        await vm.RemoveCommand.Execute(vm.Rows.Single(r => r.Name == "stale")).ToTask();

        await Assert.That(asked).IsEqualTo("stale");
        await Assert.That(vm.Rows.Any(r => r.Name == "stale")).IsFalse();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "stale.json"))).IsFalse();
        await Assert.That(vm.Message!).Contains("removed");
    });

    [Test]
    public Task Remove_does_nothing_when_declined() => AvaloniaSession.RunOnUiAsync(async () => {
        var tokens = await Seed();
        var vm = Make(tokens, confirm: (_, _) => Task.FromResult(false));
        await vm.RefreshAsync();

        await vm.RemoveCommand.Execute(vm.Rows.Single(r => r.Name == "stale")).ToTask();

        await Assert.That(vm.Rows.Any(r => r.Name == "stale")).IsTrue();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "stale.json"))).IsTrue();
    });

    [Test]
    public Task Remove_of_a_row_changed_since_the_last_read_is_refused() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed());
        await vm.RefreshAsync();
        var row = vm.Rows.Single(r => r.Name == "other");
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles = new Dictionary<string, Profile>(c.Profiles) { ["other"] = new() { ServerUrl = "https://repointed.example" } }
        });

        await vm.RemoveCommand.Execute(row).ToTask();

        await Assert.That(vm.Message).IsEqualTo("This profile changed; the list was refreshed.");
        await Assert.That(vm.Rows.Single(r => r.Name == "other").ServerUrl).IsEqualTo("https://repointed.example");
    });

    [Test]
    public Task SignIn_opens_the_dialog_for_the_row() => AvaloniaSession.RunOnUiAsync(async () => {
        (string Profile, string Server)? opened = null;
        var vm = Make(await Seed(), openSignIn: (p, s, _) => { opened = (p, s); return Task.CompletedTask; });
        await vm.RefreshAsync();

        await vm.SignInCommand.Execute(vm.Rows.Single(r => r.Name == "other")).ToTask();

        await Assert.That(opened).IsEqualTo(("other", "https://other.example"));
    });

    [Test]
    public Task Refresh_reports_an_unreadable_config_and_keeps_the_rows() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed());
        await vm.RefreshAsync();
        var before = vm.Rows.Count;
        await File.WriteAllTextAsync(AppConfig.GetConfigPath(Config.Root), "{ not json");

        await vm.RefreshAsync();

        await Assert.That(vm.Message).IsEqualTo("Could not read the profile configuration.");
        await Assert.That(vm.Rows.Count).IsEqualTo(before);
    });
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ProfilesSettingsViewModelTests/*"`
Expected: compile errors — the three types do not exist.

- [ ] **Step 3: Implement**

`src/Capacitor.App/ViewModels/ProfileCredentialStatus.cs`:

```csharp
namespace Capacitor.App.ViewModels;

public enum ProfileCredentialStatus { SignedIn, Expired, SignedOut, OtherServer, NoSignInNeeded, NoServer, Unreadable }
```

`src/Capacitor.App/ViewModels/ProfileRow.cs`:

```csharp
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.ViewModels;

/// One profile as the Settings list shows it. Active is what config.json names now; bound is the
/// profile this process resolved at startup, which an external `kcap use --global` can leave behind.
public sealed record ProfileRow(string Name, string? ServerUrl, bool IsActive, bool IsBound, ProfileCredentialStatus Status) {
    public string StatusLabel => Status switch {
        ProfileCredentialStatus.SignedIn       => "Signed in",
        ProfileCredentialStatus.Expired        => "Sign-in expired",
        ProfileCredentialStatus.SignedOut      => "Signed out",
        ProfileCredentialStatus.OtherServer    => "Signed in to another server",
        ProfileCredentialStatus.NoSignInNeeded => "No sign-in needed",
        ProfileCredentialStatus.NoServer       => "No server configured",
        _                                      => "Could not read sign-in status"
    };

    public string ServerLabel => ServerUrl is { Length: > 0 } url ? url : "Add a server with Add profile or kcap profile add";

    public bool CanSignIn => Status is not (ProfileCredentialStatus.NoServer or ProfileCredentialStatus.NoSignInNeeded);

    public bool CanRemove => !IsActive && !IsBound && Name != ProfileConfig.DefaultName;
}
```

`src/Capacitor.App/ViewModels/ProfilesSettingsViewModel.cs`:

```csharp
using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class ProfilesSettingsViewModel : ReactiveObject {
    readonly ConfigRoot _config;
    readonly TokenStore _tokens;
    readonly OnboardingGate _gate;
    readonly string _boundProfile;
    readonly Func<string, string, CancellationToken, Task> _openSignIn;
    readonly Func<string, CancellationToken, Task<bool>> _confirmRemove;
    readonly CancellationToken _lifetime;
    IReadOnlyList<ProfileRow> _rows = [];
    string? _message;
    bool _isBusy;

    public ProfilesSettingsViewModel(
            ConfigRoot config, TokenStore tokens, OnboardingGate gate, string boundProfile,
            Func<string, string, CancellationToken, Task> openSignIn,
            Func<string, CancellationToken, Task<bool>> confirmRemove,
            CancellationToken appLifetime = default) {
        _config = config;
        _tokens = tokens;
        _gate = gate;
        _boundProfile = boundProfile;
        _openSignIn = openSignIn;
        _confirmRemove = confirmRemove;
        _lifetime = appLifetime;

        var idle = this.WhenAnyValue(x => x.IsBusy, busy => !busy);
        SignInCommand = ReactiveCommand.CreateFromTask<ProfileRow>(SignInAsync, idle);
        RemoveCommand = ReactiveCommand.CreateFromTask<ProfileRow>(RemoveAsync, idle);
    }

    public IReadOnlyList<ProfileRow> Rows {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public bool IsBusy {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public ReactiveCommand<ProfileRow, Unit> SignInCommand { get; }
    public ReactiveCommand<ProfileRow, Unit> RemoveCommand { get; }

    /// Rebuilds the rows from a fresh read; an unreadable config keeps the rows shown and says so.
    public async Task RefreshAsync() {
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(_config), out var config)) {
            Message = "Could not read the profile configuration.";
            return;
        }

        var rows = new List<ProfileRow>();
        foreach (var (name, profile) in config.Profiles.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            rows.Add(new ProfileRow(name, profile.ServerUrl, name == config.ActiveName, name == _boundProfile,
                await StatusAsync(name, profile)));
        }
        Rows = rows;
    }

    // Refresh-free on purpose: grading a row must never spend a single-use refresh token.
    async Task<ProfileCredentialStatus> StatusAsync(string name, Profile profile) {
        if (!OnboardingGate.ValidServerUrl(profile.ServerUrl)) return ProfileCredentialStatus.NoServer;
        if (profile.AuthProvider is { } stamp
                && string.Equals(stamp.Provider, AuthProvider.None, StringComparison.OrdinalIgnoreCase)
                && ServerIdentity.SameServer(stamp.ServerUrl, profile.ServerUrl))
            return ProfileCredentialStatus.NoSignInNeeded;

        try {
            return await _gate.EvaluateResolvedAsync(name, profile, _lifetime) switch {
                GateResult.Complete                                                    => ProfileCredentialStatus.SignedIn,
                GateResult.Incomplete { Reason: GateReason.NoToken }                   => ProfileCredentialStatus.SignedOut,
                GateResult.Incomplete { Reason: GateReason.TokenUnusableBinding }      => ProfileCredentialStatus.OtherServer,
                GateResult.Incomplete { Reason: GateReason.TokenUnusableExpired }      => ProfileCredentialStatus.Expired,
                _                                                                      => ProfileCredentialStatus.NoServer
            };
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not read the sign-in status of profile '{name}': {ex.Message}");
            return ProfileCredentialStatus.Unreadable;
        }
    }

    async Task SignInAsync(ProfileRow row) {
        IsBusy = true;
        Message = null;
        try {
            if (await CurrentAsync(row) is not { } current) return;
            await _openSignIn(current.Name, current.ServerUrl!, _lifetime);
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Message = $"Could not open sign-in: {ex.Message}";
        } finally { IsBusy = false; }
    }

    async Task RemoveAsync(ProfileRow row) {
        IsBusy = true;
        Message = null;
        try {
            if (await CurrentAsync(row) is not { } current) return;
            if (!current.CanRemove) {
                Message = "This profile cannot be removed.";
                return;
            }
            if (!await _confirmRemove(current.Name, _lifetime)) return;

            var result = await ProfileRemoval.RemoveAsync(_config, _tokens, current.Name, _lifetime);
            Message = result.Outcome switch {
                ProfileRemovalOutcome.Removed              => $"Profile {current.Name} removed.",
                ProfileRemovalOutcome.RemovedTokenRetained => $"Profile {current.Name} removed, but its sign-in file could not be deleted ({result.Detail}).",
                ProfileRemovalOutcome.IsActive             => $"Profile {current.Name} is the active profile and cannot be removed.",
                ProfileRemovalOutcome.IsDefault            => "The default profile cannot be removed.",
                ProfileRemovalOutcome.NotFound             => $"Profile {current.Name} no longer exists.",
                _                                          => "The profile configuration could not be read; nothing was removed."
            };
            await RefreshAsync();
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Message = $"Could not remove the profile: {ex.Message}";
        } finally { IsBusy = false; }
    }

    // Every action re-reads first: a row that no longer matches the file is refused, not acted on.
    async Task<ProfileRow?> CurrentAsync(ProfileRow row) {
        await RefreshAsync();
        var current = Rows.FirstOrDefault(r => r.Name == row.Name);
        var sameServer = current is not null
            && (string.Equals(current.ServerUrl, row.ServerUrl, StringComparison.Ordinal)
                || ServerIdentity.SameServer(current.ServerUrl, row.ServerUrl));
        if (current is not null && sameServer) return current;

        Message = "This profile changed; the list was refreshed.";
        return null;
    }
}
```

If `ReactiveUI.Reactive` shadows `Unit`, alias it as the other app tests do: `using Unit = System.Reactive.Unit;`.

- [ ] **Step 4: Run to verify they pass**

`ProfilesSettingsViewModelTests` filter. Expected: seven tests pass. Note the `bad` profile's status relies on `File.ReadAllTextAsync` throwing `UnauthorizedAccessException` for a directory on macOS/Linux and Windows alike.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.App/ViewModels/ProfileCredentialStatus.cs src/Capacitor.App/ViewModels/ProfileRow.cs src/Capacitor.App/ViewModels/ProfilesSettingsViewModel.cs test/Capacitor.App.Tests.Unit/ProfilesSettingsViewModelTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Add the profiles settings view model (#1093)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 12: Row sign-in — thread `ExpectServer` and gate success on `CredentialSaved`

**Files:**
- Modify: `src/Capacitor.App/Services/Onboarding/WizardComposition.cs`, `src/Capacitor.App/Services/Onboarding/WizardAuthBridges.cs`, `src/Capacitor.App/Services/Onboarding/ReauthComposition.cs`, `src/Capacitor.App/ViewModels/Onboarding/SignInStepViewModel.cs`, `src/Capacitor.App/App.axaml.cs`
- Test: `test/Capacitor.App.Tests.Unit/SignInStepViewModelTests.cs`

**Interfaces:**
- Consumes: `CommitPrecondition.ExpectServer`, `AuthResult.Committed.CredentialSaved` (Task 9).
- Produces: `WizardFacadeSpec` gains `CommitPrecondition? Precondition = null` (last positional); `WizardSignInOperation.For(OnboardingFacade facade, string profile, CommitPrecondition? precondition = null)`; `WizardComposition.BuildOperation(..., operation, CommitPrecondition? precondition = null)`; `ReauthComposition.Build(..., operation, CommitPrecondition? precondition = null)`; `App.OpenSignInDialog(string profile, string serverUrl, bool refreshAppState, IAppNotifier notifier)`.

- [ ] **Step 1: Write the failing test**

Append to `SignInStepViewModelTests` (inside the class, after the committed-outcomes tests):

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_commit_whose_credential_was_not_saved_does_not_satisfy_the_step() {
        var (satisfied, isError, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Prefill("https://acme.example");
            h.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Committed("acme", "https://acme.example:443", AuthProvider.GitHubApp, "sam", [], CredentialSaved: false));

            await h.Vm.OnEnterAsync(CancellationToken.None);
            await h.SignIn();

            return (h.Vm.Satisfied, h.Vm.StatusIsError, h.Vm.Status);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(isError).IsTrue();
        await Assert.That(status).IsEqualTo("Signed in, but the credential could not be saved.");
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/SignInStepViewModelTests/*"`
Expected: fails — `Satisfied` is true and the status reads "Signed in as sam".

- [ ] **Step 3: Implement**

`SignInStepViewModel.Apply`: insert before `case AuthResult.Committed committed:`:

```csharp
            case AuthResult.Committed { CredentialSaved: false }:
                SetStatus("Signed in, but the credential could not be saved.", isError: true,
                    _lastReport ?? "Sign in again from the profile's row in Settings.");

                break;
```

`WizardComposition.cs`:
- `WizardFacadeSpec`: add a final positional parameter `CommitPrecondition? Precondition = null`.
- `NewOperation`: `WizardSignInOperation.For(new OnboardingFacade(...), spec.Profile, spec.Precondition)`.
- `BuildOperation`: add `CommitPrecondition? precondition = null` as the last parameter and pass it as the spec's last argument: `WizardAuthService.ArmingHook(claims), precondition));`.

`WizardAuthBridges.cs`, `WizardSignInOperation.For`:

```csharp
    public static Func<ConnectIntent, CancellationToken, Task<AuthResult>> For(
            OnboardingFacade facade, string profile, CommitPrecondition? precondition = null) =>
        async (intent, ct) => intent switch {
            ConnectIntent.Paste paste => await facade.LoginAsync(
                ResolveServer(paste.ServerInput), forceDevice: false, profile, ct, adoptServer: true, precondition: precondition),
```

(the other arms unchanged).

`ReauthComposition.Build`: add `CommitPrecondition? precondition = null` as the last parameter and pass it to `BuildOperation` after `operation`.

`App.axaml.cs`:

1. Replace `Action requestSignIn = () => OpenSignInDialog(profiles, notifier);` with

```csharp
        Action requestSignIn = () => {
            // Reachable in the carve-out arm (gate Incomplete after an abandoned wizard), where the
            // rail can show disconnected with no server to re-auth against.
            if (profiles?.Resolution.ServerUrl is not { } serverUrl || !OnboardingGate.ValidServerUrl(serverUrl)) {
                notifier.Notify("No server is configured. Run kcap setup first.");
                return;
            }
            OpenSignInDialog(profiles.Name, serverUrl, refreshAppState: true, notifier);
        };
```

2. Change `OpenSignInDialog` to:

```csharp
    /// The re-auth dialog over a fresh ReauthComposition graph, pinned to one profile and server.
    /// A graph is built per open — a settled attempt's rendered state must never leak into the next
    /// sign-in. The commit checks the profile still names the server, so a row edited while the
    /// browser was open is refused rather than stamped.
    void OpenSignInDialog(string profile, string serverUrl, bool refreshAppState, IAppNotifier notifier) {
        if (_signInWindow is { } open) {
            open.Activate();
            return;
        }

        var graph = ReauthComposition.Build(
            _config,
            _foreignHttp.GetRequiredService<TokenStore>(),
            _foreignHttp.GetRequiredService<IHttpClientFactory>(),
            _foreignHttp.GetRequiredService<IAuthProxyClient>(),
            _foreignHttp.GetRequiredService<GitHubOAuthClient>(),
            _foreignHttp.GetRequiredService<WorkOSClient>(),
            profile, serverUrl,
            WizardComposition.BuildBridges(
                action => Dispatcher.UIThread.Post(action),
                _foreignHttp.GetRequiredService<TenantProvisioningClient>(), _telemetry, _endpoints, _time),
            new ConsentFlipClaims(_config),
            new AppStateStore(_config.Path("app-state.json")),
            new ShellUrlOpener(),
            _time,
            WizardComposition.NewOperation,
            new CommitPrecondition.ExpectServer(serverUrl));
        var window = new SignInWindow { DataContext = graph.SignIn };

        void OnSignInChanged(object? _, PropertyChangedEventArgs e) {
            if (e.PropertyName == nameof(SignInStepViewModel.Satisfied) && graph.SignIn.Satisfied)
                _ = CloseSignInAfterSuccessAsync(window, refreshAppState);
        }

        graph.SignIn.PropertyChanged += OnSignInChanged;
        window.Closed += (_, _) => {
            graph.SignIn.PropertyChanged -= OnSignInChanged;
            _signInWindow = null;
            _reauthSettle = FinishSignInAsync(graph, refreshAppState);
        };

        _signInWindow = window;
        window.Show();
    }
```

3. `CloseSignInAfterSuccessAsync(Window window)` → `CloseSignInAfterSuccessAsync(Window window, bool refreshAppState)`, with `var refresh = refreshAppState ? RefreshAfterReauthAsync() : Task.CompletedTask;`.
4. `FinishSignInAsync(ReauthGraph graph)` → `FinishSignInAsync(ReauthGraph graph, bool refreshAppState)`, with `if (refreshAppState && graph.SignIn.Satisfied) await RefreshAfterReauthAsync();`.
5. Add `using Capacitor.Cli.Core.Auth;` if `CommitPrecondition` does not resolve (the file already references `TokenStore`, so it is present).

- [ ] **Step 4: Run to verify it passes**

`SignInStepViewModelTests` filter, then `dotnet build src/Capacitor.App/Capacitor.App.csproj` — zero warnings, AVLN included. Expected: green.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.App/Services/Onboarding/WizardComposition.cs src/Capacitor.App/Services/Onboarding/WizardAuthBridges.cs src/Capacitor.App/Services/Onboarding/ReauthComposition.cs src/Capacitor.App/ViewModels/Onboarding/SignInStepViewModel.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/SignInStepViewModelTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Pin the sign-in dialog to a profile and require the saved credential (#1093)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 13: The Profiles tab — view, third tab, wiring

**Files:**
- Create: `src/Capacitor.App/Views/ProfilesSettingsView.axaml`, `src/Capacitor.App/Views/ProfilesSettingsView.axaml.cs`
- Modify: `src/Capacitor.App/Views/SettingsWindow.axaml`, `src/Capacitor.App/ViewModels/SettingsViewModel.cs`, `src/Capacitor.App/Services/ILifecycleSurface.cs`, `src/Capacitor.App/ViewModels/LifecyclePromptViewModel.cs`, `src/Capacitor.App/App.axaml.cs`
- Test: `test/Capacitor.App.Tests.Unit/SettingsWindowSmokeTests.cs`

**Interfaces:**
- Consumes: `ProfilesSettingsViewModel` (Task 11), `OpenSignInDialog(profile, serverUrl, refreshAppState, notifier)` (Task 12).
- Produces: `SettingsViewModel` constructor gains `ProfilesSettingsViewModel? profiles = null` (last parameter) and `public ProfilesSettingsViewModel? Profiles { get; }`; `LifecyclePrompt.KindRemoveProfile = "remove-profile"`.

- [ ] **Step 1: Write the failing smoke test**

Append to `SettingsWindowSmokeTests`:

```csharp
    [Test]
    public Task Profiles_tab_lists_rows_with_chip_actions_and_purple_marks() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"]  = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } },
                ["other"] = new Profile { ServerUrl = "https://other.example" }
            }
        });
        var tokens = AuthFixtures.NewTokenStore(Config.Root);
        var profiles = new ProfilesSettingsViewModel(Config.Root, tokens,
            new OnboardingGate(Config.Root, tokens, ProfileOverrides.None, TimeProvider.System), "work",
            (_, _, _) => Task.CompletedTask, (_, _) => Task.FromResult(false));
        await profiles.RefreshAsync();
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["settings/1"]));
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(active: 0));
        using var vm = new SettingsViewModel(new SettingsProfileStore(Config.Root, "work", "https://work.example"),
            service, new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
            (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask, (_, _) => Task.FromResult(true),
            profiles: profiles);
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var tabs = window.FindControl<TabControl>("SettingsTabs")!;
            var tab  = window.FindControl<TabItem>("ProfilesTab")!;
            tabs.SelectedItem = tab;
            Dispatcher.UIThread.RunJobs();
            Settle(window);

            var chips = tab.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("kcapChip")).ToList();
            await Assert.That(chips.Any(b => b.Content as string == "Sign in")).IsTrue();
            await Assert.That(chips.Any(b => b.Content as string == "Remove" && b.IsVisible)).IsTrue();

            var marks = tab.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("profileMark") && t.IsVisible).ToList();
            await Assert.That(marks.Count).IsEqualTo(2); // Active and This app, both on "work"
            foreach (var mark in marks)
                await Assert.That(ReferenceEquals(mark.Foreground, window.FindResource("KcapPurpleBrush"))).IsTrue();

            var statuses = tab.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("profileStatus")).Select(t => t.Text).ToList();
            await Assert.That(statuses).Contains("Signed out");
        } finally {
            window.Close();
        }
    });
```

Add `using Capacitor.App.Services.Onboarding;` and `using Capacitor.Cli.Core.Auth;` to the file's usings.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/SettingsWindowSmokeTests/*"`
Expected: compile error — `SettingsViewModel` has no `profiles` parameter.

- [ ] **Step 3: Implement**

`src/Capacitor.App/Views/ProfilesSettingsView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:Capacitor.App.ViewModels"
             x:Class="Capacitor.App.Views.ProfilesSettingsView"
             x:DataType="vm:ProfilesSettingsViewModel">
    <ScrollViewer VerticalScrollBarVisibility="Auto" Margin="0,18,0,0">
        <StackPanel Spacing="16">
            <StackPanel Spacing="10">
                <TextBlock x:Name="ProfilesTitleText" Classes="kcapTitle" Text="Profiles" LineHeight="22" />
                <TextBlock Classes="kcapSubtitle" TextWrapping="Wrap"
                           Text="Each profile is a server and a sign-in. This app runs against one profile until it is relaunched." />
            </StackPanel>

            <ItemsControl x:Name="ProfileRows" ItemsSource="{Binding Rows}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate>
                        <StackPanel Spacing="12" />
                    </ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate x:DataType="vm:ProfileRow">
                        <Border Classes="profileRow" Background="{StaticResource KcapSurfaceBrush}"
                                BorderBrush="{StaticResource KcapBorderBrush}" BorderThickness="1" CornerRadius="12" Padding="20">
                            <Grid ColumnDefinitions="*,Auto" ColumnSpacing="18">
                                <StackPanel Spacing="6">
                                    <StackPanel Orientation="Horizontal" Spacing="8">
                                        <TextBlock Classes="kcapLabel" Text="{Binding Name}" VerticalAlignment="Center" />
                                        <TextBlock Classes="profileMark" Text="Active" IsVisible="{Binding IsActive}"
                                                   Foreground="{StaticResource KcapPurpleBrush}" FontSize="11" FontWeight="SemiBold"
                                                   VerticalAlignment="Center" />
                                        <TextBlock Classes="profileMark" Text="This app" IsVisible="{Binding IsBound}"
                                                   Foreground="{StaticResource KcapPurpleBrush}" FontSize="11" FontWeight="SemiBold"
                                                   VerticalAlignment="Center" />
                                    </StackPanel>
                                    <TextBlock Classes="kcapHint" Text="{Binding ServerLabel}" TextWrapping="Wrap" />
                                    <TextBlock Classes="kcapHint profileStatus" Text="{Binding StatusLabel}"
                                               Foreground="{StaticResource KcapTextBrush}" />
                                </StackPanel>
                                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="8" VerticalAlignment="Top">
                                    <Button Classes="kcapChip profileSignIn" Content="Sign in" IsVisible="{Binding CanSignIn}"
                                            Command="{Binding $parent[ItemsControl].((vm:ProfilesSettingsViewModel)DataContext).SignInCommand}"
                                            CommandParameter="{Binding}" />
                                    <Button Classes="kcapChip profileRemove" Content="Remove" IsVisible="{Binding CanRemove}"
                                            Command="{Binding $parent[ItemsControl].((vm:ProfilesSettingsViewModel)DataContext).RemoveCommand}"
                                            CommandParameter="{Binding}" />
                                </StackPanel>
                            </Grid>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>

            <TextBlock x:Name="ProfilesMessageText" Classes="kcapHint" Text="{Binding Message}" TextWrapping="Wrap"
                       IsVisible="{Binding Message, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

`src/Capacitor.App/Views/ProfilesSettingsView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace Capacitor.App.Views;

public partial class ProfilesSettingsView : UserControl {
    public ProfilesSettingsView() => InitializeComponent();
}
```

`SettingsWindow.axaml`: add `xmlns:views="clr-namespace:Capacitor.App.Views"` to the `Window` element, and insert between the Daemon and Notifications tabs:

```xml
            <TabItem x:Name="ProfilesTab" Classes="kcapSettingsTab" Header="Profiles"
                     IsVisible="{Binding Profiles, Converter={x:Static ObjectConverters.IsNotNull}}">
                <views:ProfilesSettingsView DataContext="{Binding Profiles}" />
            </TabItem>
```

`SettingsViewModel.cs`: add a constructor parameter `ProfilesSettingsViewModel? profiles = null` after `notificationSettings`, assign `Profiles = profiles;`, and add `public ProfilesSettingsViewModel? Profiles { get; }`.

`ILifecycleSurface.cs`: add `public const string KindRemoveProfile = "remove-profile";` to `LifecyclePrompt`.

`LifecyclePromptViewModel.cs`: add `LifecyclePrompt.KindRemoveProfile => "Remove"` to the `AcceptButtonText` switch and `LifecyclePrompt.KindRemoveProfile => "Remove profile"` to `TitleFor`.

`App.axaml.cs`, in `OpenSettings`, before `SettingsViewModel vm;`:

```csharp
        var tokenStore = _foreignHttp.GetRequiredService<TokenStore>();
        var profilesVm = new ProfilesSettingsViewModel(
            _config, tokenStore, new OnboardingGate(_config, tokenStore, _serverEnv, _time), settings.ProfileName,
            openSignIn: (profile, serverUrl, _) => {
                OpenSignInDialog(profile, serverUrl, refreshAppState: profile == settings.ProfileName, notifier);
                return Task.CompletedTask;
            },
            confirmRemove: (name, ct) => ShowLifecyclePromptDialogAsync(_settingsWindow,
                new LifecyclePrompt(LifecyclePrompt.KindRemoveProfile, null, null, false,
                    $"Remove profile {name}? Its saved sign-in is deleted too. Nothing on the server changes."), ct),
            appLifetime: _shutdown.Token);
```

pass `profiles: profilesVm` to the `SettingsViewModel` constructor, and after `window.Closed += ...` add:

```csharp
        // The list is re-read whenever the window regains focus, so a `kcap use` or `kcap profile
        // add` made in a terminal shows up on return without a file watcher.
        window.Activated += (_, _) => _ = profilesVm.RefreshAsync();
        _ = profilesVm.RefreshAsync();
```

Add `using Capacitor.App.Services.Onboarding;` and `using Capacitor.Cli.Core.Auth;` to `App.axaml.cs` if not already present.

- [ ] **Step 4: Run to verify it passes**

`SettingsWindowSmokeTests` filter, then the whole app suite: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`, then `dotnet build src/Capacitor.App/Capacitor.App.csproj` with zero warnings (AVLN included). Expected: green.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add src/Capacitor.App/Views/ProfilesSettingsView.axaml src/Capacitor.App/Views/ProfilesSettingsView.axaml.cs src/Capacitor.App/Views/SettingsWindow.axaml src/Capacitor.App/ViewModels/SettingsViewModel.cs src/Capacitor.App/Services/ILifecycleSurface.cs src/Capacitor.App/ViewModels/LifecyclePromptViewModel.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/SettingsWindowSmokeTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Add the Profiles tab to desktop Settings (#1093)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 14: Change notes, AOT check, full sweep

**Files:**
- Modify: `docs/CHANGES.md`

- [ ] **Step 1: Add the change note**

Insert after the header paragraphs of `docs/CHANGES.md` (before the first `## `):

```markdown
## Profiles in Settings, and every credential write under its profile lock

The desktop app lists profiles from `config.json` and grades each with the same refresh-free gate
the app starts with, so a row never spends a single-use refresh token. Removal is one operation in
Core shared with `kcap profile remove`: it decides on the locked config, refuses the active profile
instead of resetting the selection to an empty `default`, and deletes the credential under the
profile's token lock only when no remaining profile can still read that file. Every token write
now takes that lock, a refresh re-reads under it rather than reviving a file deleted while it
waited, and the legacy `tokens.json` is moved into its owner's slot before any writer of
`active_profile` changes the selection. Mutations whose decision depends on what the file says go
through a strict variant that refuses an unreadable config rather than publishing a default over it.
```

- [ ] **Step 2: Build the solution and run every affected suite**

```bash
dotnet build Capacitor.slnx
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj
```

Expected: build with zero warnings; all three suites green. The daemon suite is untouched by this PR but `dotnet build Capacitor.slnx` proves it still compiles against the changed `TokenStore` surface.

- [ ] **Step 3: AOT check**

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output.

- [ ] **Step 4: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 add docs/CHANGES.md
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/agent-c48630062fe142 commit -q -m "Note why profile removal and token writes changed shape (#1093)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

- [ ] **Step 5: Open the PR**

Follow `.github/PULL_REQUEST_TEMPLATE.md` for the body. Title: `Add a Profiles tab to desktop Settings`. The reference line carries `Part of #1093` and `AI-3072`. Push with `git push https://github.com/kurrent-io/kcap-cli.git capacitor/agent-c48630062fe142` (SSH pushes are refused from these shells). Do not enable auto-merge: only `license/cla` is a required check, so `--auto` would merge before build and tests run.

---

## Self-review notes

- **Spec coverage (PR 1 scope):** Profiles tab rows, marks, statuses and actions → Tasks 11, 13. Re-read on open/action/activation → Task 11 (`CurrentAsync`), Task 13 (`Activated`). Sign in with `ExpectServer` and success gated on the saved credential → Tasks 9, 12. Strict config mutation → Task 1, used by Tasks 6, 8, 9. Commit guards, two-part save guard, `CredentialSaved`, foreign login unchanged, paused-login race → Task 9. Remove (`ProfileRemoval`, alias guard, `RemovedTokenRetained`, `ConfigUnreadable`) → Tasks 6, 7. `kcap use` strict + migration → Task 8. Token lock (seams, reload under lock, owner-aware legacy delete, `MigrateLegacyAsync`, locked logout, `Delete(profile)` removed) → Tasks 2–5. Selection writers migrating (discovery) → Task 10. README + help → Task 7. CHANGES + AOT → Task 14.
- **Not in this PR, by the spec's delivery split:** Add (`ExpectAbsent`, `SeedFrom`, `IsValidProfileName`) and everything under Switch; "both sections read the lane's restart state before every action" is a Switch-era requirement and lands with PR 3.
- **Type consistency:** `GuardedWriteOutcome` (Task 2) is what `DeleteGuardedAsync` (Task 4), `ProfileRemoval` (Task 6) and the facade (Task 9) switch on; `SaveGuardedAsync`'s guard is `Func<ProfileConfig, bool>?` everywhere; `CommitPrecondition.ExpectServer(string Url)` is constructed in Tasks 9 and 12 with that name; `ProfilesSettingsViewModel`'s constructor order `(config, tokens, gate, boundProfile, openSignIn, confirmRemove, appLifetime)` is the same in Tasks 11 and 13.
