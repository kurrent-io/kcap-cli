# AI-52: GitHub login via localhost callback + PKCE Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace GitHub Device Flow with the standard OAuth authorization-code-with-PKCE flow using a loopback `HttpListener` for `kapacitor login`, while keeping device flow as a headless fallback and adding it as an opt-in via `--device`.

**Architecture:** The CLI generates a PKCE verifier/challenge and a CSRF `state`, binds an `HttpListener` on `127.0.0.1` on a free port, opens the system browser straight to GitHub's `/login/oauth/authorize` page, then exchanges the returned `code` + verifier for a GitHub access token. The existing `/auth/token` exchange against the Capacitor server is unchanged. Device flow stays in the codebase: it's chosen when the env looks headless (SSH/no DISPLAY/loopback bind fails) or when `--device` is passed, and it also gets the original AI-52 polish (clipboard copy + auto-open of `verification_uri`). The same CSRF `state` discipline is also added to the existing Auth0 flow.

**Tech Stack:** .NET 10, NativeAOT, `System.Net.HttpListener`, `System.Security.Cryptography`, TUnit + WireMock.Net for tests. AOT-safe: keep all JSON via `KapacitorJsonContext`, no reflection-based serialization.

---

## File Structure

**Will create:**
- `src/Kapacitor.Core/Auth/HeadlessEnvironment.cs` — pure env-var checker
- `src/Kapacitor.Core/Auth/Clipboard.cs` — best-effort clipboard writer using `pbcopy`/`wl-copy`/`xclip`/`clip.exe`
- `test/kapacitor.Tests.Unit/HeadlessEnvironmentTests.cs`
- `test/kapacitor.Tests.Unit/OAuthFlowTests.cs` — tests for testable internals (URL builder, callback parser, headless dispatch)

**Will modify:**
- `src/Kapacitor.Core/Auth/OAuthLoginFlow.cs` — add `RunGitHubBrowserFlowAsync`, refactor `HandleGitHubLogin`, polish `RunDeviceFlowAsync`, add `state` to `LoginAsync` (Auth0)
- `src/kapacitor/Program.cs` — parse `--device` flag for `kapacitor login` and `kapacitor login --discover`, pass through
- `src/kapacitor/Commands/SetupCommand.cs` — parse `--device`, thread through `RunDiscoveryAsync`
- `README.md` — note new flow + `--device` option

**Out of scope for code:** the Capacitor GitHub App callback URL must be set to `http://127.0.0.1` in the GitHub App settings before this ships. Noted in the issue as a prerequisite, not a code task.

---

## Task 1: Headless environment detection

**Files:**
- Create: `src/Kapacitor.Core/Auth/HeadlessEnvironment.cs`
- Test: `test/kapacitor.Tests.Unit/HeadlessEnvironmentTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// test/kapacitor.Tests.Unit/HeadlessEnvironmentTests.cs
using kapacitor.Auth;

namespace kapacitor.Tests.Unit;

public class HeadlessEnvironmentTests {
    [Test]
    public async Task Detects_ssh_connection() {
        var env = new Dictionary<string, string?> { ["SSH_CONNECTION"] = "1.2.3.4 5 6.7.8.9 22" };
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.Linux)).IsTrue();
    }

    [Test]
    public async Task Detects_ssh_client() {
        var env = new Dictionary<string, string?> { ["SSH_CLIENT"] = "1.2.3.4 5 22" };
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.Linux)).IsTrue();
    }

    [Test]
    public async Task Linux_without_display_is_headless() {
        var env = new Dictionary<string, string?>();
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.Linux)).IsTrue();
    }

    [Test]
    public async Task Linux_with_display_is_not_headless() {
        var env = new Dictionary<string, string?> { ["DISPLAY"] = ":0" };
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.Linux)).IsFalse();
    }

    [Test]
    public async Task Linux_with_wayland_is_not_headless() {
        var env = new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = "wayland-0" };
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.Linux)).IsFalse();
    }

    [Test]
    public async Task MacOS_default_is_not_headless() {
        var env = new Dictionary<string, string?>();
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.MacOS)).IsFalse();
    }

    [Test]
    public async Task Windows_default_is_not_headless() {
        var env = new Dictionary<string, string?>();
        await Assert.That(HeadlessEnvironment.IsHeadless(env, OSPlatformKind.Windows)).IsFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HeadlessEnvironmentTests/*"`
Expected: build error — `HeadlessEnvironment` and `OSPlatformKind` not defined.

- [ ] **Step 3: Create the implementation**

```csharp
// src/Kapacitor.Core/Auth/HeadlessEnvironment.cs
using System.Runtime.InteropServices;

namespace kapacitor.Auth;

public enum OSPlatformKind { Linux, MacOS, Windows, Other }

/// <summary>
/// Heuristic check for whether the current process has access to an interactive
/// desktop browser. Used by <c>OAuthLoginFlow</c> to decide between the localhost
/// browser flow and the device-code fallback.
/// </summary>
public static class HeadlessEnvironment {
    public static bool IsHeadless() => IsHeadless(CurrentEnv(), CurrentPlatform());

    public static bool IsHeadless(IReadOnlyDictionary<string, string?> env, OSPlatformKind platform) {
        if (HasValue(env, "SSH_CONNECTION") || HasValue(env, "SSH_CLIENT")) return true;

        return platform == OSPlatformKind.Linux
            && !HasValue(env, "DISPLAY")
            && !HasValue(env, "WAYLAND_DISPLAY");
    }

    static bool HasValue(IReadOnlyDictionary<string, string?> env, string key)
        => env.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v);

    static IReadOnlyDictionary<string, string?> CurrentEnv() {
        var keys = new[] { "SSH_CONNECTION", "SSH_CLIENT", "DISPLAY", "WAYLAND_DISPLAY" };
        return keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
    }

    static OSPlatformKind CurrentPlatform() {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))   return OSPlatformKind.Linux;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return OSPlatformKind.MacOS;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return OSPlatformKind.Windows;
        return OSPlatformKind.Other;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HeadlessEnvironmentTests/*"`
Expected: 7 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Core/Auth/HeadlessEnvironment.cs test/kapacitor.Tests.Unit/HeadlessEnvironmentTests.cs
git commit -m "[AI-52] Add HeadlessEnvironment detection for OAuth flow selection"
```

---

## Task 2: Clipboard helper

**Files:**
- Create: `src/Kapacitor.Core/Auth/Clipboard.cs`

No unit test — `Clipboard.TryCopy` shells out to `pbcopy`/`wl-copy`/`xclip`/`clip.exe`. We rely on the call-site treating it as best-effort.

- [ ] **Step 1: Create the implementation**

```csharp
// src/Kapacitor.Core/Auth/Clipboard.cs
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace kapacitor.Auth;

/// <summary>
/// Best-effort clipboard writer. Used to make the GitHub device-flow fallback less
/// painful — the user pastes the user_code into the verification page instead of
/// retyping it. All failures are swallowed; the caller still prints the code.
/// </summary>
public static class Clipboard {
    public static bool TryCopy(string text) {
        try {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return RunWithStdin("pbcopy", "", text);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return RunWithStdin("clip", "", text);

            // Linux: prefer Wayland, fall back to X11
            if (RunWithStdin("wl-copy", "", text)) return true;
            return RunWithStdin("xclip", "-selection clipboard", text);
        } catch {
            return false;
        }
    }

    static bool RunWithStdin(string fileName, string args, string stdin) {
        try {
            var psi = new ProcessStartInfo(fileName, args) {
                RedirectStandardInput = true,
                UseShellExecute       = false,
                CreateNoWindow        = true
            };

            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        } catch {
            return false;
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Kapacitor.Core/Kapacitor.Core.csproj`
Expected: build succeeds, no warnings.

- [ ] **Step 3: Commit**

```bash
git add src/Kapacitor.Core/Auth/Clipboard.cs
git commit -m "[AI-52] Add best-effort cross-platform clipboard helper"
```

---

## Task 3: GitHub browser flow URL builder + callback parser (testable)

**Files:**
- Modify: `src/Kapacitor.Core/Auth/OAuthLoginFlow.cs` (add internal static helpers)
- Test: `test/kapacitor.Tests.Unit/OAuthFlowTests.cs`

The HTTP listener side is hard to unit-test, but the URL construction and the query-string parsing are pure functions. Extract them so they're testable.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/kapacitor.Tests.Unit/OAuthFlowTests.cs
using kapacitor.Auth;

namespace kapacitor.Tests.Unit;

public class OAuthFlowTests {
    [Test]
    public async Task GitHub_authorize_url_includes_all_required_params() {
        var url = OAuthLoginFlow.BuildGitHubAuthorizeUrl(
            clientId:     "Iv1.abc",
            redirectUri:  "http://127.0.0.1:54321/callback",
            state:        "state-xyz",
            codeChallenge:"challenge-123");

        await Assert.That(url).StartsWith("https://github.com/login/oauth/authorize?");
        await Assert.That(url).Contains("client_id=Iv1.abc");
        await Assert.That(url).Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A54321%2Fcallback");
        await Assert.That(url).Contains("state=state-xyz");
        await Assert.That(url).Contains("scope=read%3Auser%20read%3Aorg");
        await Assert.That(url).Contains("code_challenge=challenge-123");
        await Assert.That(url).Contains("code_challenge_method=S256");
        await Assert.That(url).Contains("response_type=code");
    }

    [Test]
    public async Task Callback_parser_returns_code_when_state_matches() {
        var result = OAuthLoginFlow.ParseCallback(
            queryString:  "?code=abc&state=expected",
            expectedState:"expected");

        await Assert.That(result.Code).IsEqualTo("abc");
        await Assert.That(result.Error).IsNull();
    }

    [Test]
    public async Task Callback_parser_rejects_state_mismatch() {
        var result = OAuthLoginFlow.ParseCallback(
            queryString:  "?code=abc&state=attacker",
            expectedState:"expected");

        await Assert.That(result.Code).IsNull();
        await Assert.That(result.Error).IsEqualTo("state_mismatch");
    }

    [Test]
    public async Task Callback_parser_surfaces_provider_error() {
        var result = OAuthLoginFlow.ParseCallback(
            queryString:  "?error=access_denied&state=expected",
            expectedState:"expected");

        await Assert.That(result.Code).IsNull();
        await Assert.That(result.Error).IsEqualTo("access_denied");
    }

    [Test]
    public async Task Callback_parser_rejects_missing_code() {
        var result = OAuthLoginFlow.ParseCallback(
            queryString:  "?state=expected",
            expectedState:"expected");

        await Assert.That(result.Code).IsNull();
        await Assert.That(result.Error).IsEqualTo("missing_code");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/OAuthFlowTests/*"`
Expected: build error — `BuildGitHubAuthorizeUrl` and `ParseCallback` not defined.

- [ ] **Step 3: Add the helpers to OAuthLoginFlow**

Add to `src/Kapacitor.Core/Auth/OAuthLoginFlow.cs`, just above the `GenerateCodeVerifier()` method (around line 358):

```csharp
internal readonly record struct CallbackResult(string? Code, string? Error);

internal static string BuildGitHubAuthorizeUrl(
        string clientId, string redirectUri, string state, string codeChallenge) =>
    "https://github.com/login/oauth/authorize?"             +
    $"client_id={Uri.EscapeDataString(clientId)}"           +
    $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"    +
    $"&state={Uri.EscapeDataString(state)}"                 +
    $"&scope={Uri.EscapeDataString("read:user read:org")}"  +
    $"&code_challenge={Uri.EscapeDataString(codeChallenge)}"+
    "&code_challenge_method=S256"                           +
    "&response_type=code";

internal static CallbackResult ParseCallback(string queryString, string expectedState) {
    var query = System.Web.HttpUtility.ParseQueryString(queryString);
    var error = query["error"];
    if (error is not null) return new(null, error);

    var state = query["state"];
    if (state != expectedState) return new(null, "state_mismatch");

    var code = query["code"];
    return string.IsNullOrEmpty(code) ? new(null, "missing_code") : new(code, null);
}
```

`System.Web.HttpUtility` lives in `System.Web` — make sure the using is there. If the project doesn't already reference it, replace with `Microsoft.AspNetCore.WebUtilities.QueryHelpers` is overkill; use a tiny manual parser instead:

```csharp
internal static CallbackResult ParseCallback(string queryString, string expectedState) {
    var qs    = queryString.TrimStart('?');
    var parts = qs.Split('&', StringSplitOptions.RemoveEmptyEntries);
    string? code = null, state = null, error = null;

    foreach (var part in parts) {
        var eq = part.IndexOf('=');
        if (eq < 0) continue;
        var key = part[..eq];
        var val = Uri.UnescapeDataString(part[(eq + 1)..]);
        switch (key) {
            case "code":  code  = val; break;
            case "state": state = val; break;
            case "error": error = val; break;
        }
    }

    if (error is not null)        return new(null, error);
    if (state != expectedState)   return new(null, "state_mismatch");
    return string.IsNullOrEmpty(code) ? new(null, "missing_code") : new(code, null);
}
```

Use the manual parser to avoid pulling in `System.Web` for AOT cleanliness.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/OAuthFlowTests/*"`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Core/Auth/OAuthLoginFlow.cs test/kapacitor.Tests.Unit/OAuthFlowTests.cs
git commit -m "[AI-52] Add testable GitHub auth URL builder + callback parser"
```

---

## Task 4: Implement `RunGitHubBrowserFlowAsync`

**Files:**
- Modify: `src/Kapacitor.Core/Auth/OAuthLoginFlow.cs`

No unit test for the listener orchestration itself (it requires a real loopback + browser). Tests for the testable pieces already exist from Task 3. The integration story is "run `kapacitor login` against a real server in dev mode."

- [ ] **Step 1: Add the method**

Add to `src/Kapacitor.Core/Auth/OAuthLoginFlow.cs` directly below `RunDeviceFlowAsync` (around line 131):

```csharp
/// <summary>
/// Runs GitHub authorization-code-with-PKCE flow against a localhost loopback
/// listener. Opens the system browser to GitHub's authorize page; on callback,
/// verifies CSRF state and exchanges code+verifier for an access token.
/// Returns the token on success, or <c>null</c> on user cancel, state mismatch,
/// or upstream error. Throws if the loopback port can't be bound — the caller
/// uses that signal to fall back to device flow.
/// </summary>
public static async Task<string?> RunGitHubBrowserFlowAsync(string clientId, TimeSpan? timeout = null) {
    var verifier  = GenerateCodeVerifier();
    var challenge = GenerateCodeChallenge(verifier);
    var state     = GenerateCodeVerifier(); // reuse the random source — same entropy is fine

    var port        = GetAvailablePort();
    var redirectUri = $"http://127.0.0.1:{port}/callback";

    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();

    var authUrl = BuildGitHubAuthorizeUrl(clientId, redirectUri, state, challenge);

    await Console.Out.WriteLineAsync("Opening browser for GitHub authentication...");
    await Console.Out.WriteLineAsync($"  If the browser doesn't open, visit: {authUrl}");

    try {
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
    } catch {
        /* Browser open is best-effort — user can still copy the URL */
    }

    using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));

    HttpListenerContext context;
    try {
        context = await listener.GetContextAsync().WaitAsync(cts.Token);
    } catch (OperationCanceledException) {
        Console.Error.WriteLine("Timed out waiting for authorization. Re-run `kapacitor login` to try again.");
        return null;
    }

    var callback = ParseCallback(context.Request.Url?.Query ?? "", state);
    await RespondCallbackAsync(context, callback);
    listener.Stop();

    if (callback.Code is null) {
        Console.Error.WriteLine($"Authorization failed: {callback.Error}");
        return null;
    }

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Accept.Add(new("application/json"));

    var tokenResponse = await http.PostAsync(
        "https://github.com/login/oauth/access_token",
        new FormUrlEncodedContent(new Dictionary<string, string> {
            ["client_id"]     = clientId,
            ["code"]          = callback.Code,
            ["redirect_uri"]  = redirectUri,
            ["code_verifier"] = verifier,
            ["grant_type"]    = "authorization_code"
        }));

    if (!tokenResponse.IsSuccessStatusCode) {
        Console.Error.WriteLine($"Error exchanging code: {await tokenResponse.Content.ReadAsStringAsync()}");
        return null;
    }

    var tokenResult = (await tokenResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.GitHubTokenResponse))!;
    if (tokenResult.AccessToken is null) {
        Console.Error.WriteLine($"Error: {tokenResult.Error ?? "no access_token in response"}");
        return null;
    }

    await Console.Out.WriteLineAsync("Authorization complete.");
    return tokenResult.AccessToken;
}

static async Task RespondCallbackAsync(HttpListenerContext ctx, CallbackResult callback) {
    var (status, message) = callback.Code is not null
        ? ("Authentication successful!",  "You can close this window and return to the terminal.")
        : ($"Authentication failed: {callback.Error}", "Return to the terminal for details.");

    var html = $"<html><body style='font-family:system-ui;max-width:480px;margin:80px auto;text-align:center'>"
             + $"<h2>{System.Net.WebUtility.HtmlEncode(status)}</h2>"
             + $"<p>{System.Net.WebUtility.HtmlEncode(message)}</p></body></html>";

    var buffer = Encoding.UTF8.GetBytes(html);
    ctx.Response.ContentType     = "text/html";
    ctx.Response.ContentLength64 = buffer.Length;
    await ctx.Response.OutputStream.WriteAsync(buffer);
    ctx.Response.Close();
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Kapacitor.Core/Kapacitor.Core.csproj`
Expected: build succeeds.

- [ ] **Step 3: Verify AOT cleanliness**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: empty output (no AOT warnings).

- [ ] **Step 4: Commit**

```bash
git add src/Kapacitor.Core/Auth/OAuthLoginFlow.cs
git commit -m "[AI-52] Implement GitHub browser flow with PKCE + loopback callback"
```

---

## Task 5: Dispatcher — choose browser or device flow

**Files:**
- Modify: `src/Kapacitor.Core/Auth/OAuthLoginFlow.cs` (`HandleGitHubLogin` + new public `LoginWithDiscoveryAsync` overload)
- Test: `test/kapacitor.Tests.Unit/OAuthFlowTests.cs` (add dispatch test)

- [ ] **Step 1: Write the failing dispatch test**

Append to `test/kapacitor.Tests.Unit/OAuthFlowTests.cs`:

```csharp
[Test]
public async Task ChooseGitHubFlow_returns_device_when_forced() {
    var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: true, isHeadless: false);
    await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
}

[Test]
public async Task ChooseGitHubFlow_returns_device_when_headless() {
    var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: true);
    await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
}

[Test]
public async Task ChooseGitHubFlow_returns_browser_when_interactive() {
    var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: false);
    await Assert.That(choice).IsEqualTo(GitHubFlow.Browser);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/OAuthFlowTests/ChooseGitHubFlow_*"`
Expected: build error.

- [ ] **Step 3: Add `ChooseGitHubFlow` and refactor `HandleGitHubLogin`**

Add near the bottom of `OAuthLoginFlow` class:

```csharp
public enum GitHubFlow { Browser, Device }

internal static GitHubFlow ChooseGitHubFlow(bool forceDevice, bool isHeadless)
    => forceDevice || isHeadless ? GitHubFlow.Device : GitHubFlow.Browser;
```

Change the public `LoginWithDiscoveryAsync` signature to accept `forceDevice`, keeping the existing zero-arg overload for back-compat:

```csharp
public static Task<int> LoginWithDiscoveryAsync(string serverUrl)
    => LoginWithDiscoveryAsync(serverUrl, forceDevice: false);

public static async Task<int> LoginWithDiscoveryAsync(string serverUrl, bool forceDevice) {
    // ... existing body, but pass forceDevice into HandleGitHubLogin
}
```

Replace the `HandleGitHubLogin` body (currently line 258-263) with:

```csharp
static async Task<int> HandleGitHubLogin(string serverUrl, AuthDiscoveryResponse config, bool forceDevice) {
    var accessToken = await AcquireGitHubTokenAsync(config.GithubClientId!, forceDevice);
    if (accessToken is null) return 1;
    return await ExchangeAndSaveAsync(serverUrl, accessToken, config.Provider);
}

internal static async Task<string?> AcquireGitHubTokenAsync(string clientId, bool forceDevice) {
    var headless = HeadlessEnvironment.IsHeadless();
    var choice   = ChooseGitHubFlow(forceDevice, headless);

    if (choice == GitHubFlow.Browser) {
        try {
            var token = await RunGitHubBrowserFlowAsync(clientId);
            if (token is not null) return token;
            // Browser flow ran but user cancelled / state mismatch — don't silently fall back.
            return null;
        } catch (HttpListenerException ex) {
            Console.Error.WriteLine($"Could not bind loopback listener ({ex.Message}); falling back to device flow.");
        } catch (PlatformNotSupportedException ex) {
            Console.Error.WriteLine($"Loopback listener not supported on this platform ({ex.Message}); falling back to device flow.");
        }
    }

    return await RunDeviceFlowAsync(clientId);
}
```

Update the `switch` in `LoginWithDiscoveryAsync` accordingly:

```csharp
return config.Provider switch {
    AuthProvider.None      => HandleNoneLogin(),
    AuthProvider.GitHubApp => await HandleGitHubLogin(serverUrl, config, forceDevice),
    AuthProvider.Auth0     => await HandleAuth0Login(config),
    _                      => HandleUnknownProvider(config.Provider)
};
```

- [ ] **Step 4: Run tests**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/OAuthFlowTests/*"`
Expected: all OAuthFlowTests pass.

- [ ] **Step 5: Verify nothing else regressed**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/LoginDiscoverTests/*"`
Expected: all 7 LoginDiscoverTests still pass.

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Core/Auth/OAuthLoginFlow.cs test/kapacitor.Tests.Unit/OAuthFlowTests.cs
git commit -m "[AI-52] Dispatch GitHub login between browser and device flow"
```

---

## Task 6: Polish device flow (original AI-52 scope)

**Files:**
- Modify: `src/Kapacitor.Core/Auth/OAuthLoginFlow.cs` (`RunDeviceFlowAsync`)

- [ ] **Step 1: Replace the user-facing block in `RunDeviceFlowAsync`**

Replace lines 87–96 of the original file (the block that writes `Enter code:` etc.) with:

```csharp
var copied = Clipboard.TryCopy(device.UserCode);

await Console.Out.WriteLineAsync();
await Console.Out.WriteLineAsync($"  Code: {device.UserCode}{(copied ? "  (copied to clipboard)" : "")}");
await Console.Out.WriteLineAsync($"  Open: {device.VerificationUri}");
await Console.Out.WriteLineAsync();

try {
    Process.Start(new ProcessStartInfo(device.VerificationUri) { UseShellExecute = true });
} catch {
    /* Browser open is best-effort */
}
```

- [ ] **Step 2: Build + AOT-check**

Run in parallel:
- `dotnet build src/Kapacitor.Core/Kapacitor.Core.csproj`
- `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`

Expected: build succeeds; AOT grep is empty.

- [ ] **Step 3: Commit**

```bash
git add src/Kapacitor.Core/Auth/OAuthLoginFlow.cs
git commit -m "[AI-52] Polish device flow: clipboard copy + auto-open verification URI"
```

---

## Task 7: Wire `--device` flag through CLI commands

**Files:**
- Modify: `src/kapacitor/Program.cs` (`login` case at line 182 + `HandleDiscoverLoginAsync` at line 858)
- Modify: `src/kapacitor/Commands/SetupCommand.cs` (`HandleAsync` at line 11 + `RunDiscoveryAsync` at line 231)

- [ ] **Step 1: Update `kapacitor login` case in Program.cs (line 182–194)**

Replace with:

```csharp
case "login": {
    var forceDevice = args.Contains("--device");

    if (args.Contains("--discover")) {
        return await HandleDiscoverLoginAsync(forceDevice);
    }

    if (baseUrl is null) {
        Console.Error.WriteLine("No server configured. Run `kapacitor setup`, set KAPACITOR_URL, or use `kapacitor login --discover`.");
        return 1;
    }

    return await OAuthLoginFlow.LoginWithDiscoveryAsync(baseUrl, forceDevice);
}
```

- [ ] **Step 2: Update `HandleDiscoverLoginAsync` (line 858)**

Change signature and the `RunDeviceFlowAsync` call site (line 870):

```csharp
async Task<int> HandleDiscoverLoginAsync(bool forceDevice) {
    using var http  = new HttpClient();
    var proxyClient = new AuthProxyClient(http);

    var clientId = await proxyClient.GetGitHubClientIdAsync(AuthProxyEndpoint.Url);

    if (clientId is null) {
        await Console.Error.WriteLineAsync("Cannot reach the Kurrent auth service.");
        return 1;
    }

    var ghToken = await OAuthLoginFlow.AcquireGitHubTokenAsync(clientId, forceDevice);
    if (ghToken is null) return 1;

    // ... rest unchanged
}
```

- [ ] **Step 3: Update `SetupCommand.HandleAsync` (line 11)**

After `var noPrompt = args.Contains("--no-prompt");` (line 13), add:

```csharp
var forceDevice = args.Contains("--device");
```

Update the call to `RunDiscoveryAsync()` (line 57) to pass it:

```csharp
var discovered = await RunDiscoveryAsync(forceDevice);
```

And the call to `LoginWithDiscoveryAsync` (line 79):

```csharp
var loginResult = await OAuthLoginFlow.LoginWithDiscoveryAsync(serverUrl, forceDevice);
```

- [ ] **Step 4: Update `SetupCommand.RunDiscoveryAsync` (line 231)**

```csharp
static async Task<(string ServerUrl, string PreAuthToken, string Provider)?> RunDiscoveryAsync(bool forceDevice) {
    // ... existing body, but replace the RunDeviceFlowAsync call (line 244):
    var ghToken = await OAuthLoginFlow.AcquireGitHubTokenAsync(clientId, forceDevice);
    if (ghToken is null) return null;
    // ... rest unchanged
}
```

- [ ] **Step 5: Build + run full test suite**

Run:
```bash
dotnet build src/kapacitor/kapacitor.csproj
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: build succeeds; tests pass.

- [ ] **Step 6: Verify AOT**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: empty.

- [ ] **Step 7: Commit**

```bash
git add src/kapacitor/Program.cs src/kapacitor/Commands/SetupCommand.cs
git commit -m "[AI-52] Thread --device flag through login, setup, and discover"
```

---

## Task 8: Add CSRF `state` to Auth0 flow

**Files:**
- Modify: `src/Kapacitor.Core/Auth/OAuthLoginFlow.cs` (`LoginAsync`, line 272)

- [ ] **Step 1: Generate and verify `state` in Auth0 flow**

In `LoginAsync` (line 272), after `var challenge = GenerateCodeChallenge(verifier);` add:

```csharp
var state = GenerateCodeVerifier();
```

Update the auth URL (line 282-287) to include state:

```csharp
var authUrl = $"https://{auth0Domain}/authorize?"                           +
    $"response_type=code&client_id={Uri.EscapeDataString(clientId)}"        +
    $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"                    +
    $"&scope={Uri.EscapeDataString("openid profile email offline_access")}" +
    $"&audience={Uri.EscapeDataString(audience)}"                           +
    $"&state={Uri.EscapeDataString(state)}"                                 +
    $"&code_challenge={challenge}&code_challenge_method=S256";
```

After receiving the callback (after line 293, `var code = context.Request.QueryString["code"];`), add:

```csharp
var returnedState = context.Request.QueryString["state"];
if (returnedState != state) {
    Console.Error.WriteLine("Error: state mismatch — possible CSRF. Aborting.");
    listener.Stop();
    return 1;
}
```

- [ ] **Step 2: Build + AOT-check**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: empty.

- [ ] **Step 3: Commit**

```bash
git add src/Kapacitor.Core/Auth/OAuthLoginFlow.cs
git commit -m "[AI-52] Add CSRF state parameter to Auth0 login flow"
```

---

## Task 9: Documentation updates

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update the login section in README.md**

Find the line `kapacitor login          # authenticate via OAuth` (line 249) and replace its surrounding block to mention the browser flow and `--device`:

```markdown
kapacitor login          # authenticate via OAuth (browser flow by default)
kapacitor login --device # force device-code flow (use in SSH / headless envs)
```

- [ ] **Step 2: Verify markdown**

Run: `cat README.md | grep -A1 "kapacitor login"` and visually confirm the help block reads sensibly.

- [ ] **Step 3: Final full-suite verification**

Run:
```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: all tests pass; AOT grep empty.

- [ ] **Step 4: Manual smoke test (interactive — record outcome)**

Against a real or staging Capacitor server with GitHubApp provider:
1. `kapacitor login` → browser opens to GitHub authorize page; click Authorize → CLI completes with `Logged in as <user>`.
2. `kapacitor login --device` → original device-flow text appears; code printed + (if clipboard tool installed) "copied to clipboard"; browser opens to verification page.
3. On SSH (`ssh into a box`, then `kapacitor login`): falls through to device flow automatically.

Note: this is interactive and can't be automated in unit tests. Document the result in the PR description.

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "[AI-52] Document browser-default login flow and --device override"
```

---

## Self-Review

**Spec coverage check (against the rewritten AI-52):**

| Requirement | Task |
|---|---|
| Primary flow: PKCE + loopback callback | Tasks 3, 4 |
| CSRF `state` on GitHub flow | Task 3 (URL + parser) + Task 4 (wiring) |
| `state` also added to Auth0 flow | Task 8 |
| `--device` forces device flow | Tasks 5, 7 |
| Headless auto-detect → device flow | Tasks 1, 5 |
| Loopback port bind failure → fallback | Task 5 (`HttpListenerException` catch) |
| Listener bound to `127.0.0.1` (not `0.0.0.0`) | Task 4 |
| Clean listener shutdown | Task 4 (`using var listener` + explicit `Stop`) |
| Device flow clipboard + auto-open polish | Tasks 2, 6 |
| README + help text updated | Task 9 |
| GitHub App callback URL prerequisite | Out of scope for code (called out in issue) |

**Placeholder scan:** No "TODO" / "implement later" / "add appropriate error handling" entries. Every step has either real code or a real command.

**Type consistency:** `AcquireGitHubTokenAsync` is the new shared entry point used by `HandleGitHubLogin` (Task 5), `HandleDiscoverLoginAsync` (Task 7), and `SetupCommand.RunDiscoveryAsync` (Task 7). `GitHubFlow.Browser/Device` enum used consistently. `CallbackResult` struct used in both Task 3 (parser) and Task 4 (listener).

**One inconsistency caught and fixed:** Task 5's `ChooseGitHubFlow` is used only for tests + early dispatch — the actual fallback on bind failure happens after the `try { RunGitHubBrowserFlowAsync(...) }` block, so the enum doesn't fully encode the runtime decision. That's intentional and documented in Task 5 step 3.
