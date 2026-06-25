# Default CLI login to org SSO — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make plain `kcap login`/`kcap setup` default to the org SSO path and drop the vendor-revealing `--workos` flag; `--github` is the opt-in for the GitHub App path.

**Architecture:** `OAuthLoginFlow.ChooseDiscoveryProvider` becomes the single decision point and always returns a concrete provider (no prompt). The interactive `DiscoveryProviderPrompt` picker is deleted and its two callers call the resolver directly. Help text + README drop `--workos` and gratuitous "WorkOS" naming.

**Tech Stack:** .NET 10 NativeAOT, TUnit, Spectre.Console (CLI), no new dependencies.

## Global Constraints

- AOT: after changes, `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` MUST be empty.
- README sync (CLAUDE.md): any user-facing CLI surface change updates `README.md` in the same change — both quick-start (`## Getting started`) and the per-command login/setup section.
- TUnit: filter with `--treenode-filter`; run tests via `dotnet run --project <test csproj>`.
- The internal `AuthProvider.WorkOS = "workos"` constant stays (server `/auth/config` protocol value); only the user-facing `--workos` flag + gratuitous "WorkOS" copy are removed.

**Spec:** `docs/superpowers/specs/2026-06-25-login-default-workos-design.md`

---

### Task 1: `ChooseDiscoveryProvider` defaults to WorkOS (no prompt signal)

**Files:**
- Modify: `src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs:59-67`
- Test: `test/Capacitor.Cli.Tests.Unit/OAuthFlowTests.cs` (`ChooseDiscoveryProvider_honors_flags_and_default`)

**Interfaces:**
- Produces: `internal static string ChooseDiscoveryProvider(string[] args, bool isInteractive)` — non-nullable; `--github` → `GitHubApp`; no flag + interactive → `WorkOS`; no flag + headless → `GitHubApp`.

- [ ] **Step 1: Update the test to the new contract**

In `test/Capacitor.Cli.Tests.Unit/OAuthFlowTests.cs`, replace the body of `ChooseDiscoveryProvider_honors_flags_and_default` with:
```csharp
    [Test]
    public async Task ChooseDiscoveryProvider_honors_flags_and_default() {
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider(["--github"], isInteractive: true)).IsEqualTo(AuthProvider.GitHubApp);
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider([], isInteractive: true)).IsEqualTo(AuthProvider.WorkOS);   // default = org SSO, no prompt
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider([], isInteractive: false)).IsEqualTo(AuthProvider.GitHubApp); // headless → GitHub device flow
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OAuthFlowTests/ChooseDiscoveryProvider_honors_flags_and_default"`
Expected: FAIL — `[]`+interactive currently returns `null`, not `AuthProvider.WorkOS`.

- [ ] **Step 3: Update `ChooseDiscoveryProvider`**

In `src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs`, replace the method (currently lines 54-67) with:
```csharp
    /// <summary>
    /// Picks the discovery provider before any auth runs: <c>--github</c> selects the GitHub App
    /// path; otherwise login defaults to the org SSO path (WorkOS). Headless callers can't run the
    /// WorkOS 127.0.0.1 browser loopback, so a no-flag headless caller falls back to GitHub (whose
    /// device flow works without a local browser).
    /// </summary>
    internal static string ChooseDiscoveryProvider(string[] args, bool isInteractive) {
        if (args.Contains("--github")) return AuthProvider.GitHubApp;

        return isInteractive ? AuthProvider.WorkOS : AuthProvider.GitHubApp;
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OAuthFlowTests/ChooseDiscoveryProvider_honors_flags_and_default"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs test/Capacitor.Cli.Tests.Unit/OAuthFlowTests.cs
git commit -m "Default login to org SSO; --github opts into GitHub (drop prompt signal)"
```

---

### Task 2: Delete the interactive picker; rewire callers

**Files:**
- Delete: `src/Capacitor.Cli/Commands/DiscoveryProviderPrompt.cs`
- Modify: `src/Capacitor.Cli/Program.cs:666`
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs:410`

**Interfaces:**
- Consumes: `OAuthLoginFlow.ChooseDiscoveryProvider(string[], bool)` (Task 1), `HeadlessEnvironment.IsHeadless()` — both in `Capacitor.Cli.Core.Auth` (already imported in both files).

- [ ] **Step 1: Delete the picker**

```bash
git rm src/Capacitor.Cli/Commands/DiscoveryProviderPrompt.cs
```

- [ ] **Step 2: Rewire the login caller**

In `src/Capacitor.Cli/Program.cs`, replace:
```csharp
    var provider = DiscoveryProviderPrompt.Resolve(args);
```
with:
```csharp
    var provider = OAuthLoginFlow.ChooseDiscoveryProvider(args, isInteractive: !HeadlessEnvironment.IsHeadless());
```

- [ ] **Step 3: Rewire the setup caller**

In `src/Capacitor.Cli/Commands/SetupCommand.cs`, replace:
```csharp
        var provider = DiscoveryProviderPrompt.Resolve(args);
```
with:
```csharp
        var provider = OAuthLoginFlow.ChooseDiscoveryProvider(args, isInteractive: !HeadlessEnvironment.IsHeadless());
```

- [ ] **Step 4: Build to verify wiring + no stale references**

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
Expected: Build succeeded.
Run: `grep -rn "DiscoveryProviderPrompt" src/ test/`
Expected: no hits.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Remove interactive provider picker; callers resolve provider directly"
```

---

### Task 3: Help text + README — drop `--workos`, scrub vendor naming

**Files:**
- Modify: `src/Capacitor.Cli.Core/Resources/help-login.txt`
- Modify: `README.md`

- [ ] **Step 1: Rewrite `help-login.txt`**

Replace the entire contents of `src/Capacitor.Cli.Core/Resources/help-login.txt` with:
```
kcap login — Authenticate via OAuth

Usage: kcap login [--discover] [--github] [--device]

Opens the system browser for OAuth authentication.

With no configured server (or with --discover), kcap runs tenant discovery:
it signs you in with your organization's single sign-on, then lets you pick
from the tenants you belong to. No --server-url and no existing profile are
required. Pass --github to sign in with GitHub instead. With a server already
configured, kcap logs into it directly (the sign-in method is auto-discovered
from the server's /auth/config).

For GitHub, the default flow uses a localhost callback with PKCE when the
server advertises a code-exchange endpoint — the browser opens straight to
the authorize page and the CLI receives the code on a loopback listener.
The CLI falls back to GitHub Device Flow when the server doesn't advertise
that endpoint, in headless environments (SSH, no DISPLAY), or when the
loopback port can't be bound.

Options:
  --discover    Force tenant discovery even when a server is configured.
                Discovery signs you in, lists the tenants you belong to, and
                saves the chosen one as the active profile.

  --github      Sign in with GitHub instead of your organization's SSO.

  --device      Force GitHub Device Flow even when a browser is available.
                Useful for SSH sessions, containers without a browser, or any
                environment where the loopback callback can't open.
```

- [ ] **Step 2: Update README quick-start (`## Getting started`)**

In `README.md`, replace line 62:
```
1. **Server** — with no `--server-url`/`<tenant>`, kcap **discovers** your tenant: pick how to sign in (**Continue** for email/SSO, or **Continue with GitHub**), authenticate once, then choose from the tenants you belong to. A bare `<tenant>` slug expands to `https://<tenant>.kcap.ai`; a full URL is used as-is.
```
with:
```
1. **Server** — with no `--server-url`/`<tenant>`, kcap **discovers** your tenant: it signs you in with your organization's single sign-on (pass `--github` to use GitHub instead), then lets you choose from the tenants you belong to. A bare `<tenant>` slug expands to `https://<tenant>.kcap.ai`; a full URL is used as-is.
```
and replace line 63:
```
2. **Login** — authenticates via your tenant's provider (WorkOS or GitHub App); discovery completes the sign-in inline
```
with:
```
2. **Login** — authenticates via your tenant's configured sign-in method; discovery completes the sign-in inline
```

- [ ] **Step 3: Update README per-command setup/login section**

In `README.md`, replace line 143:
```
With no server argument, setup (and `kcap login`) runs **tenant discovery**: you choose how to sign in — **Continue** (WorkOS email/SSO) or **Continue with GitHub** — then pick from the tenants you belong to. `--github`/`--workos` skip the provider prompt; `--discover` forces discovery even when a server is configured.
```
with:
```
With no server argument, setup (and `kcap login`) runs **tenant discovery**: it signs you in with your organization's single sign-on, then lets you pick from the tenants you belong to. Pass `--github` to sign in with GitHub instead; `--discover` forces discovery even when a server is configured.
```

- [ ] **Step 4: Verify no user-facing `--workos` or stray vendor leak remains**

Run: `grep -rn -- "--workos" src/Capacitor.Cli.Core/Resources/ README.md`
Expected: no hits.
Run: `grep -rni "Continue with GitHub\|WorkOS email/SSO" src/Capacitor.Cli.Core/Resources/ README.md`
Expected: no hits.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Resources/help-login.txt README.md
git commit -m "Docs: default login to org SSO, drop --workos, scrub vendor naming"
```

---

### Task 4: Verification sweep

**Files:** none (verification only).

- [ ] **Step 1: Full unit + integration suites**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj`
Expected: all pass (`failed: 0`).

- [ ] **Step 2: AOT publish gate**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: empty output.

- [ ] **Step 3: `--help` shows no `--workos`**

Run: `dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- login --help 2>&1 | grep -- "--workos" || echo "clean: no --workos in help"`
Expected: `clean: no --workos in help`.

- [ ] **Step 4: No stray code references**

Run: `grep -rn --include=*.cs "DiscoveryProviderPrompt\|\"--workos\"" src/ test/`
Expected: no hits.

## Notes for the implementer

- `Program.cs` and `SetupCommand.cs` already `using Capacitor.Cli.Core.Auth;`, so `OAuthLoginFlow` and `HeadlessEnvironment` resolve without new imports. Deleting `DiscoveryProviderPrompt.cs` removes the only use of its `using Spectre.Console;` — nothing else to clean up there.
- A stale `--workos` passed by a user is now an unrecognized no-op: with no `--github`, an interactive caller still lands on the WorkOS default, so old muscle-memory keeps working.
- Do not touch the `AuthProvider.WorkOS`/`GitHubApp` constants or any auth mechanics.
