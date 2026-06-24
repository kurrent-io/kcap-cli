# AI-960: Adopt Duende.IdentityModel.OidcClient for CLI OAuth flows

## Problem

Every OAuth flow in the CLI is hand-rolled in
`src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs`: PKCE generation
(`GenerateCodeVerifier`/`GenerateCodeChallenge`), authorize-URL construction
(`BuildGitHubAuthorizeUrl` and the inline WorkOS URL), the `HttpListener`
loopback (duplicated between the GitHub and WorkOS browser flows), callback
parsing (`ParseCallback`), and the code→token exchanges
(`RunWorkOSLoopbackAsync` + `AuthenticateWorkOSCodeAsync`,
`RunGitHubBrowserFlowAsync`).

A dependency on `IdentityModel.OidcClient` 6.0.0 already exists — but it is
**completely unused** (`grep` finds zero `OidcClient`/`IdentityModel` usages in
`src/`), it is the **old pre-Duende** package, and the reference sits in the
**wrong project** (`Capacitor.Cli.csproj`) while all the auth code lives in
`Capacitor.Cli.Core` (which today has no package references at all). It was
added and never wired in.

Goal: replace the bespoke authorization-code-with-PKCE + loopback +
code-exchange machinery with the audited
**`Duende.IdentityModel.OidcClient` 7.1.0**, keeping the genuinely
provider-specific bits custom.

## Goals / non-goals

**Goals**
1. Adopt `Duende.IdentityModel.OidcClient` 7.1.0 for the WorkOS and GitHub
   browser authorization-code-with-PKCE flows.
2. Collapse the two duplicated `HttpListener` loopbacks into one
   `IBrowser` implementation.
3. Fold in AI-958 (CLI part): WorkOS authorize URL **always** on
   `api.workos.com`, never `authKitDomain`.
4. Stay IL2026/IL3050-clean under `dotnet publish -c Release`.

**Non-goals**
- GitHub **device flow** (`RunDeviceFlowAsync`) — OidcClient has no device
  grant; stays custom.
- WorkOS **org-switch** (`SwitchWorkOSOrgAsync`) — WorkOS-specific refresh
  grant + `organization_id`; stays custom.
- The GitHub App **proxy code-exchange** contract — proxy-mediated JSON to our
  own server; stays custom (OidcClient owns only the front-channel for GitHub).
- kcap-server WorkOS files named in AI-958 — out of scope for this repo.

## Confirmed library facts (from DuendeSoftware/foss `main`)

- **AOT**: `Duende.IdentityModel.OidcClient` 7.1.0 targets `net10.0` with
  `IsTrimmable=true` / `IsAotCompatible=true`; deps are only
  `Duende.IdentityModel 8.1.0` + `Microsoft.Extensions.Logging.Abstractions
  10.0.4`. No `System.IdentityModel.Tokens.Jwt` reflection chain. It uses its
  own `SourceGenerationContext` for `System.Text.Json`.
- **No `id_token` required**: `ResponseProcessor.ProcessCodeFlowResponseAsync`
  calls `ValidateTokenResponseAsync(..., requireIdentityToken: false, ...)`.
  A token response carrying only `access_token`/`refresh_token` (WorkOS's exact
  shape) validates fine; `User` becomes an anonymous `Principal`. The raw
  `LoginResult.TokenResponse` is exposed ("additional custom response data that
  clients need access to"), so WorkOS's extra `organization_id`/`user` fields
  remain readable via `TokenResponse.Json` (a `JsonElement`, AOT-safe).
- **Injectable seams**: `IBrowser` (loopback), `OidcClientOptions.Browser`,
  `BackchannelHandler`/`HttpClientFactory` (point token endpoint at WireMock),
  `PrepareLoginAsync`/`ProcessResponseAsync`, and `AuthorizeResponse(raw)`
  (callback parser) are all public.
- **Manual endpoints**: set `OidcClientOptions.ProviderInformation` with
  `IssuerName` (required, non-empty), `AuthorizeEndpoint` (required),
  `TokenEndpoint` (required); `KeySet` may be null when
  `Policy.Discovery.RequireKeySet = false`. Setting `ProviderInformation`
  disables discovery (no `.well-known` fetch).

## Package changes

In `Directory.Packages.props`:
- Remove `PackageVersion Include="IdentityModel.OidcClient" Version="6.0.0"`.
- Add `PackageVersion Include="Duende.IdentityModel.OidcClient" Version="7.1.0"`.
- Add `PackageVersion Include="Duende.IdentityModel" Version="8.1.0"` (used
  directly for `AuthorizeResponse`/`Parameters`; also the transitive dep).

Project references:
- Remove `PackageReference Include="IdentityModel.OidcClient"` from
  `src/Capacitor.Cli/Capacitor.Cli.csproj`.
- Add `Duende.IdentityModel.OidcClient` + `Duende.IdentityModel`
  `PackageReference`s to **`src/Capacitor.Cli.Core/Capacitor.Cli.Core.csproj`**
  (the project that owns the auth code; `Capacitor.Cli` already references it).

## Component design

### 1. `LoopbackBrowser : IBrowser` (new — `Core/Auth/LoopbackBrowser.cs`)

Single implementation of the 127.0.0.1 loopback, replacing both inline
`HttpListener` blocks.

```csharp
public sealed class LoopbackBrowser : IBrowser {
    public Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken ct = default);
}
```

Behavior (lifted from the existing flows, de-duplicated):
- Parse the port from `options.EndUrl` (the redirect URI); bind
  `http://127.0.0.1:{port}/`.
- `Process.Start` the system browser to `options.StartUrl` (best-effort;
  swallow failures — headless has no browser). Print the "if it didn't open,
  visit: {StartUrl}" line.
- Loop `GetContextAsync` with `options.Timeout`; ignore non-`/callback`
  requests (favicon etc.) with 404; on cancellation return
  `BrowserResult{ResultType = Timeout}`.
- On `/callback`: write the success HTML, return
  `BrowserResult{ResultType = Success, Response = <raw query string>}`.
  (CSRF state validation is OidcClient's job in `ProcessResponseAsync`; for the
  GitHub manual path we validate `AuthorizeResponse.State` ourselves.)

Port allocation stays the caller's responsibility (keep `GetAvailablePort`) so
`RedirectUri` is fixed before `LoginAsync`. Same brief alloc→bind TOCTOU window
as today — no regression.

### 2. WorkOS — full OidcClient `LoginAsync`

Rewrite `LoginWorkOSAsync` and add a reusable
`AuthenticateWorkOSAsync(clientId, organizationId, IBrowser, HttpMessageHandler?)`
→ `WorkOSAuthResponse?` helper that builds:

```csharp
var options = new OidcClientOptions {
    ClientId    = clientId,
    Scope       = "",                          // preserve current no-scope behavior
    RedirectUri = $"http://127.0.0.1:{port}/callback",
    Browser     = loopback,
    LoadProfile = false,                       // no userinfo endpoint
    DisablePushedAuthorization = true,         // WorkOS has no PAR
    ProviderInformation = new ProviderInformation {
        IssuerName        = "https://api.workos.com",
        AuthorizeEndpoint = "https://api.workos.com/user_management/authorize",   // AI-958
        TokenEndpoint     = "https://api.workos.com/user_management/authenticate",
    },
};
options.Policy.Discovery.RequireKeySet = false;
if (backchannelHandler is not null) options.BackchannelHandler = backchannelHandler; // tests
```

Front-channel extra params via `LoginRequest.FrontChannelExtraParameters`:
`provider=authkit` (+ `organization_id` when non-null).

Map `LoginResult` → existing `WorkOSAuthResponse`:
- `AccessToken` / `RefreshToken` from `LoginResult`.
- `OrganizationId` and `User` read from `LoginResult.TokenResponse.Json`
  (`GetProperty("organization_id")`, `GetProperty("user")`), so the existing
  org-gate and `WorkOSDisplayName` logic is unchanged.

`HandleWorkOSLogin` keeps the org-gate, token save, and "Logged in as …"
output. **Deletes** `RunWorkOSLoopbackAsync`, `AuthenticateWorkOSCodeAsync`,
and the WorkOS `WorkOSLoopbackResult` record.

### 3. GitHub browser — OidcClient front-channel, custom proxy exchange

`RunGitHubBrowserFlowAsync` becomes:
1. Build `OidcClientOptions` with `AuthorizeEndpoint =
   https://github.com/login/oauth/authorize`, `Scope = "read:user read:org"`,
   `TokenEndpoint = https://github.com/login/oauth/access_token` (set but never
   called — `ProviderInformation` requires it non-empty), `IssuerName =
   "https://github.com"`, the shared `LoopbackBrowser`.
2. `var state = await oidc.PrepareLoginAsync();` → `StartUrl`, `State`,
   `CodeVerifier`, `RedirectUri`.
3. `var result = await browser.InvokeAsync(new BrowserOptions(state.StartUrl,
   state.RedirectUri){ Timeout = ... });`
4. `var resp = new AuthorizeResponse(result.Response);` — check `resp.IsError`,
   `resp.State == state.State` (CSRF), get `resp.Code`.
5. Keep the existing JSON proxy exchange to `codeExchangeUrl`
   (`GitHubCodeExchangeRequest{Code, CodeVerifier = state.CodeVerifier,
   RedirectUri = state.RedirectUri}` → `GitHubTokenResponse`), unchanged.

**Deletes** `BuildGitHubAuthorizeUrl`, `ParseCallback`, `CallbackResult`,
`RespondCallbackAsync` (HTML now written by `LoopbackBrowser`),
`GenerateCodeVerifier`, `GenerateCodeChallenge`, and the GitHub `HttpListener`
block. `AcquireGitHubTokenAsync`'s browser→device fallback (catching loopback
bind failures) is preserved; the bind now throws from inside
`LoopbackBrowser.InvokeAsync`, so the `catch (HttpListenerException)` /
`catch (PlatformNotSupportedException)` stays at the `AcquireGitHubTokenAsync`
call site.

### 4. AI-958 fold-in (plumbing removal)

Because WorkOS authorize is now constant `api.workos.com`, the `authKitDomain`
→ `authorizeBase` plumbing is dead:
- `HandleWorkOSLogin`/`LoginWorkOSAsync` drop the `authKitDomain` parameter.
- `WorkOSDiscovery.RunAsync` drops the `authorizeBase` computation, and its
  `orglessLogin` delegate changes from `Func<string, Task<WorkOSAuthResponse?>>`
  to `Func<Task<WorkOSAuthResponse?>>`.
- `WorkOSDiscovery.RunWithLiveAuthAsync` calls the new
  `AuthenticateWorkOSAsync` helper (no `authorizeBase`).

`config.AuthKitDomain` / `proxyConfig.WorkOSAuthKitDomain` stay in the DTOs
(server still sends them; logout may use them) but no longer drive authorize.

### Stays custom (unchanged)

`RunDeviceFlowAsync`, `SwitchWorkOSOrgAsync`, `ExchangeAndSaveAsync` (all
overloads), `ChooseGitHubFlow`, `ChooseDiscoveryProvider`, `ShouldDiscoverLogin`,
`IsValidExchangeUrl`, `WorkOSDisplayName`, `TryParseInstallationMessage`,
`WriteExchangeError`, `GetAvailablePort`, the org-gate and `TokenStore` saves.

## Error handling

- WorkOS: a `LoginResult.IsError` maps to the existing
  "WorkOS token exchange failed" / timeout / state-mismatch messaging
  (OidcClient surfaces `"Invalid state."`, `"Missing authorization code."`,
  `"Timeout"` from the browser result — translate to the current user-facing
  strings).
- GitHub: `resp.IsError` / state mismatch → existing "Authorization failed"
  path; unreachable/invalid proxy exchange → existing messaging.
- Loopback bind failure → `HttpListenerException`/`PlatformNotSupportedException`
  propagates to `AcquireGitHubTokenAsync` → device-flow fallback (unchanged).

## Testing strategy

Delete (methods gone): `GitHub_authorize_url_includes_all_required_params`,
all `Callback_parser_*` tests.

Keep: `SwitchWorkOSOrg_*`, `IsValidExchangeUrl_*`, `ChooseGitHubFlow_*`,
`ChooseDiscoveryProvider_*`, `ShouldDiscoverLogin_*`.

Add:
- WorkOS options builder test: asserts `AuthorizeEndpoint`/`TokenEndpoint` are
  `api.workos.com`, `provider=authkit` present, `organization_id` present only
  when supplied, `LoadProfile == false`. (Authorize URL is verified by driving
  `PrepareLoginAsync` and inspecting `AuthorizeState.StartUrl`.)
- WorkOS end-to-end mapping test: a fake `IBrowser` returns a canned
  `?code=...&state=<state>` callback; a WireMock token endpoint returns
  `{access_token, refresh_token, organization_id, user}` (no `id_token`);
  assert the new helper yields a `WorkOSAuthResponse` with the right
  `OrganizationId`/`RefreshToken`/`User`. Wire WireMock via
  `OidcClientOptions.BackchannelHandler`.
- `WorkOSDiscoveryTests`: update the `orglessLogin` fake to the no-arg delegate
  signature.

Plus the mandatory AOT gate:
`dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
must be empty.

## Verify-at-implementation

1. **`redirect_uri` on WorkOS token call**: our hand-rolled exchange omits it;
   OidcClient always sends it. WorkOS `/user_management/authenticate` should
   accept/ignore it, but confirm against a real WorkOS exchange (or docs)
   before merge. If WorkOS rejects it, fall back to the manual
   `PrepareLoginAsync` + custom exchange pattern (same as GitHub) for WorkOS.
2. **Empty `Scope`**: confirm OidcClient omits `scope` from the authorize URL
   when `Scope == ""` (preserving current behavior). If it emits `scope=`,
   decide whether WorkOS tolerates it or set a minimal scope.

## Files touched

- `Directory.Packages.props` — swap package versions.
- `src/Capacitor.Cli/Capacitor.Cli.csproj` — remove stray ref.
- `src/Capacitor.Cli.Core/Capacitor.Cli.Core.csproj` — add OidcClient refs.
- `src/Capacitor.Cli.Core/Auth/LoopbackBrowser.cs` — new.
- `src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs` — rewrite WorkOS + GitHub
  browser flows; delete bespoke helpers.
- `src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs` — drop `authorizeBase`
  plumbing; call the new WorkOS helper.
- `test/Capacitor.Cli.Tests.Unit/OAuthFlowTests.cs` — delete obsolete tests,
  add option-builder + e2e-mapping tests.
- `test/Capacitor.Cli.Tests.Unit/WorkOSDiscoveryTests.cs` — delegate signature.
- `README.md` — no user-facing CLI surface change expected (same commands,
  flags, prerequisites); confirm no edit needed.
