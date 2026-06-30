# TokenStore corruption resilience — Design

**Date:** 2026-06-30
**Issue:** [AI-1082](https://linear.app/kurrent/issue/AI-1082/kcap-crashes-on-corrupt-token-file-harden-tokenstore-load-save)
**Status:** Approved; ready for plan execution.
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

Two independent, centralized changes in `TokenStore`. Both callers already route
through `LoadAsync`/`SaveAsync`, so no call sites change.

### Fix 1 — tolerate a corrupt token file on read (must-fix)

A corrupt, empty, partially-written, or hand-edited token file is semantically
equivalent to "no usable credentials" — the correct response is to behave as
**not authenticated** and let the existing UX guide the user to `kcap login`,
not to crash.

Introduce one private reader used by both load paths (DRY):

```csharp
static async Task<StoredTokens?> ReadTokensAsync(string path) {
    if (!File.Exists(path)) return null;
    var json = await File.ReadAllTextAsync(path);
    try {
        return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.StoredTokens);
    } catch (JsonException) {
        // Corrupt / partially-written / hand-edited token file. Treat as
        // unauthenticated so the CLI degrades gracefully (callers print
        // "run kcap login") instead of crashing every command, hook, the
        // daemon, and MCP. The next successful login/refresh overwrites it.
        return null;
    }
}
```

- `LoadAsync(string profile)` → `return await ReadTokensAsync(ProfileTokenPath(profile));`
- `LoadAsync()` legacy branch → `return await ReadTokensAsync(LegacyTokenPath);`

This single point of resilience covers the whole blast radius in finding 1.

**Why catch `JsonException` only (not `IOException`).** Empty/zero-byte files
also surface as `JsonException` ("input does not contain any JSON tokens"), so
they are covered. A read-time `IOException`/`UnauthorizedAccessException`
indicates a different class of problem (permissions, disk) that should *not* be
silently reinterpreted as "not authenticated" — that would mask a real fault and
send users into a confusing re-login loop. Keeping the catch narrow keeps the
fix targeted to the reported failure.

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
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

Unit tests in `test/Capacitor.Cli.Tests.Unit/` (mirror `TokenStoreProfileTests`:
`KCAP_CONFIG_DIR` is redirected to a temp dir at assembly load;
`[NotInParallel]` + per-test cleanup of the tokens dir):

1. **`LoadAsync_with_corrupt_json_returns_null`** — write the customer's exact
   signature (a complete object + `,` + trailing bytes) to a profile path; assert
   `LoadAsync(profile)` returns `null` and does **not** throw.
2. **`LoadAsync_with_empty_file_returns_null`** — zero-byte file → `null`.
3. **`LoadAsync_legacy_corrupt_file_returns_null`** — corrupt legacy
   `tokens.json`, no per-profile file → parameterless `LoadAsync()` returns
   `null`.
4. **`GetValidTokensAsync_with_corrupt_file_returns_null`** — the regression test
   that reproduces the customer crash through the public entry point.
5. **`SaveAsync_concurrent_writes_never_corrupt`** — many parallel `SaveAsync`
   calls for one profile alternating long-token / short-token payloads; after the
   barrier, the on-disk file always parses and `LoadAsync` returns non-null
   (would intermittently fail under the old shared temp name).
6. **`SaveAsync_leaves_no_temp_residue`** — after a normal save, the tokens dir
   contains only `<profile>.json` (no `*.tmp`).

## Scope & non-goals

- **In scope:** `TokenStore.LoadAsync` resilience and `TokenStore.SaveAsync`
  temp-file uniqueness. No call sites change; no public API changes.
- **Non-goals:** read-time `IOException`/permission handling; tightening the
  brief umask window on the temp file before `SetUnixFileMode` (pre-existing,
  unchanged); any server-side change.
- **AOT:** no new reflection or dynamic codegen; run the
  `dotnet publish -c Release` IL2026/IL3050 grep per `CLAUDE.md` after the change.
- **README:** no user-facing CLI surface changes (internal robustness only), so
  no `README.md` update is required for this PR. A one-line `CHANGELOG`/release
  note ("`kcap` no longer crashes on a corrupt local token file") is nice-to-have.
