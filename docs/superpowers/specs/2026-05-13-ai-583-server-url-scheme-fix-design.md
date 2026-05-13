# AI-583: Fix scheme-less `server_url` crash

## Problem

A `server_url` stored without a scheme (`"staging.kapacitor.ai"` instead of
`"https://staging.kapacitor.ai"`) crashes every hook invocation with an
unhandled `InvalidOperationException` from `HttpClient.PrepareRequestMessage`.
Because hook crashes surface in Claude Code as a `Stop hook error` banner,
the failure is visible to the user on every turn.

Root cause: `AppConfig.NormalizeUrl` only trims trailing slashes — it does
not add a scheme. `ProfileResolver` calls it on every resolve path, so a
scheme-less value flows verbatim into `client.PostAsync($"{baseUrl}/...")`,
where `HttpClient` rejects it as a relative URI.

The user who hit this used a v1-shaped config (bare host, no scheme).
This format is no longer written by setup, but legacy and hand-edited
configs still break.

## Goal

1. Prevent scheme-less values from being saved in the first place
   (auto-correct on save, with probe + loopback fallback).
2. If a bad value still lands in `HttpClient` (legacy config, manual edit),
   surface a clean, actionable error — not a stack trace.
3. No silent migration of on-disk configs.

## Architecture overview

```
[save path]                                [use path]
config set       ┐                          ResolvedServerUrl
profile add      ├─► ServerUrlNormalizer    │
setup            ┘   (probe + loopback)     ▼
                       │                  *WithRetryAsync
                       ▼                    │
                     disk                   ├─ EnsureAbsolute → fail fast, exit 2
                                            └─ absolute → HttpClient
```

`AppConfig.NormalizeUrl` is left unchanged (trim trailing slashes). The
resolve path keeps its existing behavior — whatever is on disk flows
through. The use-site guard catches anything still broken.

## `ServerUrlNormalizer`

New class at `src/Kapacitor.Core/Config/ServerUrlNormalizer.cs`:

```csharp
public static class ServerUrlNormalizer {
    public record Result(string Url, string? Warning);

    public static string WithLoopbackDefault(string input);

    public static Task<Result> NormalizeAsync(
        string input, bool skipProbe, CancellationToken ct);
}
```

### `WithLoopbackDefault` (pure)

- If `input` already has `http://` or `https://`: return `input.TrimEnd('/')`.
- Otherwise, parse the host portion (text before any `:port` or `/`):
  - If host is `localhost`, `127.0.0.1`, `::1`, or `host.docker.internal`:
    prepend `http://`.
  - Else: prepend `https://`.
- Trim trailing slash.

No network, no exceptions for normal input. Used as the fallback when
probing fails or is skipped.

### `NormalizeAsync` (probes `/auth/config`)

1. If `skipProbe`: return `WithLoopbackDefault(input)` with no warning.
2. If `input` has a scheme:
   - Probe that URL.
   - On success: return `Result(input.TrimEnd('/'), null)`.
   - On failure: return `Result(input.TrimEnd('/'), "could not reach <url>")`.
3. If `input` has no scheme:
   - Probe `https://<input>` (3 s timeout).
     - On success: return `Result("https://<input>" trimmed, null)`.
   - Else probe `http://<input>` (3 s timeout).
     - On success: return `Result("http://<input>" trimmed, null)`.
   - Else: fall back to `WithLoopbackDefault(input)` with warning
     `"could not reach <input> on https or http; saved as <chosen>. " +
      "Verify with 'kapacitor config show'."`.

The probe is a `GET /auth/config` with a 3 s timeout. It reuses the
anonymous discovery endpoint that `HttpClientExtensions.DiscoverProviderAsync`
already calls. Extract a small `TryProbeAsync(url, timeout, ct)` helper
that does not cache (the existing `DiscoverProviderAsync` caches the
provider; we don't want to poison that cache from save-time probes).

## Save-path integration

### `ConfigCommand.Set`

Pre-process the value for the `server_url` key before calling `ApplySet`:

```csharp
static async Task<int> Set(string key, string value, bool skipProbe) {
    if (key == "server_url") {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            value, skipProbe, CancellationToken.None);
        if (result.Warning is not null)
            await Console.Error.WriteLineAsync($"Warning: {result.Warning}");
        value = result.Url;
    }
    // ... rest unchanged: ApplySet stays pure, just stores `value`.
}
```

`HandleAsync` parses `--no-probe` with a simple `args.Contains` check
(matches the existing `--no-prompt` style in `SetupCommand`).

`ApplySet` is unchanged — it stays a pure function that stores the value
verbatim, so its existing unit tests need no update.

### `ProfileCommand.Add`

Replace the current `AppConfig.NormalizeUrl(serverUrl)` call with
`ServerUrlNormalizer.NormalizeAsync(serverUrl, skipProbe, ct)`. Same
`--no-probe` flag, same warning behavior.

### `SetupCommand`

Today, the `--server-url` branch normalizes with `AppConfig.NormalizeUrl`
and then calls `DiscoverProviderAsync` to verify reachability, erroring
out on failure. Refactor:

1. Call `ServerUrlNormalizer.NormalizeAsync` first to resolve the scheme.
2. Keep the strict reachability check — setup's next step is login, which
   requires a reachable server. If the normalizer returns a warning,
   setup treats the server as unreachable and errors out (unchanged
   user-facing behavior).

`--no-probe` does not apply to `setup`. `setup --no-prompt` already
requires `--server-url`; documenting that scripted setups should pass
a scheme-qualified URL is sufficient.

## Use-site guard in `HttpClientExtensions`

Add an `EnsureAbsolute` check to each `*WithRetryAsync` extension method.
Up-front check (preventive), not exception-based (reactive):

```csharp
public Task<HttpResponseMessage> PostWithRetryAsync(string url, HttpContent content, ...) {
    EnsureAbsolute(url);
    return SendWithRetryAsync(() => client.PostAsync(url, content, ct), ...);
}

internal static void EnsureAbsolute(string url) {
    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "http" || uri.Scheme == "https")) {
        return;
    }
    Console.Error.WriteLine(
        "server_url is missing a scheme. Run: " +
        "kapacitor config set server_url https://<host>");
    Environment.Exit(2);
}
```

- `Uri.TryCreate` avoids constructor exceptions for malformed input.
- Restrict to `http`/`https` to reject pathological cases like
  `file:///etc/passwd`.
- `Environment.Exit(2)` matches the existing hook-fatal-error pattern;
  prevents the unhandled exception from reaching Claude Code's hook
  crash banner.

For unit testing, factor `EnsureAbsolute` to return a `bool` (or expose
a `TryEnsureAbsolute` variant) so tests can assert behavior without
calling `Environment.Exit`. The single-line shim that does the exit
stays at the call site.

## What we are not doing

- **No load-time migration.** Configs on disk are not silently rewritten.
  If a legacy v1 config triggers the use-site guard, the user sees the
  remediation hint and runs `kapacitor config set server_url <url>` (or
  deletes the config and re-runs setup). README documents this.
- **No change to `AppConfig.NormalizeUrl` or `ProfileResolver`.** Resolve
  path stays scheme-blind. All scheme handling is concentrated on the
  save path and the use-site guard.

## README updates

Add a short upgrade section to both `README.md` and `npm/kapacitor/README.md`:

> ### Upgrading from v1
>
> The v1 config format used a bare host name without a scheme. If
> `kapacitor` crashes with `An invalid request URI was provided` after
> upgrading, your config still has the old format. Fix it with one
> command:
>
>     kapacitor config set server_url https://your-server.example.com
>
> Or remove the config file and re-run setup:
>
>     rm ~/.config/kapacitor/config.json
>     kapacitor setup

## Testing

### Unit (`test/kapacitor.Tests.Unit`)

`ServerUrlNormalizerTests` — pure `WithLoopbackDefault`:

- `"localhost"` → `"http://localhost"`
- `"localhost:5108"` → `"http://localhost:5108"`
- `"127.0.0.1"` → `"http://127.0.0.1"`
- `"::1"` → `"http://::1"`
- `"host.docker.internal"` → `"http://host.docker.internal"`
- `"staging.kapacitor.ai"` → `"https://staging.kapacitor.ai"`
- `"staging.kapacitor.ai:8443"` → `"https://staging.kapacitor.ai:8443"`
- `"https://staging.kapacitor.ai/"` → `"https://staging.kapacitor.ai"`
- `"http://localhost:5108"` → unchanged

`HttpClientExtensionsAbsoluteUrlTests` — the testable form of
`EnsureAbsolute` (returning `bool`):

- `"staging.kapacitor.ai/hooks/stop"` → false
- `"https://staging.kapacitor.ai/hooks/stop"` → true
- `"http://localhost:5108/hooks/stop"` → true
- `"file:///etc/passwd"` → false
- `""` → false

`ConfigCommandApplySetTests` — already exist; unchanged (`ApplySet`
stays pure).

### Integration (`test/kapacitor.Tests.Integration`, WireMock)

`ServerUrlNormalizerProbeTests`:

- WireMock serves `/auth/config` 200 on https port → input
  `"server.local:<port>"` → result `"https://server.local:<port>"`, no
  warning.
- WireMock serves only http → result `"http://server.local:<port>"`,
  no warning.
- Neither responds (use a reserved/closed port) → result follows
  loopback rule, warning emitted.
- `skipProbe: true` → no HTTP calls (assert via WireMock request log),
  result follows loopback rule, no warning.

### Verification commands

- `dotnet build src/kapacitor/kapacitor.csproj`
- `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — must be empty
- `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
- `dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj`

## Files changed

**New:**

- `src/Kapacitor.Core/Config/ServerUrlNormalizer.cs`
- `test/kapacitor.Tests.Unit/Config/ServerUrlNormalizerTests.cs`
- `test/kapacitor.Tests.Unit/Http/HttpClientExtensionsAbsoluteUrlTests.cs`
- `test/kapacitor.Tests.Integration/Config/ServerUrlNormalizerProbeTests.cs`

**Modified:**

- `src/Kapacitor.Core/HttpClientExtensions.cs` — `EnsureAbsolute` on all
  `*WithRetryAsync` extensions; small `TryProbeAsync` helper for the
  normalizer.
- `src/kapacitor/Commands/ConfigCommand.cs` — async normalize on
  `server_url`, `--no-probe` flag.
- `src/kapacitor/Commands/ProfileCommand.cs` — same on `profile add`.
- `src/kapacitor/Commands/SetupCommand.cs` — share normalizer; keep
  strict reachability check.
- `README.md` — v1 upgrade note.
- `npm/kapacitor/README.md` — same note.
