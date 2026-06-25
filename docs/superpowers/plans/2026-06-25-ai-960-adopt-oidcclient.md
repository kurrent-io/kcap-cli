# AI-960: Adopt Duende.IdentityModel.OidcClient — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the hand-rolled OAuth machinery in `OAuthLoginFlow` with `Duende.IdentityModel.OidcClient` 7.1.0 for the WorkOS and GitHub-browser authorization-code-with-PKCE flows, keeping device flow + org-switch + the GitHub proxy exchange custom.

**Architecture:** One shared `LoopbackBrowser : IBrowser` replaces both duplicated `HttpListener` loopbacks. WorkOS uses OidcClient's full `LoginAsync` (works because the code flow never requires an `id_token`); the WorkOS-specific response fields are recovered by deserializing the raw token response into the existing `WorkOSAuthResponse`. GitHub-browser uses OidcClient only for the front-channel (`PrepareLoginAsync` + `AuthorizeResponse`) and keeps the custom JSON proxy code-exchange. AI-958 is folded in: WorkOS authorize is always built on `api.workos.com`.

**Tech Stack:** .NET 10, NativeAOT, `Duende.IdentityModel.OidcClient` 7.1.0 (+ `Duende.IdentityModel` 8.1.0), TUnit, WireMock.Net, NSubstitute.

## Global Constraints

- Target `net10.0`, NativeAOT — after any change, `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` MUST be empty. (AOT warnings only show on `publish`, not `build`.)
- Central Package Management — package versions go in `Directory.Packages.props`; project files carry version-less `<PackageReference>`.
- `JsonArray` collection expressions are banned (need `new JsonArray(...)`); not expected here, but stay AOT-safe — use the source-gen `CapacitorJsonContext` for all our DTO (de)serialization.
- TUnit: filter with `--treenode-filter`, not `--filter`. Tests run as executables via `dotnet run --project <test csproj>`.
- README sync: only required if user-facing CLI surface changes. This change is internal plumbing (same commands/flags/prereqs) — confirm no README edit is needed (Task 5), don't invent one.
- Preserve the current `AcquireGitHubTokenAsync` contract: a `null` from the browser flow is a hard login failure (no device fallback); only a loopback **bind** exception (`HttpListenerException`/`PlatformNotSupportedException`) falls back to device flow.

**Spec:** `docs/superpowers/specs/2026-06-24-ai-960-adopt-oidcclient-design.md`

---

### Task 1: Swap the package dependency

Remove the unused, mis-located old `IdentityModel.OidcClient` 6.0.0 and add `Duende.IdentityModel.OidcClient` 7.1.0 (+ `Duende.IdentityModel` 8.1.0) to the project that actually owns the auth code (`Capacitor.Cli.Core`).

**Files:**
- Modify: `Directory.Packages.props:6`
- Modify: `src/Capacitor.Cli/Capacitor.Cli.csproj:35`
- Modify: `src/Capacitor.Cli.Core/Capacitor.Cli.Core.csproj` (add a `<PackageReference>` ItemGroup)

**Interfaces:**
- Consumes: nothing.
- Produces: `Duende.IdentityModel.OidcClient` + `Duende.IdentityModel` types available in `Capacitor.Cli.Core` (and transitively in `Capacitor.Cli`, which references Core).

- [ ] **Step 1: Replace the package version pin**

In `Directory.Packages.props`, replace the line:
```xml
        <PackageVersion Include="IdentityModel.OidcClient" Version="6.0.0" />
```
with:
```xml
        <PackageVersion Include="Duende.IdentityModel.OidcClient" Version="7.1.0" />
        <PackageVersion Include="Duende.IdentityModel" Version="8.1.0" />
```

- [ ] **Step 2: Remove the stray reference from the CLI project**

In `src/Capacitor.Cli/Capacitor.Cli.csproj`, delete the line:
```xml
        <PackageReference Include="IdentityModel.OidcClient" />
```

- [ ] **Step 3: Add the references to Core**

In `src/Capacitor.Cli.Core/Capacitor.Cli.Core.csproj`, add a new ItemGroup after the existing `<ItemGroup>` blocks (before `</Project>`):
```xml
    <ItemGroup>
        <PackageReference Include="Duende.IdentityModel.OidcClient" />
        <PackageReference Include="Duende.IdentityModel" />
    </ItemGroup>
```

- [ ] **Step 4: Restore and build**

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
Expected: build succeeds, restore pulls `Duende.IdentityModel.OidcClient 7.1.0` + `Duende.IdentityModel 8.1.0`. No `IdentityModel.OidcClient` left:
Run: `grep -rni "IdentityModel.OidcClient\b" Directory.Packages.props src/*/*.csproj` returns only the `Duende.` lines.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/Capacitor.Cli/Capacitor.Cli.csproj src/Capacitor.Cli.Core/Capacitor.Cli.Core.csproj
git commit -m "[AI-960] Swap IdentityModel.OidcClient 6.0.0 -> Duende.IdentityModel.OidcClient 7.1.0"
```

---

### Task 2: Shared `LoopbackBrowser : IBrowser`

A single 127.0.0.1 loopback listener implementing OidcClient's `IBrowser`, replacing both duplicated `HttpListener` blocks. The browser-open action is injectable so it's unit-testable without launching a real browser. The bind exception is **not** caught — it must propagate so the GitHub device-flow fallback still triggers.

**Files:**
- Create: `src/Capacitor.Cli.Core/Auth/LoopbackBrowser.cs`
- Modify: `src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs` (make `GetAvailablePort` `internal` so the browser/tests share it)
- Test: `test/Capacitor.Cli.Tests.Unit/LoopbackBrowserTests.cs`

**Interfaces:**
- Consumes: `OAuthLoginFlow.GetAvailablePort()` (now `internal static int`).
- Produces:
  - `public sealed class LoopbackBrowser : IBrowser` with ctor `LoopbackBrowser(Action<string>? openBrowser = null)` and `Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken ct = default)`.
  - `BrowserResult{ResultType = Success, Response = <raw callback query incl. leading '?'>}` on `/callback`; `{ResultType = Timeout}` on timeout; the port is parsed from `options.EndUrl`; `listener.Start()` exceptions propagate.

- [ ] **Step 1: Make `GetAvailablePort` internal**

In `src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs`, change:
```csharp
    static int GetAvailablePort() {
```
to:
```csharp
    internal static int GetAvailablePort() {
```

- [ ] **Step 2: Write the failing success + timeout tests**

Create `test/Capacitor.Cli.Tests.Unit/LoopbackBrowserTests.cs`:
```csharp
using Capacitor.Cli.Core.Auth;
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Tests.Unit;

public class LoopbackBrowserTests {
    [Test]
    public async Task Returns_success_with_raw_query_on_callback() {
        var port     = OAuthLoginFlow.GetAvailablePort();
        var redirect = $"http://127.0.0.1:{port}/callback";
        var browser  = new LoopbackBrowser(openBrowser: _ => { }); // don't launch a real browser

        var invoke = browser.InvokeAsync(new BrowserOptions("http://example.test/authorize", redirect));

        using var http = new HttpClient();
        // Listener is started synchronously before InvokeAsync's first await, so this connects.
        _ = await http.GetAsync($"{redirect}?code=abc&state=xyz");

        var result = await invoke;
        await Assert.That(result.ResultType).IsEqualTo(BrowserResultType.Success);
        await Assert.That(result.Response).Contains("code=abc");
        await Assert.That(result.Response).Contains("state=xyz");
    }

    [Test]
    public async Task Returns_timeout_when_no_callback_arrives() {
        var port     = OAuthLoginFlow.GetAvailablePort();
        var redirect = $"http://127.0.0.1:{port}/callback";
        var browser  = new LoopbackBrowser(openBrowser: _ => { });

        var result = await browser.InvokeAsync(
            new BrowserOptions("http://example.test/authorize", redirect) { Timeout = TimeSpan.FromMilliseconds(200) });

        await Assert.That(result.ResultType).IsEqualTo(BrowserResultType.Timeout);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LoopbackBrowserTests/*"`
Expected: FAIL — `LoopbackBrowser` does not exist (compile error).

- [ ] **Step 4: Implement `LoopbackBrowser`**

Create `src/Capacitor.Cli.Core/Auth/LoopbackBrowser.cs`:
```csharp
using System.Diagnostics;
using System.Net;
using System.Text;
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// OidcClient <see cref="IBrowser"/> backed by a 127.0.0.1 loopback HttpListener.
/// Opens the system browser to the authorize URL, waits for the redirect callback,
/// and returns its raw query string. WorkOS documents the loopback exception as
/// 127.0.0.1 (not localhost). The bind exception is intentionally NOT caught so the
/// GitHub flow can fall back to device flow on a bind failure.
/// </summary>
public sealed class LoopbackBrowser(Action<string>? openBrowser = null) : IBrowser {
    readonly Action<string> _openBrowser = openBrowser ?? OpenSystemBrowser;

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken ct = default) {
        var port = new Uri(options.EndUrl).Port;

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start(); // bind failure propagates (HttpListenerException / PlatformNotSupportedException)

        await Console.Out.WriteLineAsync("Opening browser for authentication...");
        await Console.Out.WriteLineAsync($"  If the browser doesn't open, visit: {options.StartUrl}");
        _openBrowser(options.StartUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Timeout);

        HttpListenerContext context;

        while (true) {
            var getContext = listener.GetContextAsync();

            try {
                context = await getContext.WaitAsync(cts.Token);
            } catch (OperationCanceledException) {
                listener.Stop();
                _ = getContext.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                return new BrowserResult { ResultType = BrowserResultType.Timeout };
            }

            if (context.Request.Url?.AbsolutePath == "/callback") break;

            // Ignore favicon and other browser-issued requests that aren't our callback.
            context.Response.StatusCode = 404;
            context.Response.Close();
        }

        var query = context.Request.Url?.Query ?? "";
        await WriteClosingPageAsync(context, success: !query.Contains("error="));
        listener.Stop();

        return new BrowserResult { ResultType = BrowserResultType.Success, Response = query };
    }

    static void OpenSystemBrowser(string url) {
        try {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        } catch {
            // Best-effort — headless environments (devcontainers, SSH) have no browser.
        }
    }

    static async Task WriteClosingPageAsync(HttpListenerContext ctx, bool success) {
        var (title, message) = success
            ? ("Authentication successful!", "You can close this window and return to the terminal.")
            : ("Authentication failed", "Return to the terminal for details.");

        var html = $"<html><body style='font-family:system-ui;max-width:480px;margin:80px auto;text-align:center'>"
          + $"<h2>{WebUtility.HtmlEncode(title)}</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>";

        var buffer = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType     = "text/html";
        ctx.Response.ContentLength64 = buffer.Length;
        await ctx.Response.OutputStream.WriteAsync(buffer);
        ctx.Response.Close();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LoopbackBrowserTests/*"`
Expected: PASS (both tests).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/Auth/LoopbackBrowser.cs src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs test/Capacitor.Cli.Tests.Unit/LoopbackBrowserTests.cs
git commit -m "[AI-960] Add shared LoopbackBrowser IBrowser implementation"
```

---

### Task 3: WorkOS flow on OidcClient (+ AI-958 fold-in)

Replace the WorkOS loopback + exchange with OidcClient's `LoginAsync`, always building authorize on `api.workos.com` (AI-958). Map the raw token response into the existing `WorkOSAuthResponse` via the source-gen context so omitted/nullable WorkOS fields don't throw. Rewire `WorkOSDiscovery` to the new no-`authorizeBase` helper and delete the bespoke WorkOS methods.

**Files:**
- Modify: `src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs`
- Modify: `src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/OAuthFlowTests.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/WorkOSDiscoveryTests.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/FakeBrowser.cs` (new test helper)

**Interfaces:**
- Consumes: `LoopbackBrowser`, `OAuthLoginFlow.GetAvailablePort()`.
- Produces:
  - `internal static OidcClientOptions BuildWorkOSOptions(string clientId, string apiBase, string redirectUri)`
  - `internal static Parameters WorkOSFrontChannel(string? organizationId)`
  - `public static Task<WorkOSAuthResponse?> AuthenticateWorkOSAsync(string clientId, string? organizationId, IBrowser browser, string apiBase = "https://api.workos.com")`
  - `WorkOSDiscovery.RunAsync(...)` `orglessLogin` param changes to `Func<Task<WorkOSAuthResponse?>>`.
- Removes: `RunWorkOSLoopbackAsync`, `AuthenticateWorkOSCodeAsync`, `WorkOSLoopbackResult`, `LoginWorkOSAsync(string?, string, string?)`'s old body, the `WorkOSApiBase` duplicate in `WorkOSDiscovery`.

- [ ] **Step 1: Add the `FakeBrowser` test helper**

Create `test/Capacitor.Cli.Tests.Unit/FakeBrowser.cs`:
```csharp
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Test IBrowser: returns a canned callback query, or a non-success result.</summary>
public sealed class FakeBrowser(Func<string, BrowserResult> respond) : IBrowser {
    public string? LastStartUrl { get; private set; }

    public Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken ct = default) {
        LastStartUrl = options.StartUrl;
        return Task.FromResult(respond(options.StartUrl));
    }

    // Echo the state from the StartUrl so ProcessResponseAsync's state check passes.
    public static FakeBrowser WithCode(string code) => new(startUrl => {
        var query = new Uri(startUrl).Query.TrimStart('?');
        var state = query.Split('&')
            .First(p => p.StartsWith("state=", StringComparison.Ordinal))["state=".Length..];
        return new BrowserResult { ResultType = BrowserResultType.Success, Response = $"?code={code}&state={state}" };
    });

    public static FakeBrowser WithRawQuery(string query) =>
        new(_ => new BrowserResult { ResultType = BrowserResultType.Success, Response = query });

    public static FakeBrowser NonSuccess(BrowserResultType type) =>
        new(_ => new BrowserResult { ResultType = type });
}
```

- [ ] **Step 2: Write the failing WorkOS authorize-URL test**

In `test/Capacitor.Cli.Tests.Unit/OAuthFlowTests.cs`, add `using Duende.IdentityModel.OidcClient;` at the top, and add:
```csharp
    [Test]
    public async Task WorkOS_authorize_url_targets_api_domain_with_authkit_and_org() {
        var options = OAuthLoginFlow.BuildWorkOSOptions("client_d", "https://api.workos.com", "http://127.0.0.1:5555/callback");
        var oidc    = new OidcClient(options);

        var state = await oidc.PrepareLoginAsync(OAuthLoginFlow.WorkOSFrontChannel("org_a"));

        await Assert.That(state.StartUrl).StartsWith("https://api.workos.com/user_management/authorize");
        await Assert.That(state.StartUrl).Contains("provider=authkit");
        await Assert.That(state.StartUrl).Contains("organization_id=org_a");
        await Assert.That(state.StartUrl).Contains("code_challenge_method=S256");
        await Assert.That(options.LoadProfile).IsFalse();
    }

    [Test]
    public async Task WorkOS_authorize_url_omits_org_when_null() {
        var options = OAuthLoginFlow.BuildWorkOSOptions("client_d", "https://api.workos.com", "http://127.0.0.1:5555/callback");
        var oidc    = new OidcClient(options);

        var state = await oidc.PrepareLoginAsync(OAuthLoginFlow.WorkOSFrontChannel(null));

        await Assert.That(state.StartUrl).DoesNotContain("organization_id");
    }
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OAuthFlowTests/WorkOS_authorize_url_*"`
Expected: FAIL — `BuildWorkOSOptions`/`WorkOSFrontChannel` not defined.

- [ ] **Step 4: Implement the WorkOS option/param builders**

In `src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs`, add these usings at the top:
```csharp
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;
```
Then add (near the WorkOS region):
```csharp
    internal static OidcClientOptions BuildWorkOSOptions(string clientId, string apiBase, string redirectUri) {
        var options = new OidcClientOptions {
            Authority   = apiBase,            // anonymous-principal issuer; discovery stays off (ProviderInformation set)
            ClientId    = clientId,
            Scope       = "",                 // preserve current no-scope behavior
            RedirectUri = redirectUri,
            LoadProfile = false,              // WorkOS has no userinfo endpoint
            DisablePushedAuthorization = true,
            ProviderInformation = new ProviderInformation {
                IssuerName        = apiBase,
                AuthorizeEndpoint = $"{apiBase}/user_management/authorize",     // AI-958: always the API domain
                TokenEndpoint     = $"{apiBase}/user_management/authenticate",
            },
        };
        options.Policy.Discovery.RequireKeySet = false;

        return options;
    }

    internal static Parameters WorkOSFrontChannel(string? organizationId) {
        var p = new Parameters { { "provider", "authkit" } };
        if (!string.IsNullOrEmpty(organizationId)) p.Add("organization_id", organizationId);

        return p;
    }
```

- [ ] **Step 5: Run the URL tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OAuthFlowTests/WorkOS_authorize_url_*"`
Expected: PASS.

(If `Scope=""` causes a `scope=` param to appear or any error — the spec's verify-at-impl #2 — set `Scope` to a single space or omit by leaving it null; re-run. Document the chosen value in the commit.)

- [ ] **Step 6: Write the failing end-to-end mapping tests (org-scoped + org-less)**

In `OAuthFlowTests.cs` add:
```csharp
    [Test]
    public async Task AuthenticateWorkOS_maps_token_response_including_org_and_user() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"user":{"id":"user_x","first_name":"Ada"},"organization_id":"org_a","access_token":"acc","refresh_token":"rt"}"""));

        var result = await OAuthLoginFlow.AuthenticateWorkOSAsync(
            "client_d", "org_a", FakeBrowser.WithCode("the_code"), apiBase: server.Urls[0]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.AccessToken).IsEqualTo("acc");
        await Assert.That(result.RefreshToken).IsEqualTo("rt");
        await Assert.That(result.OrganizationId).IsEqualTo("org_a");
        await Assert.That(result.User!.FirstName).IsEqualTo("Ada");
    }

    [Test]
    public async Task AuthenticateWorkOS_handles_orgless_response_without_throwing() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"access_token":"acc","refresh_token":"rt"}"""));   // no organization_id, no user

        var result = await OAuthLoginFlow.AuthenticateWorkOSAsync(
            "client_d", organizationId: null, FakeBrowser.WithCode("the_code"), apiBase: server.Urls[0]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.OrganizationId).IsNull();
        await Assert.That(result.User).IsNull();
        await Assert.That(result.RefreshToken).IsEqualTo("rt");
    }
```

- [ ] **Step 7: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OAuthFlowTests/AuthenticateWorkOS_*"`
Expected: FAIL — `AuthenticateWorkOSAsync` not defined.

- [ ] **Step 8: Implement `AuthenticateWorkOSAsync` and rewrite the WorkOS login path**

In `OAuthLoginFlow.cs` add (using the builders from Step 4):
```csharp
    /// <summary>
    /// WorkOS AuthKit authorization-code-with-PKCE login via OidcClient. Authorize + token both
    /// on the API domain (AI-958), org-scoped when <paramref name="organizationId"/> is set.
    /// Maps the raw token response (which carries WorkOS's non-standard organization_id/user and
    /// no id_token) into <see cref="WorkOSAuthResponse"/> via the source-gen context.
    /// </summary>
    public static async Task<WorkOSAuthResponse?> AuthenticateWorkOSAsync(
            string clientId, string? organizationId, IBrowser browser, string apiBase = WorkOSApiBase) {
        var redirectUri = $"http://127.0.0.1:{GetAvailablePort()}/callback";
        var options     = BuildWorkOSOptions(clientId, apiBase, redirectUri);
        options.Browser = browser;

        var oidc   = new OidcClient(options);
        var result = await oidc.LoginAsync(new LoginRequest { FrontChannelExtraParameters = WorkOSFrontChannel(organizationId) });

        if (result.IsError || result.TokenResponse?.Json is not { } json) return null;

        return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.WorkOSAuthResponse);
    }
```
Replace the body of `HandleWorkOSLogin` + `LoginWorkOSAsync` so it no longer threads `authKitDomain`:
```csharp
    static Task<int> HandleWorkOSLogin(AuthDiscoveryResponse config) =>
        LoginWorkOSAsync(config.ClientId!, config.OrganizationId);

    static async Task<int> LoginWorkOSAsync(string clientId, string? organizationId) {
        var json = await AuthenticateWorkOSAsync(clientId, organizationId, new LoopbackBrowser());
        if (json is null) {
            Console.Error.WriteLine("Error: WorkOS sign-in failed.");

            return 1;
        }

        // Org gate: a multi-org user must not be "logged in" to the wrong org.
        if (!string.IsNullOrEmpty(organizationId) && !string.Equals(json.OrganizationId, organizationId, StringComparison.Ordinal)) {
            Console.Error.WriteLine($"Error: signed in to the wrong WorkOS organization (expected {organizationId}). Re-run `kcap login` and pick the correct organization.");

            return 1;
        }

        var username = WorkOSDisplayName(json.User);

        await TokenStore.SaveAsync(
            new() {
                AccessToken    = json.AccessToken,
                RefreshToken   = json.RefreshToken,
                ExpiresAt      = TokenStore.JwtExpiry(json.AccessToken),
                GitHubUsername = username,
                Provider       = AuthProvider.WorkOS,
                ClientId       = clientId
            });

        await Console.Out.WriteLineAsync($"Logged in as {username}");

        return 0;
    }
```
Delete `RunWorkOSLoopbackAsync`, `AuthenticateWorkOSCodeAsync`, and the `WorkOSLoopbackResult` record entirely. Keep the `const string WorkOSApiBase = "https://api.workos.com";`.

- [ ] **Step 9: Rewire `WorkOSDiscovery` to the no-`authorizeBase` helper**

In `src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs`:

Change `RunWithLiveAuthAsync` to drop the `authorizeBase` computation and pass the new helper:
```csharp
    public static Task<int> RunWithLiveAuthAsync(
            string proxyUrl, ProxyConfigResponse proxyConfig, IAuthProxyClient proxy, ITenantPicker picker) {
        var clientId = proxyConfig.WorkOSClientId ?? "";

        return RunAsync(proxyUrl, proxyConfig, proxy, picker,
            orglessLogin: () => OAuthLoginFlow.AuthenticateWorkOSAsync(clientId, organizationId: null, new LoopbackBrowser()),
            orgSwitch: async (refreshToken, organizationId) => {
                using var http = new HttpClient();
                return await OAuthLoginFlow.SwitchWorkOSOrgAsync(http, WorkOSApiBase, clientId, refreshToken, organizationId);
            });
    }
```
Change the `RunAsync` signature delegate and its single call site:
```csharp
            Func<Task<WorkOSAuthResponse?>>                 orglessLogin,   // (was Func<string, ...>)
```
and:
```csharp
        var auth = await orglessLogin();
```
(Remove the now-unused `authorizeBase` local and the `WorkOSAuthKitDomain` read.)

- [ ] **Step 10: Update `WorkOSDiscoveryTests` to the no-arg delegate**

In `test/Capacitor.Cli.Tests.Unit/WorkOSDiscoveryTests.cs`, change every `orglessLogin` lambda from `_ => Task.FromResult(...)` to `() => Task.FromResult(...)`. The four occurrences:
```csharp
            orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(orgless),
```
```csharp
            _      => Task.FromResult<WorkOSAuthResponse?>(null),     // -> () => Task.FromResult<WorkOSAuthResponse?>(null),
```
```csharp
            _      => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),   // -> () => ...
```
```csharp
            _      => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),   // -> () => ...
```
(`orgSwitch` lambdas keep their two args.)

- [ ] **Step 11: Run the WorkOS + discovery tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OAuthFlowTests/AuthenticateWorkOS_*"`
Then: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/WorkOSDiscoveryTests/*"`
Expected: PASS. (If WorkOS rejects the extra `redirect_uri` OidcClient sends on the token POST — spec verify-at-impl #1 — the WireMock tests still pass; flag it for the live-WorkOS check in Task 5.)

- [ ] **Step 12: AOT publish gate + commit**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: empty output.
```bash
git add src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs test/Capacitor.Cli.Tests.Unit/OAuthFlowTests.cs test/Capacitor.Cli.Tests.Unit/WorkOSDiscoveryTests.cs test/Capacitor.Cli.Tests.Unit/FakeBrowser.cs
git commit -m "[AI-960] WorkOS login via OidcClient; authorize on api.workos.com (AI-958)"
```

---

### Task 4: GitHub browser flow on OidcClient front-channel

Use OidcClient for PKCE + state + authorize-URL via `PrepareLoginAsync`, the shared `LoopbackBrowser`, and `AuthorizeResponse` to parse the callback; keep the custom JSON proxy code-exchange. Delete the bespoke GitHub URL/PKCE/callback helpers.

**Files:**
- Modify: `src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/OAuthFlowTests.cs`

**Interfaces:**
- Consumes: `LoopbackBrowser`, `GitHubCodeExchangeRequest`, `GitHubTokenResponse`, `OAuthLoginFlow.GetAvailablePort()`.
- Produces: `public static Task<string?> RunGitHubBrowserFlowAsync(string clientId, string codeExchangeUrl, IBrowser? browser = null, TimeSpan? timeout = null)`.
- Removes: `BuildGitHubAuthorizeUrl`, `ParseCallback`, `CallbackResult`, `RespondCallbackAsync`, `GenerateCodeVerifier`, `GenerateCodeChallenge`.

- [ ] **Step 1: Delete the now-obsolete GitHub unit tests**

In `OAuthFlowTests.cs`, delete `GitHub_authorize_url_includes_all_required_params` and all six `Callback_parser_*` tests (the methods they cover are being removed).

- [ ] **Step 2: Write the failing GitHub browser-flow tests**

In `OAuthFlowTests.cs` add:
```csharp
    [Test]
    public async Task GitHubBrowser_exchanges_code_and_returns_access_token() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/code-exchange").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"access_token":"gho_abc"}"""));

        var token = await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
            "Iv1.abc", $"{server.Urls[0]}/code-exchange", FakeBrowser.WithCode("the_code"));

        await Assert.That(token).IsEqualTo("gho_abc");
    }

    [Test]
    public async Task GitHubBrowser_returns_null_on_state_mismatch_without_calling_proxy() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/code-exchange").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"access_token":"nope"}"""));

        var token = await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
            "Iv1.abc", $"{server.Urls[0]}/code-exchange",
            FakeBrowser.WithRawQuery("?code=the_code&state=attacker"));

        await Assert.That(token).IsNull();
        // The proxy must never be hit when the CSRF state doesn't match.
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path == "/code-exchange")).IsFalse();
    }

    [Test]
    public async Task GitHubBrowser_returns_null_on_non_success_browser_result() {
        var token = await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
            "Iv1.abc", "http://unused.test/code-exchange", FakeBrowser.NonSuccess(BrowserResultType.Timeout));

        await Assert.That(token).IsNull();
    }
```
Add `using Duende.IdentityModel.OidcClient.Browser;` to the test file if not present.

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OAuthFlowTests/GitHubBrowser_*"`
Expected: FAIL — `RunGitHubBrowserFlowAsync` signature doesn't accept an `IBrowser` yet / state-mismatch path not implemented.

- [ ] **Step 4: Rewrite `RunGitHubBrowserFlowAsync`**

In `OAuthLoginFlow.cs` replace the whole `RunGitHubBrowserFlowAsync` method with:
```csharp
    /// <summary>
    /// GitHub authorization-code-with-PKCE via OidcClient's front-channel (authorize URL + PKCE +
    /// state) over a 127.0.0.1 loopback, then the proxy-mediated JSON code-exchange to the Capacitor
    /// server (GitHub Apps need client_secret on the token POST, which the server adds). Returns the
    /// GitHub access token, or <c>null</c> on cancel/timeout/state-mismatch/error — a null is a hard
    /// failure (the caller does NOT fall back to device flow on null, only on a loopback bind exception).
    /// </summary>
    public static async Task<string?> RunGitHubBrowserFlowAsync(
            string clientId, string codeExchangeUrl, IBrowser? browser = null, TimeSpan? timeout = null) {
        browser ??= new LoopbackBrowser();
        var redirectUri = $"http://127.0.0.1:{GetAvailablePort()}/callback";

        var options = new OidcClientOptions {
            Authority   = "https://github.com",
            ClientId    = clientId,
            Scope       = "read:user read:org",
            RedirectUri = redirectUri,
            LoadProfile = false,
            DisablePushedAuthorization = true,
            Browser     = browser,
            ProviderInformation = new ProviderInformation {
                IssuerName        = "https://github.com",
                AuthorizeEndpoint = "https://github.com/login/oauth/authorize",
                TokenEndpoint     = "https://github.com/login/oauth/access_token", // required non-empty; never called
            },
        };
        options.Policy.Discovery.RequireKeySet = false;

        var oidc  = new OidcClient(options);
        var state = await oidc.PrepareLoginAsync();

        var result = await browser.InvokeAsync(
            new BrowserOptions(state.StartUrl, redirectUri) { Timeout = timeout ?? TimeSpan.FromMinutes(5) });

        if (result.ResultType != BrowserResultType.Success) {
            Console.Error.WriteLine(result.ResultType == BrowserResultType.Timeout
                ? "Timed out waiting for authorization. Re-run `kcap login` to try again."
                : $"Authorization failed: {result.Error ?? result.ResultType.ToString()}");

            return null;
        }

        var resp = new AuthorizeResponse(result.Response);
        if (resp.IsError) {
            Console.Error.WriteLine($"Authorization failed: {resp.Error}");

            return null;
        }
        if (!string.Equals(resp.State, state.State, StringComparison.Ordinal)) {
            Console.Error.WriteLine("Error: state mismatch — possible CSRF. Aborting.");

            return null;
        }
        if (string.IsNullOrEmpty(resp.Code)) {
            Console.Error.WriteLine("Authorization failed: no authorization code received.");

            return null;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(new("application/json"));

        HttpResponseMessage tokenResponse;

        try {
            tokenResponse = await http.PostAsJsonAsync(
                codeExchangeUrl,
                new GitHubCodeExchangeRequest { Code = resp.Code, CodeVerifier = state.CodeVerifier, RedirectUri = redirectUri },
                CapacitorJsonContext.Default.GitHubCodeExchangeRequest);
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException) {
            Console.Error.WriteLine($"Could not reach the code-exchange endpoint at {codeExchangeUrl}: {ex.Message}");

            return null;
        }

        if (!tokenResponse.IsSuccessStatusCode) {
            Console.Error.WriteLine($"Error exchanging code: {await tokenResponse.Content.ReadAsStringAsync()}");

            return null;
        }

        GitHubTokenResponse? tokenResult;

        try {
            tokenResult = await tokenResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.GitHubTokenResponse);
        } catch (JsonException ex) {
            var raw = await tokenResponse.Content.ReadAsStringAsync();
            Console.Error.WriteLine($"Code-exchange response was not valid JSON ({ex.Message}): {raw}");

            return null;
        }

        if (tokenResult?.AccessToken is null) {
            Console.Error.WriteLine($"Error: {tokenResult?.Error ?? "no access_token in response"}");

            return null;
        }

        await Console.Out.WriteLineAsync("Authorization complete.");

        return tokenResult.AccessToken;
    }
```
Then delete `BuildGitHubAuthorizeUrl`, `ParseCallback`, the `CallbackResult` record, `RespondCallbackAsync`, `GenerateCodeVerifier`, and `GenerateCodeChallenge`. Remove now-unused usings (`System.Security.Cryptography`; keep `System.Net` only if still referenced — `HttpListener` now lives in `LoopbackBrowser`, so drop `System.Net` if nothing else uses it). Let the compiler guide the using cleanup.

- [ ] **Step 5: Run the GitHub tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OAuthFlowTests/GitHubBrowser_*"`
Expected: PASS (all three).

- [ ] **Step 6: AOT publish gate + commit**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: empty.
```bash
git add src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs test/Capacitor.Cli.Tests.Unit/OAuthFlowTests.cs
git commit -m "[AI-960] GitHub browser flow via OidcClient front-channel; keep proxy exchange"
```

---

### Task 5: Full verification sweep

Confirm the whole suite passes, the AOT publish is clean, the macOS binary still signs, and no README change is owed.

**Files:** none (verification only).

- [ ] **Step 1: Full unit + integration test run**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Then: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj`
Expected: all pass.

- [ ] **Step 2: AOT publish gate (authoritative)**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: empty output. (If anything appears, it's almost certainly from a reflection-based JSON path — route it through `CapacitorJsonContext` / OidcClient's own source-gen; do not suppress.)

- [ ] **Step 3: Re-sign the macOS AOT binary (if copied)**

If the publish output binary was copied anywhere, run `codesign --force --sign - <path-to-binary>`. (No-op if not copied.)

- [ ] **Step 4: Confirm no stray old references remain**

Run: `grep -rni "RunWorkOSLoopbackAsync\|AuthenticateWorkOSCodeAsync\|BuildGitHubAuthorizeUrl\|ParseCallback\|GenerateCodeChallenge\|WorkOSLoopbackResult\|IdentityModel.OidcClient\b" src/ test/`
Expected: no hits except the `Duende.IdentityModel.OidcClient` package/using lines.

- [ ] **Step 5: README confirmation**

Confirm `kcap login` / `kcap setup` commands, flags (`--github`, `--workos`, `--device`, `--discover`), and prerequisites are unchanged. No README edit required. (If any user-visible behavior shifted, update `README.md` quick-start + the per-command section in the same change.)

- [ ] **Step 6: Live-WorkOS verify-at-impl checks (spec items 1 & 2)**

Verify against a real WorkOS sign-in (or confirm via WorkOS docs) that: (1) the `redirect_uri` OidcClient adds to the `/user_management/authenticate` POST is accepted/ignored; (2) the `Scope` value chosen in Task 3 Step 5 produces a working authorize request. If either fails, fall WorkOS back to the manual `PrepareLoginAsync` + custom exchange pattern (mirroring GitHub) and note it.

- [ ] **Step 7: Final commit (if Step 3/5 changed anything)**

```bash
git add -A && git commit -m "[AI-960] Verification sweep: tests, AOT, codesign, docs"
```

## Notes for the implementer

- OidcClient parses a token response with **no `id_token`** fine — the code flow hardcodes `requireIdentityToken: false`. Don't add an `IdentityTokenValidator`; `LoadProfile=false` keeps it from hitting a userinfo endpoint.
- `LoginResult.TokenResponse.Json` is a `JsonElement?`; the WorkOS extra fields (`organization_id`, `user`) come from deserializing it into `WorkOSAuthResponse`, NOT `JsonElement.GetProperty` (which throws on omitted fields).
- The device flow (`RunDeviceFlowAsync`) and org-switch (`SwitchWorkOSOrgAsync`) are untouched — do not route them through OidcClient.
