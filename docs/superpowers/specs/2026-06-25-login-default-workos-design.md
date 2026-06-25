# Default CLI login to org SSO; drop the `--workos` vendor flag

## Problem

`kcap login` / `kcap setup` expose the auth vendor to users through the
`--workos` flag (`help-login.txt:27`, usage `[--github | --workos]`). Vendor
naming (WorkOS) should not leak into the user-facing CLI surface. The
interactive provider picker is already unbranded ("Continue" / "Continue with
GitHub"), so the flag is the only leak — plus a couple of descriptive "WorkOS"
mentions in help text.

The product model we want: **org SSO is the default sign-in; GitHub is the
explicit opt-in via `--github`.**

## Goals

1. Remove the `--workos` flag.
2. No-flag `kcap login` / `kcap setup` (interactive) signs in via the org SSO
   path (internally `AuthProvider.WorkOS`) directly — no provider prompt.
3. `--github` selects the GitHub App path.
4. Scrub gratuitous user-facing "WorkOS" naming from help text + README.

## Non-goals

- Auth mechanics (PKCE/loopback/OidcClient/device flow/org-switch) — untouched.
- `--device`, `--discover` — unchanged.
- The internal `AuthProvider.WorkOS = "workos"` constant — stays; it's the
  server's `/auth/config` protocol value, not user-facing.

## Design

### Decision point: `OAuthLoginFlow.ChooseDiscoveryProvider`

`src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs`. New behavior:

| `args` | `isInteractive` | Result |
|--------|-----------------|--------|
| contains `--github` | any | `AuthProvider.GitHubApp` |
| no flag | `true` | `AuthProvider.WorkOS` *(was: `null` → prompt)* |
| no flag | `false` (headless) | `AuthProvider.GitHubApp` *(unchanged — WorkOS needs a browser loopback)* |

- `--workos` is no longer recognized. A stale `--workos` is harmless: with no
  `--github`, an interactive caller lands on the WorkOS default anyway.
- Return type tightens from `string?` to `string` (never null now).
- Headless rationale stays: WorkOS uses a 127.0.0.1 browser loopback that can't
  complete without a browser, so headless no-flag falls back to GitHub device
  flow. (A headless WorkOS path never worked, so nothing is lost.)

### Remove the interactive picker

`src/Capacitor.Cli/Commands/DiscoveryProviderPrompt.cs` is **deleted** (its
Spectre "Continue / Continue with GitHub" `SelectionPrompt` is no longer
reachable — `ChooseDiscoveryProvider` always returns a concrete provider). Its
two callers call the resolver directly:

- `src/Capacitor.Cli/Program.cs:666`
- `src/Capacitor.Cli/Commands/SetupCommand.cs:410`

both become:

```csharp
var provider = OAuthLoginFlow.ChooseDiscoveryProvider(args, isInteractive: !HeadlessEnvironment.IsHeadless());
```

(`HeadlessEnvironment` is in `Capacitor.Cli.Core.Auth`.) Remove the now-unused
`using Spectre.Console;` that the deleted file carried.

### Help text — `src/Capacitor.Cli.Core/Resources/help-login.txt`

- Usage line: `kcap login [--discover] [--github] [--device]`.
- Discovery blurb: drop the "Continue / Continue with GitHub" wording; state
  that login uses your organization's single sign-on by default, and `--github`
  signs in with GitHub instead.
- Remove the `--workos` option line; reword `--github` to "Sign in with GitHub
  instead of the default."
- Reword the "the auth method — GitHub App or WorkOS — is auto-discovered"
  sentence to drop "WorkOS" (e.g. "the sign-in method is auto-discovered from
  the server").

### README — `README.md`

Update both the quick-start (`## Getting started`) and the per-command login /
setup sections (`## CLI commands`) to match: default = org SSO, `--github` for
GitHub, no `--workos`, no provider picker. Remove user-facing "WorkOS"
references where they're gratuitous (keep wording about SSO / "your
organization").

## Tests

`test/Capacitor.Cli.Tests.Unit/OAuthFlowTests.cs` —
`ChooseDiscoveryProvider_honors_flags_and_default`:

```csharp
await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider(["--github"], isInteractive: true)).IsEqualTo(AuthProvider.GitHubApp);
await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider([], isInteractive: true)).IsEqualTo(AuthProvider.WorkOS);   // default = org SSO, no prompt
await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider([], isInteractive: false)).IsEqualTo(AuthProvider.GitHubApp); // headless → GitHub device flow
```

Drop the old `--workos`-flag assertion and the `[]`+interactive→`null`
assertion. No test covers the deleted prompt.

## Verification

- `dotnet run` unit + integration suites pass.
- `dotnet publish -c Release` stays IL2026/IL3050-clean.
- Manual: `kcap login` (interactive) goes straight to SSO; `kcap login --github`
  uses GitHub; `--help` shows no `--workos`.

## Files touched

- `src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs` — `ChooseDiscoveryProvider`.
- `src/Capacitor.Cli/Commands/DiscoveryProviderPrompt.cs` — delete.
- `src/Capacitor.Cli/Program.cs` — call resolver directly.
- `src/Capacitor.Cli/Commands/SetupCommand.cs` — call resolver directly.
- `src/Capacitor.Cli.Core/Resources/help-login.txt` — flags + wording.
- `README.md` — getting-started + login/setup sections.
- `test/Capacitor.Cli.Tests.Unit/OAuthFlowTests.cs` — default assertion.
