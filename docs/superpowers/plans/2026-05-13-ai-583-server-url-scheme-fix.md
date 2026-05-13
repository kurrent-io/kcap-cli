# AI-583 Server URL Scheme Fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop a scheme-less `server_url` in config from crashing every hook with `InvalidOperationException`. Auto-correct on save (with HTTP probe + loopback fallback), and fail fast with a clear error at use sites if a bad value still slips through.

**Architecture:** A new `ServerUrlNormalizer` (in `Kapacitor.Core/Config`) owns scheme resolution on the save path. All three save-path commands (`config set server_url`, `profile add --server-url`, `setup --server-url`) call it. The probe is an injectable `Func` delegate so orchestration can be unit-tested deterministically; the real implementation issues a `GET /auth/config` via `HttpClient`. A separate `EnsureAbsolute` guard in `HttpClientExtensions` catches relative URIs at use time and exits cleanly with an actionable message. Resolve path and `AppConfig.NormalizeUrl` are unchanged.

**Tech Stack:** .NET 10, NativeAOT, TUnit, WireMock.Net, NSubstitute. Source-generated JSON. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-05-13-ai-583-server-url-scheme-fix-design.md`

---

## File map

**New:**
- `src/Kapacitor.Core/Config/ServerUrlNormalizer.cs` — pure helper + async orchestrator + default HTTP probe
- `test/kapacitor.Tests.Unit/Config/ServerUrlNormalizerTests.cs` — unit tests (pure helper + orchestration via fake probe)
- `test/kapacitor.Tests.Unit/Http/HttpClientExtensionsAbsoluteUrlTests.cs` — unit tests for `EnsureAbsolute`
- `test/kapacitor.Tests.Integration/Config/ServerUrlProbeIntegrationTests.cs` — end-to-end WireMock probe test

**Modified:**
- `src/Kapacitor.Core/HttpClientExtensions.cs` — `EnsureAbsolute` guard wired into each `*WithRetryAsync` extension
- `src/kapacitor/Commands/ConfigCommand.cs` — async normalize on `server_url`, `--no-probe` flag
- `src/kapacitor/Commands/ProfileCommand.cs` — same on `profile add`
- `src/kapacitor/Commands/SetupCommand.cs` — share normalizer logic, keep strict reachability check
- `README.md` and `npm/kapacitor/README.md` — v1 upgrade note

---

## Task 1: `WithLoopbackDefault` pure helper

**Files:**
- Create: `src/Kapacitor.Core/Config/ServerUrlNormalizer.cs`
- Create: `test/kapacitor.Tests.Unit/Config/ServerUrlNormalizerTests.cs`

- [ ] **Step 1.1: Write the failing tests**

Create `test/kapacitor.Tests.Unit/Config/ServerUrlNormalizerTests.cs`:

```csharp
using kapacitor.Config;

namespace kapacitor.Tests.Unit.Config;

public class ServerUrlNormalizerLoopbackTests {
    [Test]
    [Arguments("localhost", "http://localhost")]
    [Arguments("localhost:5108", "http://localhost:5108")]
    [Arguments("127.0.0.1", "http://127.0.0.1")]
    [Arguments("127.0.0.1:8080", "http://127.0.0.1:8080")]
    [Arguments("::1", "http://::1")]
    [Arguments("host.docker.internal", "http://host.docker.internal")]
    [Arguments("host.docker.internal:5108", "http://host.docker.internal:5108")]
    public async Task Loopback_HostsGetHttp(string input, string expected) {
        var result = ServerUrlNormalizer.WithLoopbackDefault(input);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments("staging.kapacitor.ai", "https://staging.kapacitor.ai")]
    [Arguments("staging.kapacitor.ai:8443", "https://staging.kapacitor.ai:8443")]
    [Arguments("example.com/api", "https://example.com/api")]
    public async Task NonLoopback_HostsGetHttps(string input, string expected) {
        var result = ServerUrlNormalizer.WithLoopbackDefault(input);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments("https://staging.kapacitor.ai/", "https://staging.kapacitor.ai")]
    [Arguments("http://localhost:5108", "http://localhost:5108")]
    [Arguments("https://example.com", "https://example.com")]
    public async Task ExistingScheme_IsPreservedAndTrimmed(string input, string expected) {
        var result = ServerUrlNormalizer.WithLoopbackDefault(input);
        await Assert.That(result).IsEqualTo(expected);
    }
}
```

- [ ] **Step 1.2: Run tests to verify they fail**

Run: `dotnet build test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: compilation FAILS — `ServerUrlNormalizer` does not exist.

- [ ] **Step 1.3: Create the file with the pure helper**

Create `src/Kapacitor.Core/Config/ServerUrlNormalizer.cs`:

```csharp
namespace kapacitor.Config;

/// <summary>
/// Resolves the scheme for user-supplied server URLs. Used on save paths
/// (config set / profile add / setup) so v1-style scheme-less values do
/// not silently land on disk.
/// </summary>
public static class ServerUrlNormalizer {
    public record Result(string Url, string? Warning);

    static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1", "host.docker.internal"];

    /// <summary>
    /// Returns the input with an inferred scheme: <c>http://</c> for well-known
    /// loopback hosts, <c>https://</c> otherwise. Trims trailing slashes. Pure;
    /// does no I/O. Used as a fallback when probing fails or is skipped.
    /// </summary>
    public static string WithLoopbackDefault(string input) {
        var trimmed = input.TrimEnd('/');

        if (HasScheme(trimmed)) return trimmed;

        var host = ExtractHost(trimmed);
        var scheme = LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ? "http" : "https";
        return $"{scheme}://{trimmed}";
    }

    static bool HasScheme(string input) =>
        input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    static string ExtractHost(string input) {
        // Strip path
        var pathStart = input.IndexOf('/');
        var hostAndPort = pathStart >= 0 ? input[..pathStart] : input;

        // Strip port — but only the last ":N" if N is digits (so "::1" survives).
        var lastColon = hostAndPort.LastIndexOf(':');
        if (lastColon > 0 && hostAndPort[(lastColon + 1)..].All(char.IsDigit))
            return hostAndPort[..lastColon];

        return hostAndPort;
    }
}
```

- [ ] **Step 1.4: Run tests to verify they pass**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ServerUrlNormalizerLoopbackTests/*"`
Expected: all 13 test cases PASS.

- [ ] **Step 1.5: Commit**

```bash
git add src/Kapacitor.Core/Config/ServerUrlNormalizer.cs test/kapacitor.Tests.Unit/Config/ServerUrlNormalizerTests.cs
git commit -m "[AI-583] add ServerUrlNormalizer.WithLoopbackDefault pure helper

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `NormalizeAsync` orchestration with injectable probe

**Files:**
- Modify: `src/Kapacitor.Core/Config/ServerUrlNormalizer.cs`
- Modify: `test/kapacitor.Tests.Unit/Config/ServerUrlNormalizerTests.cs`

The probe is an injectable `Func<string, TimeSpan, CancellationToken, Task<bool>>` so we can unit-test orchestration without HTTP. Default value (real HTTP probe) is added in Task 3; for now the test passes a fake.

- [ ] **Step 2.1: Append failing orchestration tests**

Append to `test/kapacitor.Tests.Unit/Config/ServerUrlNormalizerTests.cs`:

```csharp
public class ServerUrlNormalizerOrchestrationTests {
    static Func<string, TimeSpan, CancellationToken, Task<bool>> Probe(Func<string, bool> reachable) =>
        (url, _, _) => Task.FromResult(reachable(url));

    [Test]
    public async Task SkipProbe_ReturnsLoopbackDefault_NoWarning_NoProbeCalls() {
        var probeCalls = 0;
        var probe = Probe(u => { probeCalls++; return true; });

        var result = await ServerUrlNormalizer.NormalizeAsync(
            "staging.kapacitor.ai", skipProbe: true, CancellationToken.None, probe);

        await Assert.That(result.Url).IsEqualTo("https://staging.kapacitor.ai");
        await Assert.That(result.Warning).IsNull();
        await Assert.That(probeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SchemePresent_ProbeSucceeds_NoWarning() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "https://staging.kapacitor.ai/", skipProbe: false, CancellationToken.None,
            Probe(_ => true));

        await Assert.That(result.Url).IsEqualTo("https://staging.kapacitor.ai");
        await Assert.That(result.Warning).IsNull();
    }

    [Test]
    public async Task SchemePresent_ProbeFails_Warns() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "https://staging.kapacitor.ai", skipProbe: false, CancellationToken.None,
            Probe(_ => false));

        await Assert.That(result.Url).IsEqualTo("https://staging.kapacitor.ai");
        await Assert.That(result.Warning).IsNotNull();
        await Assert.That(result.Warning!).Contains("could not reach");
    }

    [Test]
    public async Task SchemeMissing_HttpsSucceeds_UsesHttps() {
        var probedUrls = new List<string>();
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "staging.kapacitor.ai", skipProbe: false, CancellationToken.None,
            (u, _, _) => { probedUrls.Add(u); return Task.FromResult(u.StartsWith("https://")); });

        await Assert.That(result.Url).IsEqualTo("https://staging.kapacitor.ai");
        await Assert.That(result.Warning).IsNull();
        await Assert.That(probedUrls[0]).IsEqualTo("https://staging.kapacitor.ai");
    }

    [Test]
    public async Task SchemeMissing_HttpsFails_HttpSucceeds_UsesHttp() {
        var probedUrls = new List<string>();
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "localhost:5108", skipProbe: false, CancellationToken.None,
            (u, _, _) => { probedUrls.Add(u); return Task.FromResult(u.StartsWith("http://")); });

        await Assert.That(result.Url).IsEqualTo("http://localhost:5108");
        await Assert.That(result.Warning).IsNull();
        await Assert.That(probedUrls.Count).IsEqualTo(2);
        await Assert.That(probedUrls[0]).StartsWith("https://");
        await Assert.That(probedUrls[1]).StartsWith("http://");
    }

    [Test]
    public async Task SchemeMissing_BothFail_FallsBackToLoopbackDefault_Warns() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "staging.kapacitor.ai", skipProbe: false, CancellationToken.None,
            Probe(_ => false));

        await Assert.That(result.Url).IsEqualTo("https://staging.kapacitor.ai");
        await Assert.That(result.Warning).IsNotNull();
        await Assert.That(result.Warning!).Contains("could not reach");
    }

    [Test]
    public async Task SchemeMissing_BothFail_Loopback_UsesHttpFallback() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "localhost:5108", skipProbe: false, CancellationToken.None,
            Probe(_ => false));

        await Assert.That(result.Url).IsEqualTo("http://localhost:5108");
        await Assert.That(result.Warning).IsNotNull();
    }
}
```

- [ ] **Step 2.2: Run to verify compilation fails**

Run: `dotnet build test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: compilation FAILS — `NormalizeAsync` does not exist.

- [ ] **Step 2.3: Add `NormalizeAsync` to ServerUrlNormalizer**

Append to `src/Kapacitor.Core/Config/ServerUrlNormalizer.cs` (inside the `ServerUrlNormalizer` class):

```csharp
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Resolves a server URL the user just supplied: applies the loopback default
    /// for scheme-less input and probes the result (or both schemes) via
    /// <c>GET /auth/config</c>. Never throws — on probe failure, returns the
    /// loopback-default URL with a warning so the caller can decide what to do.
    /// </summary>
    public static async Task<Result> NormalizeAsync(
        string                                                  input,
        bool                                                    skipProbe,
        CancellationToken                                       ct,
        Func<string, TimeSpan, CancellationToken, Task<bool>>?  probe = null) {

        probe ??= HttpProbeAsync;
        var trimmed = input.TrimEnd('/');

        if (skipProbe) return new(WithLoopbackDefault(trimmed), null);

        if (HasScheme(trimmed)) {
            return await probe(trimmed, ProbeTimeout, ct)
                ? new(trimmed, null)
                : new(trimmed, $"could not reach {trimmed}. Saved anyway. Verify with 'kapacitor config show'.");
        }

        var httpsCandidate = $"https://{trimmed}";
        if (await probe(httpsCandidate, ProbeTimeout, ct))
            return new(httpsCandidate, null);

        var httpCandidate = $"http://{trimmed}";
        if (await probe(httpCandidate, ProbeTimeout, ct))
            return new(httpCandidate, null);

        var fallback = WithLoopbackDefault(trimmed);
        return new(fallback, $"could not reach {trimmed} on https or http. Saved as {fallback}. Verify with 'kapacitor config show'.");
    }

    // Placeholder — real implementation added in Task 3.
    static Task<bool> HttpProbeAsync(string url, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(false);
```

- [ ] **Step 2.4: Run to verify tests pass**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ServerUrlNormalizerOrchestrationTests/*"`
Expected: all 7 tests PASS.

- [ ] **Step 2.5: Commit**

```bash
git add src/Kapacitor.Core/Config/ServerUrlNormalizer.cs test/kapacitor.Tests.Unit/Config/ServerUrlNormalizerTests.cs
git commit -m "[AI-583] add ServerUrlNormalizer.NormalizeAsync orchestration

Probe is an injectable delegate so orchestration is unit-tested without HTTP.
Default HTTP probe is wired in the next commit.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Real HTTP probe + integration test

**Files:**
- Modify: `src/Kapacitor.Core/Config/ServerUrlNormalizer.cs` — replace placeholder probe
- Create: `test/kapacitor.Tests.Integration/Config/ServerUrlProbeIntegrationTests.cs`

- [ ] **Step 3.1: Write the failing integration test**

Create `test/kapacitor.Tests.Integration/Config/ServerUrlProbeIntegrationTests.cs`:

```csharp
using kapacitor.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Integration.Config;

/// <summary>
/// End-to-end verification that the default HTTP probe issued by
/// <see cref="ServerUrlNormalizer.NormalizeAsync"/> reaches a real HTTP server
/// (here, WireMock) and resolves the scheme correctly.
/// </summary>
public class ServerUrlProbeIntegrationTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task SchemeMissing_HttpServerResponds_PicksHttp() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var port  = new Uri(_server.Url!).Port;
        var input = $"localhost:{port}";

        var result = await ServerUrlNormalizer.NormalizeAsync(
            input, skipProbe: false, CancellationToken.None);

        await Assert.That(result.Url).IsEqualTo($"http://localhost:{port}");
        await Assert.That(result.Warning).IsNull();
    }

    [Test]
    public async Task SchemePresent_ProbeHitsAuthConfig() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var result = await ServerUrlNormalizer.NormalizeAsync(
            _server.Url!, skipProbe: false, CancellationToken.None);

        await Assert.That(result.Url).IsEqualTo(_server.Url!.TrimEnd('/'));
        await Assert.That(result.Warning).IsNull();

        var calls = _server.FindLogEntries(Request.Create().WithPath("/auth/config").UsingGet());
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task UnreachableHost_FallsBackToLoopbackDefault_Warns() {
        // 127.0.0.1:1 is reserved (tcpmux) and reliably unbound on dev machines.
        var input = "127.0.0.1:1";

        var result = await ServerUrlNormalizer.NormalizeAsync(
            input, skipProbe: false, CancellationToken.None);

        await Assert.That(result.Url).IsEqualTo("http://127.0.0.1:1");
        await Assert.That(result.Warning).IsNotNull();
    }
}
```

- [ ] **Step 3.2: Verify the test fails as expected**

Run: `dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj --treenode-filter "/*/*/ServerUrlProbeIntegrationTests/*"`
Expected: tests FAIL — the placeholder `HttpProbeAsync` returns `false`, so even reachable URLs get the warning path.

- [ ] **Step 3.3: Replace the placeholder probe with the real implementation**

In `src/Kapacitor.Core/Config/ServerUrlNormalizer.cs`, replace the placeholder `HttpProbeAsync` method with:

```csharp
    static async Task<bool> HttpProbeAsync(string url, TimeSpan timeout, CancellationToken ct) {
        using var http = new HttpClient { Timeout = timeout };

        try {
            using var resp = await http.GetAsync($"{url}/auth/config", ct);
            // Any HTTP response means the server is reachable. We do not require
            // 200 — older servers without /auth/config still count as "up".
            return true;
        } catch {
            return false;
        }
    }
```

- [ ] **Step 3.4: Run integration tests**

Run: `dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj --treenode-filter "/*/*/ServerUrlProbeIntegrationTests/*"`
Expected: all 3 tests PASS.

- [ ] **Step 3.5: Run all unit tests to confirm nothing regressed**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: all tests PASS.

- [ ] **Step 3.6: Commit**

```bash
git add src/Kapacitor.Core/Config/ServerUrlNormalizer.cs test/kapacitor.Tests.Integration/Config/ServerUrlProbeIntegrationTests.cs
git commit -m "[AI-583] wire real HTTP probe in ServerUrlNormalizer

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: `EnsureAbsolute` use-site guard

**Files:**
- Modify: `src/Kapacitor.Core/HttpClientExtensions.cs`
- Create: `test/kapacitor.Tests.Unit/Http/HttpClientExtensionsAbsoluteUrlTests.cs`

`EnsureAbsolute` factors into two pieces: a pure `IsAcceptable(url)` for tests, and a `EnsureAbsolute(url)` wrapper that calls `Environment.Exit(2)` on rejection.

- [ ] **Step 4.1: Write the failing tests**

Create `test/kapacitor.Tests.Unit/Http/HttpClientExtensionsAbsoluteUrlTests.cs`:

```csharp
namespace kapacitor.Tests.Unit.Http;

public class HttpClientExtensionsAbsoluteUrlTests {
    [Test]
    [Arguments("https://staging.kapacitor.ai/hooks/stop")]
    [Arguments("http://localhost:5108/hooks/stop")]
    [Arguments("http://127.0.0.1:5108")]
    public async Task Accepts_AbsoluteHttpAndHttps(string url) {
        await Assert.That(kapacitor.HttpClientExtensions.IsAcceptableUrl(url)).IsTrue();
    }

    [Test]
    [Arguments("staging.kapacitor.ai/hooks/stop")]
    [Arguments("/hooks/stop")]
    [Arguments("")]
    [Arguments("not a url at all")]
    public async Task Rejects_RelativeOrMalformed(string url) {
        await Assert.That(kapacitor.HttpClientExtensions.IsAcceptableUrl(url)).IsFalse();
    }

    [Test]
    [Arguments("file:///etc/passwd")]
    [Arguments("ftp://example.com")]
    [Arguments("javascript:alert(1)")]
    public async Task Rejects_NonHttpSchemes(string url) {
        await Assert.That(kapacitor.HttpClientExtensions.IsAcceptableUrl(url)).IsFalse();
    }
}
```

- [ ] **Step 4.2: Run to verify compilation fails**

Run: `dotnet build test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: compilation FAILS — `IsAcceptableUrl` does not exist.

- [ ] **Step 4.3: Add `IsAcceptableUrl` and `EnsureAbsolute` to HttpClientExtensions**

In `src/Kapacitor.Core/HttpClientExtensions.cs`, add the following two methods (just before the `extension(HttpClient client)` block):

```csharp
    internal const string SchemeMissingHint =
        "server_url is missing a scheme. Run: kapacitor config set server_url https://<host>";

    /// <summary>
    /// Pure test seam for <see cref="EnsureAbsolute"/>. Returns <c>true</c> only
    /// for absolute http/https URLs.
    /// </summary>
    public static bool IsAcceptableUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "http" || uri.Scheme == "https");

    /// <summary>
    /// Fails fast with an actionable message if <paramref name="url"/> is not
    /// an absolute http/https URL. Called by every <c>*WithRetryAsync</c>
    /// extension so a legacy scheme-less config produces a clean exit instead
    /// of an unhandled <see cref="InvalidOperationException"/> from
    /// <see cref="HttpClient.PrepareRequestMessage"/>.
    /// </summary>
    static void EnsureAbsolute(string url) {
        if (IsAcceptableUrl(url)) return;
        Console.Error.WriteLine(SchemeMissingHint);
        Environment.Exit(2);
    }
```

Then modify each `*WithRetryAsync` extension to call `EnsureAbsolute(url)` first. Replace the existing `extension(HttpClient client)` block in the same file with:

```csharp
    extension(HttpClient client) {
        public Task<HttpResponseMessage> PostWithRetryAsync(
                string            url,
                HttpContent       content,
                TimeSpan?         timeout = null,
                CancellationToken ct      = default
            ) {
            EnsureAbsolute(url);
            return SendWithRetryAsync(() => client.PostAsync(url, content, ct), timeout ?? DefaultTimeout, ct);
        }

        public Task<HttpResponseMessage> GetWithRetryAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default) {
            EnsureAbsolute(url);
            return SendWithRetryAsync(() => client.GetAsync(url, ct), timeout ?? DefaultTimeout, ct);
        }

        public Task<HttpResponseMessage> PutWithRetryAsync(
                string            url,
                HttpContent       content,
                TimeSpan?         timeout = null,
                CancellationToken ct      = default
            ) {
            EnsureAbsolute(url);
            return SendWithRetryAsync(() => client.PutAsync(url, content, ct), timeout ?? DefaultTimeout, ct);
        }

        public Task<HttpResponseMessage> DeleteWithRetryAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default) {
            EnsureAbsolute(url);
            return SendWithRetryAsync(() => client.DeleteAsync(url, ct), timeout ?? DefaultTimeout, ct);
        }
    }
```

- [ ] **Step 4.4: Run unit tests**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/HttpClientExtensionsAbsoluteUrlTests/*"`
Expected: all 10 tests PASS.

- [ ] **Step 4.5: Run full unit test suite to confirm nothing regressed**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: all tests PASS.

- [ ] **Step 4.6: Run integration test suite**

Run: `dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj`
Expected: all tests PASS (the existing `HookRoundTripTests` use absolute WireMock URLs, so they keep passing).

- [ ] **Step 4.7: Commit**

```bash
git add src/Kapacitor.Core/HttpClientExtensions.cs test/kapacitor.Tests.Unit/Http/HttpClientExtensionsAbsoluteUrlTests.cs
git commit -m "[AI-583] fail fast on relative URIs in *WithRetryAsync extensions

Replaces unhandled InvalidOperationException (which surfaces as a Stop hook
crash banner in Claude Code) with a clean stderr message pointing the user
at 'kapacitor config set server_url https://<host>'.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: `ConfigCommand` save-path integration

**Files:**
- Modify: `src/kapacitor/Commands/ConfigCommand.cs`
- Modify: `test/kapacitor.Tests.Unit/ConfigCommandTests.cs`

`ApplySet` stays pure — pre-process the value in `Set` before calling `ApplySet`. Parse `--no-probe` in `HandleAsync`.

- [ ] **Step 5.1: Add a unit test asserting `Set` normalizes scheme-less input**

Append to `test/kapacitor.Tests.Unit/ConfigCommandTests.cs`:

```csharp
    [Test]
    public async Task ApplySet_ServerUrl_StoresValueVerbatim() {
        // ApplySet itself stays pure — normalization happens in Set, not here.
        var profile = new Profile();

        var updated = ConfigCommand.ApplySet(profile, "server_url", "https://example.com");

        await Assert.That(updated.ServerUrl).IsEqualTo("https://example.com");
    }
```

(`Set` is private and does I/O, so it is exercised in integration tests rather than directly unit-tested.)

- [ ] **Step 5.2: Run to verify the new test passes (no production change yet)**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ConfigCommandTests/*"`
Expected: PASS (test asserts current behavior; locks it in for future refactors).

- [ ] **Step 5.3: Update `ConfigCommand.HandleAsync` and `Set` to normalize on save**

Replace the body of `src/kapacitor/Commands/ConfigCommand.cs` `HandleAsync` and `Set` with:

```csharp
    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await Console.Error.WriteLineAsync("Usage: kapacitor config <show|set> [key] [value]");

            return 1;
        }

        var subcommand = args[1];
        var skipProbe  = args.Contains("--no-probe");

        return subcommand switch {
            "show"                      => await Show(),
            "set" when args.Length >= 4 => await Set(args[2], args[3], skipProbe),
            "set"                       => SetUsage(),
            _                           => UnknownSubcommand(subcommand)
        };
    }

    static async Task<int> Set(string key, string value, bool skipProbe) {
        if (key == "server_url") {
            var result = await ServerUrlNormalizer.NormalizeAsync(
                value, skipProbe, CancellationToken.None);

            if (result.Warning is not null)
                await Console.Error.WriteLineAsync($"Warning: {result.Warning}");

            value = result.Url;
        }

        var profileConfig = await AppConfig.LoadProfileConfig();
        var profileName   = profileConfig.ActiveProfile;
        var profile       = profileConfig.Profiles.GetValueOrDefault(profileName) ?? new Profile();

        profile = ApplySet(profile, key, value);

        var profiles = new Dictionary<string, Profile>(profileConfig.Profiles) { [profileName] = profile };
        profileConfig = profileConfig with { Profiles = profiles };
        await AppConfig.SaveProfileConfig(profileConfig);

        await Console.Out.WriteLineAsync($"Set {key} = {value} (profile: {profileName})");

        return 0;
    }
```

Also add `--no-probe` to the `SetUsage` text block. Replace the existing `SetUsage` method body with:

```csharp
    static int SetUsage() {
        Console.Error.WriteLine("Usage: kapacitor config set <key> <value> [--no-probe]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Keys:");
        Console.Error.WriteLine("  server_url                  Server URL");
        Console.Error.WriteLine("  daemon.name                 Daemon name");
        Console.Error.WriteLine("  daemon.max_agents           Max concurrent agents");
        Console.Error.WriteLine("  update_check                Enable update check (true/false)");
        Console.Error.WriteLine("  default_visibility          Default session visibility (private, org_public, public)");
        Console.Error.WriteLine("  disable_session_guidelines  Skip injecting recurring-lessons context at SessionStart (true/false)");
        Console.Error.WriteLine("  excluded_repos              Excluded repos, comma-separated (owner/repo,owner/repo)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Flags:");
        Console.Error.WriteLine("  --no-probe                  Skip the reachability check when setting server_url");

        return 1;
    }
```

- [ ] **Step 5.4: Run all unit tests**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: all PASS.

- [ ] **Step 5.5: Commit**

```bash
git add src/kapacitor/Commands/ConfigCommand.cs test/kapacitor.Tests.Unit/ConfigCommandTests.cs
git commit -m "[AI-583] normalize server_url scheme on 'kapacitor config set'

Adds --no-probe to opt out of the reachability check. ApplySet stays
pure; normalization happens in Set before the value reaches ApplySet.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: `ProfileCommand` save-path integration

**Files:**
- Modify: `src/kapacitor/Commands/ProfileCommand.cs`
- Modify: `test/kapacitor.Tests.Unit/ProfileCommandTests.cs`

- [ ] **Step 6.1: Update `HandleAdd` and `AddProfile` to normalize**

In `src/kapacitor/Commands/ProfileCommand.cs`:

Replace the existing `HandleAdd` method with:

```csharp
    static async Task<int> HandleAdd(string configPath, string[] args) {
        if (args.Length < 3) {
            await Console.Error.WriteLineAsync(
                "Usage: kapacitor profile add <name> --server-url <url> [--remote <pattern>]... [--no-probe]");

            return 1;
        }

        var name      = args[2];
        var serverUrl = GetArg(args, "--server-url");
        var skipProbe = args.Contains("--no-probe");

        if (serverUrl is null) {
            await Console.Error.WriteLineAsync("--server-url is required");

            return 1;
        }

        var remotes = new List<string>();

        for (var i = 0; i < args.Length; i++) {
            if (args[i] == "--remote" && i + 1 < args.Length)
                remotes.Add(args[++i]);
        }

        return await AddProfile(configPath, name, serverUrl, remotes.ToArray(), skipProbe);
    }
```

Replace `AddProfile` with:

```csharp
    internal static async Task<int> AddProfile(string configPath, string name, string serverUrl, string[] remotes, bool skipProbe = true) {
        var config = await LoadConfig(configPath);

        if (config.Profiles.ContainsKey(name)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' already exists. Remove it first.");

            return 1;
        }

        var normalized = await ServerUrlNormalizer.NormalizeAsync(
            serverUrl, skipProbe, CancellationToken.None);

        if (normalized.Warning is not null)
            await Console.Error.WriteLineAsync($"Warning: {normalized.Warning}");

        var profiles = new Dictionary<string, Profile>(config.Profiles) {
            [name] = new() {
                ServerUrl = normalized.Url,
                Remotes   = remotes
            }
        };

        config = config with { Profiles = profiles };
        await SaveConfig(configPath, config);

        await Console.Out.WriteLineAsync($"Profile '{name}' added.");

        return 0;
    }
```

Note: `AddProfile` keeps its default `skipProbe = true` so existing unit-test callers (which pass scheme-qualified URLs and shouldn't make network calls) don't need to change.

- [ ] **Step 6.2: Run existing ProfileCommandTests to confirm no regression**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ProfileCommandTests/*"`
Expected: PASS (existing tests pass scheme-qualified URLs; with `skipProbe = true` they hit only the pure path).

- [ ] **Step 6.3: Add a test for scheme-less input in `AddProfile`**

Append to `test/kapacitor.Tests.Unit/ProfileCommandTests.cs`:

```csharp
    [Test]
    public async Task AddProfile_SchemeLessInput_AddsHttpsAndStoresNormalizedUrl() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        // skipProbe defaults to true → no network, falls back to loopback heuristic.
        var result = await ProfileCommand.AddProfile(
            configPath, "contoso", "contoso.kapacitor.io", remotes: []);

        await Assert.That(result).IsEqualTo(0);

        var saved = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(configPath),
            ProfileConfigJsonContextIndented.Default.ProfileConfig)!;

        await Assert.That(saved.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
    }
```

- [ ] **Step 6.4: Run the new test**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ProfileCommandTests/AddProfile_SchemeLessInput_AddsHttpsAndStoresNormalizedUrl"`
Expected: PASS.

- [ ] **Step 6.5: Commit**

```bash
git add src/kapacitor/Commands/ProfileCommand.cs test/kapacitor.Tests.Unit/ProfileCommandTests.cs
git commit -m "[AI-583] normalize server_url scheme on 'kapacitor profile add'

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: `SetupCommand` integration

**Files:**
- Modify: `src/kapacitor/Commands/SetupCommand.cs`

Setup's existing behavior is: `--server-url` → normalize trailing slash → `DiscoverProviderAsync` → error out on unreachable. We change it to: route through `ServerUrlNormalizer.NormalizeAsync` (which probes both schemes for scheme-less input), then keep the strict reachability check by erroring out if the normalizer returns a warning. Net behavior: scheme-less URLs are now accepted by setup; everything else is unchanged.

- [ ] **Step 7.1: Replace the `--server-url` branch**

In `src/kapacitor/Commands/SetupCommand.cs`, replace the block (lines 42–53) that currently reads:

```csharp
        if (serverUrlArg is not null) {
            serverUrl = AppConfig.NormalizeUrl(serverUrlArg);
            await Console.Out.WriteLineAsync($"  Server URL: {serverUrl}");

            try {
                provider = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Checking server…",
                    async _ => await HttpClientExtensions.DiscoverProviderAsync(serverUrl));
                AnsiConsole.MarkupLine($"  [green]✓[/] Reachable · auth provider: [cyan]{Markup.Escape(provider)}[/]");
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(ex.Message)}");
                return 1;
            }
        }
```

with:

```csharp
        if (serverUrlArg is not null) {
            var normalized = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Checking server…",
                async _ => await ServerUrlNormalizer.NormalizeAsync(
                    serverUrlArg, skipProbe: false, CancellationToken.None));

            if (normalized.Warning is not null) {
                AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(normalized.Warning)}");
                return 1;
            }

            serverUrl = normalized.Url;
            await Console.Out.WriteLineAsync($"  Server URL: {serverUrl}");

            try {
                provider = await HttpClientExtensions.DiscoverProviderAsync(serverUrl);
                AnsiConsole.MarkupLine($"  [green]✓[/] Reachable · auth provider: [cyan]{Markup.Escape(provider)}[/]");
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(ex.Message)}");
                return 1;
            }
        }
```

The two-step structure preserves the existing provider-discovery log line ("auth provider: GitHubApp") that users expect to see. The normalizer establishes reachability and resolves the scheme; `DiscoverProviderAsync` then issues its own request to populate the provider cache used downstream by `CreateAuthenticatedClientAsync`. One extra HTTP round trip during setup — acceptable for an interactive command.

- [ ] **Step 7.2: Run unit tests**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: all PASS — `SetupCommandTests` does not exercise the `--server-url` branch interactively, so this change is compile-only at the test level.

- [ ] **Step 7.3: Run integration tests**

Run: `dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj`
Expected: all PASS.

- [ ] **Step 7.4: Commit**

```bash
git add src/kapacitor/Commands/SetupCommand.cs
git commit -m "[AI-583] route 'kapacitor setup --server-url' through ServerUrlNormalizer

Preserves strict 'must be reachable' behavior — if the normalizer can't
reach the server on either scheme, setup errors out as before. Scheme-less
inputs are now accepted (probe picks the right scheme).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: README v1 upgrade note

**Files:**
- Modify: `README.md`
- Modify: `npm/kapacitor/README.md`

- [ ] **Step 8.1: Inspect the current top-level README structure**

Run: `grep -n '^##' README.md npm/kapacitor/README.md`
Pick an insertion point near the existing setup/install section in each file. If a "Troubleshooting" or "Upgrading" section already exists, place the new content there; otherwise insert it just before the "Development" / "Contributing" section (or at end-of-file if neither exists).

- [ ] **Step 8.2: Add the upgrade note to `README.md`**

Insert this block at the chosen location in `README.md`:

```markdown
## Upgrading from v1

The v1 config format stored `server_url` as a bare host name without a
scheme. If `kapacitor` crashes with `An invalid request URI was provided`
after upgrading, your config still has the old format. Fix it with one
command:

```bash
kapacitor config set server_url https://your-server.example.com
```

Or remove the config file and re-run setup:

```bash
rm ~/.config/kapacitor/config.json
kapacitor setup
```
```

- [ ] **Step 8.3: Add the same note to `npm/kapacitor/README.md`**

Insert the same block (above) at the appropriate location in `npm/kapacitor/README.md`.

- [ ] **Step 8.4: Commit**

```bash
git add README.md npm/kapacitor/README.md
git commit -m "[AI-583] document v1 config cleanup in READMEs

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: Full verification

- [ ] **Step 9.1: Build the CLI**

Run: `dotnet build src/kapacitor/kapacitor.csproj`
Expected: build SUCCEEDS, no warnings.

- [ ] **Step 9.2: AOT publish — verify no trimming warnings**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: NO output (no IL2026/IL3050 warnings).

If any warnings appear: they are blocking. Inspect the warning, adjust the offending code (e.g. switch from `JsonArray` collection expressions to `new JsonArray(...)`, or add appropriate `[DynamicallyAccessedMembers]` annotations), and re-run.

- [ ] **Step 9.3: Run all unit tests**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: all PASS.

- [ ] **Step 9.4: Run all integration tests**

Run: `dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj`
Expected: all PASS.

- [ ] **Step 9.5: Manual smoke test**

```bash
# Build and run the smoke commands from a temp HOME to avoid clobbering real config.
export OLD_HOME="$HOME"
export HOME="$(mktemp -d)"

dotnet run --project src/kapacitor/kapacitor.csproj -- config set server_url localhost:9999 --no-probe
dotnet run --project src/kapacitor/kapacitor.csproj -- config show
# Expect: server_url = "http://localhost:9999" (loopback rule, no probe).

dotnet run --project src/kapacitor/kapacitor.csproj -- config set server_url example.com --no-probe
dotnet run --project src/kapacitor/kapacitor.csproj -- config show
# Expect: server_url = "https://example.com" (non-loopback default, no probe).

# Now corrupt the config to v1 shape and confirm the use-site guard fires.
sed -i.bak 's#"https://example.com"#"example.com"#' "$HOME/.config/kapacitor/config.json"
dotnet run --project src/kapacitor/kapacitor.csproj -- stop </dev/null
# Expect exit code 2 and stderr: "server_url is missing a scheme. Run: kapacitor config set server_url https://<host>"

export HOME="$OLD_HOME"
```

Expected: each command behaves as commented.

- [ ] **Step 9.6: Final commit (only if smoke test surfaced fixes)**

If Step 9.5 surfaced anything to fix, commit each fix with a focused message. Otherwise skip.

```bash
git status   # confirm clean tree
```
Expected: nothing to commit, working tree clean.

---

## Verification checklist (post-merge)

- ✅ A scheme-less `server_url` no longer crashes hooks with `InvalidOperationException`.
- ✅ `kapacitor config set server_url <host>` (no scheme) saves a scheme-qualified URL after probing.
- ✅ `kapacitor profile add <name> --server-url <host>` (no scheme) does the same.
- ✅ `kapacitor setup --server-url <host>` accepts scheme-less input and still errors out if unreachable.
- ✅ `--no-probe` skips the network round-trip on `config set` and `profile add`.
- ✅ Legacy v1 configs surface a clear stderr message and exit code 2 — not a stack trace.
- ✅ AOT publish produces zero IL2026/IL3050 warnings.
- ✅ All unit and integration tests pass.
