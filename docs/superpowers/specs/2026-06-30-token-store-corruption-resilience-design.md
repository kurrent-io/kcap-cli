# TokenStore corruption resilience — Design

**Date:** 2026-06-30
**Issue:** [AI-1082](https://linear.app/kurrent/issue/AI-1082/kcap-crashes-on-corrupt-token-file-harden-tokenstore-load-save)
**Status:** Implemented (PR [#208](https://github.com/kurrent-io/kcap-cli/pull/208)). rev2 — revised after a code-review pass that surfaced four follow-on issues (corrupt-active-vs-legacy fallback, read-time TOCTOU, leaked-temp secrets/permissions, logout temp sweep). Sections below reflect the shipped design.
**Author:** tony.young@kurrent.io (with Claude)

## Problem

A customer running `kcap status` got an **unhandled exception** that crashed the
command:

```
Auth:    Unhandled exception. System.Text.Json.JsonException: ',' is invalid after a
single JSON value. Expected end of data. Path: $ | LineNumber: 0 | BytePositionInLine: 492.
   ...
   at Capacitor.Cli.Core.Auth.TokenStore.LoadAsync(...)
   at Capacitor.Cli.Core.Auth.TokenStore.GetValidTokensAsync(...)
   at Capacitor.Cli.Commands.StatusCommand.HandleAsync(...)
```

The local token file on disk is corrupt, and `TokenStore` neither tolerates the
corruption on read nor reliably avoids producing it on write. The result is a
hard crash that takes down not just `kcap status` but every code path that loads
a token.

## Findings

### 1. The crash (root cause — certain)

`TokenStore.LoadAsync(string profile)` deserializes the file contents with no
error handling:

[`src/Capacitor.Cli.Core/Auth/TokenStore.cs:55`](../../../src/Capacitor.Cli.Core/Auth/TokenStore.cs)
```csharp
public static async Task<StoredTokens?> LoadAsync(string profile) {
    var path = ProfileTokenPath(profile);
    if (!File.Exists(path)) return null;
    var json = await File.ReadAllTextAsync(path);
    return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.StoredTokens); // throws on corrupt content
}
```

The legacy single-file branch of the parameterless `LoadAsync()` has the same
unguarded deserialize (`:92-94`). Any unparseable content — corrupt, empty,
partially written, or hand-edited — throws `JsonException`, which propagates
through `GetValidTokensAsync` and out of every caller as an **unhandled
exception**.

**Blast radius** — every one of these reaches a token load and therefore
crashes on a corrupt file, not just `status`:

| Caller | File |
|---|---|
| `StatusCommand.HandleAsync` | `src/Capacitor.Cli/Commands/StatusCommand.cs:36` |
| `HttpClientExtensions` (Bearer header for **every** authenticated request, incl. hook forwarding) | `src/Capacitor.Cli.Core/HttpClientExtensions.cs:47,55` |
| `WatchCommand` | `src/Capacitor.Cli/Commands/WatchCommand.cs:180` |
| `McpSessionsServer` | `src/Capacitor.Cli/Commands/McpSessionsServer.cs:156` |
| daemon `ServerConnection` | `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs:72,652` |
| daemon `AgentOrchestrator` | `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:847` |
| `Program.cs` (`whoami`) | `src/Capacitor.Cli/Program.cs:213` |
| `SetupCommand` | `src/Capacitor.Cli/Commands/SetupCommand.cs` (multiple) |

### 2. The on-disk corruption

The reader reports `',' is invalid after a single JSON value ... BytePositionInLine: 492`.
That means the file is **one complete JSON object, then a stray comma, then
leftover bytes**, e.g.:

```jsonc
{"access_token":"…","expires_at":"…","github_username":"…"}   ← complete, ends ~byte 491
,"provider":"workos","client_id":"…"}                          ← leftover tail = corruption
```

This is the classic signature of a **shorter document written over the first
part of a longer document**, where the longer document's tail survives past the
shorter one's closing `}`.

### 3. Corruption source (leading hypothesis)

`SaveAsync` writes to a **fixed, shared temp filename** then atomically renames:

[`src/Capacitor.Cli.Core/Auth/TokenStore.cs:62`](../../../src/Capacitor.Cli.Core/Auth/TokenStore.cs)
```csharp
var tempPath = $"{path}.tmp";                                    // shared across ALL processes for this profile
await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(tokens, …));
File.Move(tempPath, path, overwrite: true);                      // atomic rename publishes the temp
```

The atomic rename protects the *destination* from a single writer — a reader
always sees either the old or the new file, never a partial one. But the temp
filename is **the same for every process** that writes a given profile, and the
token store is explicitly shared across hooks, the watcher, the daemon, the MCP
servers, and `kcap login` (the file's own comments at `:136-139` acknowledge
this). When two `SaveAsync` calls run concurrently they open and write the
**same** `{path}.tmp`. `File.WriteAllText` opens with `FileMode.Create`, which
truncates at open but does **not** truncate the tail when a shorter write
follows a longer one — so a short token (e.g. a `gho_…` GitHub token) written
over a long token (a WorkOS JWT access token is commonly 600–1000+ bytes) leaves
the long token's tail after the short token's closing `}`. That renamed temp is
exactly the byte-492 corruption.

The cross-process refresh lock (`RefreshWithCrossProcessLockAsync`, `:155`)
serializes *refresh-driven* saves, but it does **not** cover:
- a `kcap login` / WorkOS-discovery save (no lock) racing a concurrent hook/daemon refresh, or
- the multi-save discovery login path (`OAuthLoginFlow`, `WorkOSDiscovery`).

So the race window is real on any machine running hooks/daemon concurrently with
an interactive login or token rotation.

**Certainty.** The crash mechanism (finding 1) is 100% — it is plainly visible
in the code and reproducible with a corrupt file. The corruption source (finding
3) is a strong, signature-consistent hypothesis; the customer's actual file
(`~/.config/kcap/tokens/<profile>.json`) would confirm it definitively, but the
fix does not depend on that confirmation.

## Design

Centralized changes in `TokenStore` — resilient read (Fix 1), corruption-free
write (Fix 2), and leaked-temp cleanup (Fix 3). All callers already route through
`LoadAsync`/`SaveAsync`/`Delete`, so no call sites change.

### Fix 1 — tolerate a corrupt token file on read (must-fix)

A corrupt, empty, partially-written, or hand-edited token file is semantically
equivalent to "no usable credentials" — the correct response is to behave as
**not authenticated** and let the existing UX guide the user to `kcap login`,
not to crash.

Introduce one private reader used by both load paths (DRY). It returns a
**tri-state** so the parameterless `LoadAsync()` can tell a *genuinely absent*
file (which may legitimately fall back to the legacy single-file layout) apart
from a *present-but-unusable* one (which must mean "not authenticated", never a
fallback):

```csharp
enum TokenFileState { Missing, Unusable, Loaded }

static async Task<(TokenFileState State, StoredTokens? Tokens)> ReadTokenFileAsync(string path) {
    string json;
    try {
        json = await File.ReadAllTextAsync(path);
    } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
        return (TokenFileState.Missing, null);            // absent — or deleted mid-read (logout race)
    }

    try {
        var tokens = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.StoredTokens);
        return tokens is null ? (TokenFileState.Unusable, null) : (TokenFileState.Loaded, tokens);
    } catch (JsonException) {
        return (TokenFileState.Unusable, null);           // corrupt / empty / hand-edited
    }
}
```

- `LoadAsync(string profile)` → `ReadTokenFileAsync(ProfileTokenPath(profile))`, returning the tokens (both `Missing` and `Unusable` → `null`).
- `LoadAsync()` (legacy-resolving) → read the active profile; fall back to `ReadTokenFileAsync(LegacyTokenPath)` **only when the active file is `Missing`**. A `Unusable` (corrupt) active file returns `null`, so corruption can never resurrect stale credentials from a legacy `tokens.json` whose best-effort deletion previously failed.

This single point of resilience covers the whole blast radius in finding 1.

**Why a tri-state instead of plain `null`.** The original sketch returned `null`
for both missing and corrupt. That conflation has a real (if narrow) failure
mode: a corrupt active-profile file would fall through to the legacy fallback and
silently load a stale token instead of reporting "not authenticated". Splitting
`Missing` from `Unusable` makes the fallback fire only for genuine pre-upgrade
installs. (Surfaced in code review.)

**Which exceptions are caught.** `JsonException` covers corrupt, empty/zero-byte
("input does not contain any JSON tokens"), and missing-required-property
content; a deserialize-to-`null` (the literal `null` document) is also treated as
`Unusable`. `FileNotFoundException`/`DirectoryNotFoundException` are treated as
`Missing` — this replaces the original `File.Exists` precheck, closing a TOCTOU
race where a concurrent logout deletes the file between the existence check and
the read (which would otherwise throw an unhandled `FileNotFoundException` — the
very failure class this design exists to eliminate). Every other read-time
`IOException`/`UnauthorizedAccessException` still **propagates**: a permission or
disk fault is a different class of problem and must not be silently reinterpreted
as "not authenticated", which would mask the fault and send users into a
confusing re-login loop.

### Fix 2 — unique temp filename on write (hardening, prevents recurrence)

Give every write its own temp file so concurrent writers can never share one;
the existing atomic rename already guarantees the destination is always a
complete document.

```csharp
public static async Task SaveAsync(string profile, StoredTokens tokens) {
    Directory.CreateDirectory(TokenDir);
    var path     = ProfileTokenPath(profile);
    var tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp"; // unique per write
    try {
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(tokens, CapacitorJsonContext.Default.StoredTokens));
        // Restrict the temp to owner-only BEFORE publishing, so even a temp leaked by a
        // crash between write and move isn't group/world-readable (TokenDir itself is
        // world-traversable). The rename preserves this mode onto the final file.
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(tempPath, path, overwrite: true);
    } finally {
        // A successful Move renames the temp away; only a failed write/move
        // leaves it behind. Unique names don't self-recycle like the old shared
        // name did, so clean up explicitly to avoid leaking *.tmp files.
        if (File.Exists(tempPath)) {
            try { File.Delete(tempPath); } catch { /* best-effort */ }
        }
    }

    if (!OperatingSystem.IsWindows()) {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // defense-in-depth on the published file
    }

    if (File.Exists(LegacyTokenPath)) {
        try { File.Delete(LegacyTokenPath); } catch { /* best-effort */ }
    }
}
```

Notes:
- `Environment.ProcessId` + `Guid.NewGuid()` are AOT-safe and need no new
  dependencies.
- The unique-name pattern never shares bytes between writers, so two concurrent
  saves each produce a complete temp and the rename publishes one-or-the-other
  atomically — last-writer-wins, never a spliced file.
- Cleanup moved into `finally` (with an `Exists` guard so the success path is a
  no-op) because, unlike the old fixed name, a leaked unique temp would never be
  reused.
- **Temp permissions (added in review).** The temp is chmod'd owner-only *before*
  the rename. Unique names persist when leaked (process killed between write and
  move), and `TokenDir` is world-traversable, so a default-umask temp would leave
  a readable secret on disk. Setting the mode on the temp (then relying on rename
  to preserve it, with the post-move chmod kept as defense-in-depth) closes that
  window for both the leaked temp and the final file.

### Fix 3 — sweep leaked temps on delete/logout (added in review)

A temp leaked by a crash between write and move carries token material. `Delete`
and `DeleteAsync` previously removed only `*.json`, so logout could leave a
secret-bearing `{profile}.json.{pid}.{guid}.tmp` behind. Both now also sweep
temps (best-effort), scoped by filename prefix rather than a glob so a profile
name containing a wildcard character cannot widen the match:

```csharp
static void SweepLeakedTemps(string? profile = null) {
    if (!Directory.Exists(TokenDir)) return;
    var prefix = profile is null ? null : $"{profile}.json.";
    try {
        foreach (var tmp in Directory.EnumerateFiles(TokenDir, "*.tmp")) {
            if (prefix is null || Path.GetFileName(tmp).StartsWith(prefix, StringComparison.Ordinal)) {
                try { File.Delete(tmp); } catch { /* best-effort */ }
            }
        }
    } catch { /* best-effort */ }
}
```

`Delete(profile)` calls `SweepLeakedTemps(profile)` (only that profile's temps);
`DeleteAsync` (logout) calls `SweepLeakedTemps()` (all). The per-profile `.lock`
files used by the refresh lock end in `.lock`, not `.tmp`, so they are untouched.

## Alternatives considered

- **Delete / quarantine the corrupt file inside `LoadAsync`.** Rejected: makes a
  read method mutate state (surprising), and races a concurrent writer that may
  be mid-rename. Returning `null` is sufficient — the next login/refresh
  overwrites the file cleanly. (Quarantine-to-`.corrupt` adds complexity for no
  user-visible benefit; the file's only payload is credentials we can't
  recover.)
- **Log a warning on corrupt read.** Rejected for the Core reader: hooks call
  this in tight loops, so a per-call warning would spam stderr while the file
  stays corrupt. The existing higher-level UX already prints actionable
  "run `kcap login`" guidance once per command.
- **Hold the cross-process lock around *every* save (incl. login).** Heavier and
  slower (login would contend on the lock), and doesn't address corruption from
  any future unlocked writer. The unique-temp-name fix removes the failure mode
  structurally regardless of who writes.
- **`FileShare.None` on the temp write.** Would make concurrent opens fail rather
  than corrupt, but turns a benign concurrent save into a thrown `IOException`
  that callers don't expect. Unique names let both saves succeed.

## Test plan

Unit tests in `test/Capacitor.Cli.Tests.Unit/TokenStoreProfileTests.cs`
(`KCAP_CONFIG_DIR` is redirected to a temp dir at assembly load; class-level
`[NotInParallel(nameof(TokenStoreProfileTests))]`; per-test cleanup of the tokens
dir, the legacy file, **and** the shared `config.json` so the active profile
deterministically resolves to `default`):

Read resilience (Fix 1):
1. **`LoadAsync_with_corrupt_json_returns_null`** — write the customer's exact
   signature (a complete object + `,` + trailing bytes) to a profile path; assert
   `LoadAsync(profile)` returns `null` and does **not** throw.
2. **`LoadAsync_with_empty_file_returns_null`** — zero-byte file → `null`.
3. **`LoadAsync_legacy_corrupt_file_returns_null`** — corrupt legacy
   `tokens.json`, no per-profile file → parameterless `LoadAsync()` returns
   `null`.
4. **`GetValidTokensAsync_with_corrupt_file_returns_null`** — the regression test
   that reproduces the customer crash through the public entry point.
5. **`LoadAsync_corrupt_active_profile_does_not_fall_back_to_legacy`** — corrupt
   active file + valid legacy file → `null` (corruption must not resurrect stale
   creds). Fix 1 / tri-state.
6. **`LoadAsync_missing_active_profile_still_falls_back_to_valid_legacy`** —
   genuine pre-upgrade install (no per-profile file, valid legacy) → returns the
   legacy token. Guards the fallback still works for `Missing`.

Write correctness (Fix 2 / Fix 3):
7. **`SaveAsync_concurrent_writes_never_corrupt`** — many parallel `SaveAsync`
   calls for one profile alternating long-token / short-token payloads; after the
   barrier, the on-disk file always parses and `LoadAsync` returns non-null
   (would intermittently fail under the old shared temp name).
8. **`SaveAsync_leaves_no_temp_residue`** — after a normal save, the tokens dir
   contains no `*.tmp`.
9. **`SaveAsync_cleans_up_temp_when_publish_fails`** — force `File.Move` to fail
   (destination is an existing directory); the `finally` still removes the temp
   and the exception propagates.
10. **`SaveAsync_sets_owner_only_file_mode_on_unix`** — saved file mode is
    `UserRead | UserWrite` (Unix only).
11. **`DeleteAsync_removes_leaked_temp_files`** — logout sweeps a stray
    `*.json.*.tmp`.
12. **`Delete_profile_removes_only_its_leaked_temp_files`** — per-profile delete
    sweeps that profile's temps and leaves another profile's temps intact.

## Scope & non-goals

- **In scope:** `TokenStore.LoadAsync` resilience (incl. the `Missing` vs
  `Unusable` distinction and FileNotFound/DirectoryNotFound TOCTOU handling),
  `TokenStore.SaveAsync` temp-file uniqueness and owner-only temp permissions,
  and leaked-temp sweeping on delete/logout. No call sites change; no public API
  changes.
- **Non-goals:** general read-time `IOException`/permission handling (those still
  propagate by design — only FileNotFound/DirectoryNotFound are treated as
  `Missing`); any server-side change.
  - *Originally a non-goal, now done:* the temp-file permission window — code
    review showed unique temps **persist** when leaked, making a world-readable
    secret a real exposure, so the temp is now chmod'd owner-only before publish.
- **AOT:** no new reflection or dynamic codegen; run the
  `dotnet publish -c Release` IL2026/IL3050 grep per `CLAUDE.md` after the change.
- **README:** no user-facing CLI surface changes (internal robustness only), so
  no `README.md` update is required for this PR. A one-line `CHANGELOG`/release
  note ("`kcap` no longer crashes on a corrupt local token file") is nice-to-have.
