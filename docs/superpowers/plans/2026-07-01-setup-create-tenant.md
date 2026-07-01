# `kcap setup` Create-Tenant Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When `kcap setup`'s WorkOS discovery finds zero tenants, offer to create one inline — prompt for org name + slug, provision via kcap-web, poll until live, then continue setup against the new tenant.

**Architecture:** Two repos. **kcap-web** (`/Users/alexey/dev/eventstore/kcap-web`) gains a bearer-token auth path on its existing `/api/signup/*` endpoints (validate the CLI's WorkOS access-token JWT via JWKS, resolve the user, reuse `runProvision`). **kcap-cli** (this worktree) gets a `TenantProvisioningClient`, an injected `ITenantProvisioner` in `WorkOSDiscovery.RunAsync`, and an interactive `SpectreTenantProvisioner` wired into `kcap setup` only.

**Tech Stack:** kcap-web — Astro + Cloudflare Workers, TypeScript, Vitest, `@workos-inc/node`, `jose` (new). kcap-cli — .NET 10 NativeAOT, TUnit, WireMock.Net, NSubstitute, Spectre.Console.

**Spec:** `docs/superpowers/specs/2026-07-01-setup-create-tenant-design.md`

## Global Constraints

- **Repo split:** kcap-web tasks (A*) run in `/Users/alexey/dev/eventstore/kcap-web`. kcap-cli tasks (B*) run in this worktree. Commit in the respective repo.
- **kcap-cli is NativeAOT:** no IL3050/IL2026 warnings. All JSON goes through the source-gen `CapacitorJsonContext` (`src/Capacitor.Cli.Core/Models.cs`). Never use `JsonArray` collection-expressions; never `RegexOptions.Compiled` — use `[GeneratedRegex]`. Verify with `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` (must be empty).
- **JSON wire names are camelCase, but `CapacitorJsonContext` is globally `SnakeCaseLower`** (`Models.cs:725`). Every CLI request/response record MUST carry explicit `[JsonPropertyName("...")]` on every field, or it serializes as snake_case and kcap-web rejects it (`provision.ts:131`).
- **Endpoints:** provisioning base URL `https://capacitor.kurrent.io` (override `KCAP_SIGNUP_URL`); tenant domain `kcap.ai`; auth proxy `https://auth.kcap.ai`.
- **Client-id invariant (load-bearing):** kcap-web `WORKOS_CLIENT_ID` must equal the auth-proxy WorkOS client id (both `client_01KTVSZN2HXGP8NFH0S0HA8PAV`). Bearer JWKS verification depends on it; document it at the verification site.
- **Slug rules (verbatim from `kcap-web/src/server/tenants/slug.ts`):** pattern `[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){0,39}`; reserved set `www, auth, api, admin, app, dashboard, status, static, cdn, mail, kcap, kurrent, capacitor, internal, support, help, docs, blog, assets, console`.
- **Tier:** always `"free"`. **Headless:** interactive-only (no provisioner passed when headless). **`kcap login --discover` is unchanged** (passes no provisioner).
- **kcap-cli test run:** `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` (single test: append `-- --treenode-filter "*Name*"`). **kcap-web test run:** `npm test` (from the kcap-web dir).
- **README sync (kcap-cli CLAUDE.md rule):** any user-facing CLI surface change updates `README.md` in the same PR — quick-start + the `setup` command section — not just help text.

---

## Part A — kcap-web backend

All Part A steps run in `/Users/alexey/dev/eventstore/kcap-web`.

### Task A1: WorkOS access-token verification module

**Files:**
- Create: `src/server/auth/workos-token.ts`
- Test: `test/workos-token.test.ts`
- Modify: `package.json` (add `jose`)

**Interfaces:**
- Produces: `verifyWorkosAccessToken(token: string, clientId: string): Promise<string | null>` — returns the JWT `sub` (WorkOS user id) on a valid signature + unexpired token, else `null`.

- [ ] **Step 1: Add the `jose` dependency**

Run (in `/Users/alexey/dev/eventstore/kcap-web`):
```bash
npm install jose
```
Expected: `jose` appears under `dependencies` in `package.json`.

- [ ] **Step 2: Write the failing test**

Create `test/workos-token.test.ts`:
```typescript
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { generateKeyPair, exportJWK, SignJWT, type KeyLike } from "jose";
import { verifyWorkosAccessToken } from "../src/server/auth/workos-token";

let publicJwk: any;
let privateKey: KeyLike;
let fetchSpy: ReturnType<typeof vi.spyOn>;

// Unique client id per test avoids the module-level JWKS cache serving a stale
// keyset from a previous test (each test mints a fresh keypair).
let clientCounter = 0;
function freshClientId() { return `client_test_${++clientCounter}`; }

async function signToken(sub: string, expSeconds: string): Promise<string> {
  return new SignJWT({})
    .setProtectedHeader({ alg: "RS256", kid: "test-kid" })
    .setSubject(sub)
    .setIssuedAt()
    .setExpirationTime(expSeconds)
    .sign(privateKey);
}

beforeEach(async () => {
  const kp = await generateKeyPair("RS256");
  privateKey = kp.privateKey;
  publicJwk = await exportJWK(kp.publicKey);
  publicJwk.kid = "test-kid";
  publicJwk.alg = "RS256";
  publicJwk.use = "sig";
  fetchSpy = vi.spyOn(globalThis, "fetch").mockImplementation(async (input) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : (input as Request).url;
    if (url.includes("/sso/jwks/")) {
      return new Response(JSON.stringify({ keys: [publicJwk] }), { status: 200, headers: { "content-type": "application/json" } });
    }
    return new Response("{}", { status: 200 });
  });
});
afterEach(() => fetchSpy.mockRestore());

describe("verifyWorkosAccessToken", () => {
  it("returns the sub for a valid, unexpired token", async () => {
    const token = await signToken("user_42", "1h");
    expect(await verifyWorkosAccessToken(token, freshClientId())).toBe("user_42");
  });

  it("returns null for an expired token", async () => {
    const token = await signToken("user_42", "-1s");
    expect(await verifyWorkosAccessToken(token, freshClientId())).toBeNull();
  });

  it("returns null for a garbage token", async () => {
    expect(await verifyWorkosAccessToken("not.a.jwt", freshClientId())).toBeNull();
  });

  it("returns null when the token has no sub", async () => {
    const token = await new SignJWT({})
      .setProtectedHeader({ alg: "RS256", kid: "test-kid" })
      .setIssuedAt().setExpirationTime("1h").sign(privateKey);
    expect(await verifyWorkosAccessToken(token, freshClientId())).toBeNull();
  });
});
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `npm test -- workos-token`
Expected: FAIL — cannot find module `../src/server/auth/workos-token`.

- [ ] **Step 4: Implement the module**

Create `src/server/auth/workos-token.ts`:
```typescript
import { createRemoteJWKSet, jwtVerify } from "jose";

// One JWKS keyset per client id (jose caches keys + handles rotation internally).
const jwksByClient = new Map<string, ReturnType<typeof createRemoteJWKSet>>();

function jwksFor(clientId: string) {
  let j = jwksByClient.get(clientId);
  if (!j) {
    j = createRemoteJWKSet(new URL(`https://api.workos.com/sso/jwks/${clientId}`));
    jwksByClient.set(clientId, j);
  }
  return j;
}

// Verify a WorkOS access-token JWT (signature + lifetime) and return its `sub`
// (WorkOS user id), or null if the token is invalid/expired/unsigned.
//
// Issuer/audience validation is intentionally skipped, mirroring how the
// kcap-server auth proxy validates these exact tokens (DiscoverTenantsWorkOSEndpoint).
//
// LOAD-BEARING INVARIANT: `clientId` is kcap-web's WORKOS_CLIENT_ID, which MUST
// equal the auth-proxy WorkOS client id that minted the CLI's token — JWKS is
// scoped per client id. Both are the shared production AuthKit Application today.
export async function verifyWorkosAccessToken(token: string, clientId: string): Promise<string | null> {
  try {
    const { payload } = await jwtVerify(token, jwksFor(clientId));
    return typeof payload.sub === "string" && payload.sub.length > 0 ? payload.sub : null;
  } catch {
    return null;
  }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `npm test -- workos-token`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add package.json package-lock.json src/server/auth/workos-token.ts test/workos-token.test.ts
git commit -m "feat(signup): add WorkOS access-token JWKS verification helper"
```

---

### Task A2: Bearer-first branch in `requireVerifiedUser`

**Files:**
- Modify: `src/server/auth/session.ts`
- Test: `test/session-bearer.test.ts`

**Interfaces:**
- Consumes: `verifyWorkosAccessToken` (Task A1), `getWorkOS(env).userManagement.getUser(id)`.
- Produces: `authenticateBearer(request, env): Promise<SessionResult | null>` and a bearer-first `requireVerifiedUser`. `SessionResult`/`VerifiedUser` shapes are unchanged (`session.ts:6-9`).

- [ ] **Step 1: Write the failing test**

Create `test/session-bearer.test.ts`:
```typescript
import { describe, it, expect, vi, beforeEach } from "vitest";

const getUser = vi.fn();
const loadSealedSession = vi.fn();
vi.mock("../src/server/workos/client", () => ({
  getWorkOS: () => ({ userManagement: { getUser, loadSealedSession } }),
}));

const verifyWorkosAccessToken = vi.fn();
vi.mock("../src/server/auth/workos-token", () => ({ verifyWorkosAccessToken }));

import { requireVerifiedUser } from "../src/server/auth/session";

const env = { WORKOS_API_KEY: "k", WORKOS_CLIENT_ID: "c", WORKOS_COOKIE_PASSWORD: "p".repeat(32) } as any;

function reqWithBearer(token?: string) {
  return new Request("https://capacitor.kurrent.io/api/signup/provision", {
    method: "POST",
    headers: token ? { authorization: `Bearer ${token}` } : {},
  });
}

beforeEach(() => { getUser.mockReset(); loadSealedSession.mockReset(); verifyWorkosAccessToken.mockReset(); });

describe("requireVerifiedUser (bearer path)", () => {
  it("resolves the user from a valid bearer token", async () => {
    verifyWorkosAccessToken.mockResolvedValue("user_1");
    getUser.mockResolvedValue({ id: "user_1", email: "a@b.co", emailVerified: true, firstName: "Ada", lastName: "Lovelace" });
    const r = await requireVerifiedUser(reqWithBearer("tok"), env);
    expect(r).toEqual({ ok: true, user: { id: "user_1", email: "a@b.co", firstName: "Ada", lastName: "Lovelace" } });
  });

  it("401 when the bearer token fails verification", async () => {
    verifyWorkosAccessToken.mockResolvedValue(null);
    const r = await requireVerifiedUser(reqWithBearer("bad"), env);
    expect(r).toEqual({ ok: false, status: 401 });
    expect(getUser).not.toHaveBeenCalled();
  });

  it("403 when the bearer user's email is unverified", async () => {
    verifyWorkosAccessToken.mockResolvedValue("user_1");
    getUser.mockResolvedValue({ id: "user_1", email: "a@b.co", emailVerified: false });
    const r = await requireVerifiedUser(reqWithBearer("tok"), env);
    expect(r).toEqual({ ok: false, status: 403 });
  });

  it("falls through to the cookie path when there is no Authorization header", async () => {
    // No bearer -> authenticateBearer returns null -> cookie path runs. No cookie -> 401.
    const r = await requireVerifiedUser(reqWithBearer(undefined), env);
    expect(r).toEqual({ ok: false, status: 401 });
    expect(verifyWorkosAccessToken).not.toHaveBeenCalled();
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npm test -- session-bearer`
Expected: FAIL — bearer requests currently hit the cookie path (no `authenticateBearer`), so the first test returns `{ ok:false, status:401 }` instead of the user.

- [ ] **Step 3: Implement the bearer branch**

In `src/server/auth/session.ts`, add the import at the top (next to the existing `getWorkOS` import):
```typescript
import { verifyWorkosAccessToken } from "./workos-token";
```

Add `authenticateBearer` (place it directly above `requireVerifiedUser`):
```typescript
function readBearer(request: Request): string | null {
  const h = request.headers.get("authorization");
  if (!h) return null;
  const m = /^Bearer\s+(.+)$/i.exec(h.trim());
  return m ? m[1].trim() : null;
}

// Bearer auth for the CLI: validate a WorkOS access-token JWT (see workos-token.ts),
// then resolve the full user (email/name/verified) via the WorkOS management API.
// Returns null when there is no bearer token so the caller falls back to the cookie path.
export async function authenticateBearer(request: Request, env: Env): Promise<SessionResult | null> {
  const token = readBearer(request);
  if (!token) return null;

  const sub = await verifyWorkosAccessToken(token, env.WORKOS_CLIENT_ID);
  if (!sub) return { ok: false, status: 401 };

  const workos = getWorkOS(env);
  try {
    const user = await workos.userManagement.getUser(sub);
    return verified(user);
  } catch {
    return { ok: false, status: 401 };
  }
}
```

Change the top of `requireVerifiedUser` to try bearer first:
```typescript
export async function requireVerifiedUser(request: Request, env: Env): Promise<SessionResult> {
  const bearer = await authenticateBearer(request, env);
  if (bearer) return bearer;

  const sessionData = readCookie(request, COOKIE);
  // ...rest of the existing cookie logic unchanged...
```

(`verified()` already maps `{ id, email, emailVerified, firstName?, lastName? }` → `SessionResult` and 403s on unverified email — reuse it verbatim.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `npm test -- session`
Expected: PASS — both `session-bearer` (4) and the existing `session` tests (cookie path unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/server/auth/session.ts test/session-bearer.test.ts
git commit -m "feat(signup): accept a WorkOS bearer token in requireVerifiedUser"
```

---

### Task A3: Return `workosOrgId` on active provision/status responses

**Files:**
- Modify: `src/server/signup/provision.ts:15-19,81-83`
- Modify: `src/server/signup/status.ts:82-84`
- Test: `test/provision.test.ts`, `test/status.test.ts` (add cases)

**Interfaces:**
- Produces: `POST /api/signup/provision` `200` body and `GET /api/signup/status` active body now include `workosOrgId: string` (the WorkOS org id). The CLI (Task B3/B4) reads it to org-switch.

- [ ] **Step 1: Write the failing tests**

First read `test/provision.test.ts` and `test/status.test.ts` to see how each already seeds a tenant row and how `runProvision`/`handleStatus` are invoked (the session mock, the `env` fixture from `cloudflare:test`, `resetTenantsTable()`). Seed an **active** row using the real `d1.*` helpers those files already import from `../src/server/tenants/d1` — `d1.reserveTenant(env.DB, {...})` → `d1.setWorkosOrg(env.DB, "acme", "org_live", <nowIso>)` → `d1.markActive(env.DB, "acme", <nowIso>)` — owned by the caller (`ownerUserId: "user_1"`). Match the exact `reserveTenant` argument shape used elsewhere in `provision.test.ts` (it passes `{ slug, orgName, ownerUserId, ownerEmail, tier, trialStart, trialEndsAt, trialGuardKey, now }`).

In `test/provision.test.ts`, add (adapting the seeding lines to that file's helpers):
```typescript
it("active provision response includes workosOrgId", async () => {
  // ...seed an active row for slug "acme", owner "user_1", workos_org_id "org_live"...
  const res = await runProvision(env, { id: "user_1", email: "a@b.co" }, { orgName: "Acme", slug: "acme", tier: "free" }, fakeWorkos());
  expect(res.status).toBe(200);
  expect(res.body).toEqual({ slug: "acme", state: "active", url: "https://acme.kcap.ai", workosOrgId: "org_live" });
});
```

In `test/status.test.ts`, add (reusing that file's existing session mock that authenticates as `user_1`):
```typescript
it("active status response includes workosOrgId", async () => {
  // ...seed the same active row as above...
  const req = new Request("https://capacitor.kurrent.io/api/signup/status?slug=acme");
  const res = await handleStatus(req, env, undefined);
  expect(res.status).toBe(200);
  expect(await res.json()).toEqual({ state: "active", url: "https://acme.kcap.ai", workosOrgId: "org_live" });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `npm test -- provision status`
Expected: FAIL — responses lack `workosOrgId` (`toEqual` mismatch).

- [ ] **Step 3: Implement — provision.ts**

Update the `ProvisionResult` union (line 17):
```typescript
  | { status: 200; body: { slug: string; state: "active"; url: string; workosOrgId: string } }
```
Update the active return (line 81-83):
```typescript
  if (row.state === "active") {
    // active rows always carry a workos_org_id (set during provisioning); "" is a defensive floor.
    return { status: 200, body: { slug, state: "active", url: tenantUrl(env, slug), workosOrgId: row.workos_org_id ?? "" } };
  }
```

- [ ] **Step 4: Implement — status.ts**

Update the body construction (line 82-84):
```typescript
  const body: { state: TenantState; url?: string; workosOrgId?: string } = { state };
  if (state === "active") {
    body.url = `https://${slug}.${env.TENANT_BASE_DOMAIN}`;
    body.workosOrgId = row.workos_org_id ?? "";
  }
  return withSession(json(200, body), sess.refreshedCookie);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `npm test -- provision status`
Expected: PASS (including the two new cases and all pre-existing ones).

- [ ] **Step 6: Commit**

```bash
git add src/server/signup/provision.ts src/server/signup/status.ts test/provision.test.ts test/status.test.ts
git commit -m "feat(signup): include workosOrgId on active provision/status responses"
```

---

## Part B — kcap-cli

All Part B steps run in this worktree (`/Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/bubbly-munching-reddy`).

### Task B1: `ProvisioningEndpoint` + `SlugValidator`

**Files:**
- Create: `src/Capacitor.Cli.Core/Auth/ProvisioningEndpoint.cs`
- Create: `src/Capacitor.Cli.Core/Auth/SlugValidator.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/SlugValidatorTests.cs`

**Interfaces:**
- Produces: `ProvisioningEndpoint.Url` (string). `SlugValidator.Canonicalize(string)`, `SlugValidator.Validate(string) → SlugCheck`, `SlugValidator.Derive(string) → string`. `SlugCheck` = `readonly record struct SlugCheck(bool Ok, string? Reason)` where `Reason ∈ {"invalid","blocked"}` or null.

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/SlugValidatorTests.cs`:
```csharp
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit;

public class SlugValidatorTests {
    [Test]
    [Arguments("acme", true, null)]
    [Arguments("a", true, null)]
    [Arguments("ab-cd", true, null)]
    [Arguments("-acme", false, "invalid")]
    [Arguments("acme-", false, "invalid")]
    [Arguments("ac--me", false, "invalid")]
    [Arguments("Acme", false, "invalid")]        // uppercase (validate expects canonical)
    [Arguments("kcap", false, "blocked")]
    [Arguments("api", false, "blocked")]
    public async Task Validate_classifies_slug(string slug, bool ok, string? reason) {
        var check = SlugValidator.Validate(slug);
        await Assert.That(check.Ok).IsEqualTo(ok);
        await Assert.That(check.Reason).IsEqualTo(reason);
    }

    [Test]
    [Arguments("Acme Inc",          "acme-inc")]
    [Arguments("  Hello, World!  ", "hello-world")]
    [Arguments("Café Déjà",         "cafe-deja")]
    [Arguments("multi   space",     "multi-space")]
    [Arguments("--dashes--",        "dashes")]
    public async Task Derive_produces_a_canonical_slug(string orgName, string expected) {
        await Assert.That(SlugValidator.Derive(orgName)).IsEqualTo(expected);
    }

    [Test]
    public async Task Derive_truncates_to_40_chars() {
        var derived = SlugValidator.Derive(new string('a', 60));
        await Assert.That(derived.Length).IsEqualTo(40);
    }

    [Test]
    public async Task Url_defaults_to_capacitor_kurrent_io() {
        await Assert.That(ProvisioningEndpoint.DefaultUrl).IsEqualTo("https://capacitor.kurrent.io");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "*SlugValidatorTests*"`
Expected: FAIL — `SlugValidator` / `ProvisioningEndpoint` do not exist (compile error).

- [ ] **Step 3: Implement `ProvisioningEndpoint`**

Create `src/Capacitor.Cli.Core/Auth/ProvisioningEndpoint.cs`:
```csharp
namespace Capacitor.Cli.Core.Auth;

public static class ProvisioningEndpoint {
    public const string DefaultUrl = "https://capacitor.kurrent.io";

    // KCAP_SIGNUP_URL is an internal dev/preview override; not documented for end users.
    public static string Url =>
        (Environment.GetEnvironmentVariable("KCAP_SIGNUP_URL") ?? DefaultUrl).TrimEnd('/');
}
```

- [ ] **Step 4: Implement `SlugValidator`**

Create `src/Capacitor.Cli.Core/Auth/SlugValidator.cs`:
```csharp
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Auth;

public readonly record struct SlugCheck(bool Ok, string? Reason);

// Mirrors kcap-web/src/server/tenants/slug.ts. The server re-validates; this is
// for instant CLI feedback before any network call.
public static partial class SlugValidator {
    // Same charset rule as SLUG_PATTERN: lowercase DNS label, <=40, no leading/
    // trailing/double hyphen. [GeneratedRegex] keeps it AOT-safe.
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){0,39}$")]
    private static partial Regex SlugRegex();

    static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) {
        "www", "auth", "api", "admin", "app", "dashboard", "status", "static",
        "cdn", "mail", "kcap", "kurrent", "capacitor", "internal", "support",
        "help", "docs", "blog", "assets", "console",
    };

    public static string Canonicalize(string input) => input.Trim().ToLowerInvariant();

    public static SlugCheck Validate(string canonical) {
        if (!SlugRegex().IsMatch(canonical)) return new(false, "invalid");
        if (Reserved.Contains(canonical))    return new(false, "blocked");
        return new(true, null);
    }

    // Best-effort default slug from an org name: strip diacritics, lowercase,
    // non-alphanumeric -> '-', collapse repeats, trim hyphens, cap at 40.
    public static string Derive(string orgName) {
        var decomposed = orgName.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed) {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        var lowered = sb.ToString();

        var outSb = new StringBuilder(lowered.Length);
        var pendingHyphen = false;
        foreach (var ch in lowered) {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9')) {
                if (pendingHyphen && outSb.Length > 0) outSb.Append('-');
                pendingHyphen = false;
                outSb.Append(ch);
            } else {
                pendingHyphen = true; // collapse any run of non-alphanumerics into one hyphen
            }
        }
        var slug = outSb.ToString();
        return slug.Length > 40 ? slug[..40] : slug;
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "*SlugValidatorTests*"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/Auth/ProvisioningEndpoint.cs src/Capacitor.Cli.Core/Auth/SlugValidator.cs test/Capacitor.Cli.Tests.Unit/SlugValidatorTests.cs
git commit -m "feat(setup): add ProvisioningEndpoint + slug validator/deriver"
```

---

### Task B2: Provisioning wire records + JSON context registration

**Files:**
- Create: `src/Capacitor.Cli.Core/Auth/ProvisioningModels.cs`
- Modify: `src/Capacitor.Cli.Core/Models.cs` (register types in `CapacitorJsonContext`)
- Test: `test/Capacitor.Cli.Tests.Unit/ProvisioningWireTests.cs`

**Interfaces:**
- Produces records (all `[JsonPropertyName]`-annotated): `ProvisionRequest { OrgName, Slug, Tier }`, `ProvisionResponse { Slug?, State?, Url?, WorkosOrgId?, Reason? }`, `AvailabilityResponse { Available, Reason? }`, `StatusResponse { State?, Url?, WorkosOrgId? }`. Registered as `CapacitorJsonContext.Default.<Type>`.

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/ProvisioningWireTests.cs`:
```csharp
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit;

public class ProvisioningWireTests {
    [Test]
    public async Task ProvisionRequest_serializes_camelCase_not_snake_case() {
        var json = JsonSerializer.Serialize(
            new ProvisionRequest { OrgName = "Acme Inc", Slug = "acme", Tier = "free" },
            CapacitorJsonContext.Default.ProvisionRequest);

        await Assert.That(json).Contains(@"""orgName""");
        await Assert.That(json).Contains(@"""slug""");
        await Assert.That(json).Contains(@"""tier""");
        await Assert.That(json).DoesNotContain("org_name");
    }

    [Test]
    public async Task ProvisionResponse_deserializes_camelCase_active_body() {
        var body = """{"slug":"acme","state":"active","url":"https://acme.kcap.ai","workosOrgId":"org_live"}""";
        var resp = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.ProvisionResponse)!;

        await Assert.That(resp.State).IsEqualTo("active");
        await Assert.That(resp.Url).IsEqualTo("https://acme.kcap.ai");
        await Assert.That(resp.WorkosOrgId).IsEqualTo("org_live");
    }

    [Test]
    public async Task AvailabilityResponse_deserializes_reason() {
        var resp = JsonSerializer.Deserialize(
            """{"available":false,"reason":"taken"}""",
            CapacitorJsonContext.Default.AvailabilityResponse)!;

        await Assert.That(resp.Available).IsFalse();
        await Assert.That(resp.Reason).IsEqualTo("taken");
    }

    [Test]
    public async Task StatusResponse_deserializes_camelCase_workosOrgId() {
        var resp = JsonSerializer.Deserialize(
            """{"state":"active","url":"https://acme.kcap.ai","workosOrgId":"org_live"}""",
            CapacitorJsonContext.Default.StatusResponse)!;

        await Assert.That(resp.State).IsEqualTo("active");
        await Assert.That(resp.WorkosOrgId).IsEqualTo("org_live");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "*ProvisioningWireTests*"`
Expected: FAIL — the records / context entries don't exist (compile error).

- [ ] **Step 3: Implement the records**

Create `src/Capacitor.Cli.Core/Auth/ProvisioningModels.cs`:
```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Auth;

// Wire contract for kcap-web /api/signup/*. CapacitorJsonContext is globally
// SnakeCaseLower, so EVERY field needs an explicit camelCase [JsonPropertyName]
// or the request serializes as snake_case and kcap-web rejects it (400 invalid).

// POST /api/signup/provision request
public sealed record ProvisionRequest {
    [JsonPropertyName("orgName")] public required string OrgName { get; init; }
    [JsonPropertyName("slug")]    public required string Slug    { get; init; }
    [JsonPropertyName("tier")]    public string          Tier    { get; init; } = "free";
}

// POST /api/signup/provision response (202/200/400/409 bodies unioned; fields optional)
public sealed record ProvisionResponse {
    [JsonPropertyName("slug")]        public string? Slug        { get; init; }
    [JsonPropertyName("state")]       public string? State       { get; init; }
    [JsonPropertyName("url")]         public string? Url         { get; init; }
    [JsonPropertyName("workosOrgId")] public string? WorkosOrgId { get; init; }
    [JsonPropertyName("reason")]      public string? Reason      { get; init; }
}

// GET /api/signup/availability response
public sealed record AvailabilityResponse {
    [JsonPropertyName("available")] public bool    Available { get; init; }
    [JsonPropertyName("reason")]    public string? Reason    { get; init; }
}

// GET /api/signup/status response
public sealed record StatusResponse {
    [JsonPropertyName("state")]       public string? State       { get; init; }
    [JsonPropertyName("url")]         public string? Url         { get; init; }
    [JsonPropertyName("workosOrgId")] public string? WorkosOrgId { get; init; }
}
```

- [ ] **Step 4: Register the records in the JSON context**

In `src/Capacitor.Cli.Core/Models.cs`, add these lines with the other `[JsonSerializable(...)]` attributes immediately above the `[JsonSourceGenerationOptions(...)]` block (around line 717-724):
```csharp
[JsonSerializable(typeof(Auth.ProvisionRequest))]
[JsonSerializable(typeof(Auth.ProvisionResponse))]
[JsonSerializable(typeof(Auth.AvailabilityResponse))]
[JsonSerializable(typeof(Auth.StatusResponse))]
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "*ProvisioningWireTests*"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/Auth/ProvisioningModels.cs src/Capacitor.Cli.Core/Models.cs test/Capacitor.Cli.Tests.Unit/ProvisioningWireTests.cs
git commit -m "feat(setup): add provisioning wire records + JSON context registration"
```

---

### Task B3: `TenantProvisioningClient` (HTTP)

**Files:**
- Create: `src/Capacitor.Cli.Core/Auth/TenantProvisioningClient.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/TenantProvisioningClientTests.cs`

**Interfaces:**
- Consumes: `ProvisionRequest`/`ProvisionResponse`/`AvailabilityResponse`/`StatusResponse` (Task B2), `CapacitorJsonContext`.
- Produces:
  - `CheckAvailabilityAsync(string baseUrl, string token, string slug, CancellationToken ct) → Task<AvailabilityResponse?>`
  - `ProvisionAsync(string baseUrl, string token, string orgName, string slug, CancellationToken ct) → Task<ProvisionOutcome>` where `ProvisionOutcome(int StatusCode, ProvisionResponse? Body)`
  - `GetStatusAsync(string baseUrl, string token, string slug, CancellationToken ct) → Task<StatusResponse?>`
  - `sealed record ProvisionOutcome(int StatusCode, ProvisionResponse? Body)` (not serialized; no JSON context entry).

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/TenantProvisioningClientTests.cs`:
```csharp
using System.Net.Http;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class TenantProvisioningClientTests {
    [Test]
    public async Task ProvisionAsync_sends_bearer_and_camelCase_body_and_parses_202() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/provision").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202)
                .WithBody("""{"slug":"acme","state":"provisioning"}""")
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var outcome = await client.ProvisionAsync(server.Urls[0], "tok", "Acme Inc", "acme", CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(202);
        await Assert.That(outcome.Body!.State).IsEqualTo("provisioning");

        var log = server.FindLogEntries(Request.Create().WithPath("/api/signup/provision").UsingPost());
        await Assert.That(log.Count).IsEqualTo(1);
        var req = log[0].RequestMessage;
        await Assert.That(req.Headers!["Authorization"][0]).IsEqualTo("Bearer tok");
        var body = JsonNode.Parse(req.Body!)!;
        await Assert.That(body["orgName"]!.GetValue<string>()).IsEqualTo("Acme Inc");
        await Assert.That(body["slug"]!.GetValue<string>()).IsEqualTo("acme");
        await Assert.That(body["tier"]!.GetValue<string>()).IsEqualTo("free");
    }

    [Test]
    public async Task ProvisionAsync_parses_409_reason() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/provision").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409)
                .WithBody("""{"reason":"taken"}""").WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var outcome = await client.ProvisionAsync(server.Urls[0], "tok", "Acme", "acme", CancellationToken.None);
        await Assert.That(outcome.StatusCode).IsEqualTo(409);
        await Assert.That(outcome.Body!.Reason).IsEqualTo("taken");
    }

    [Test]
    public async Task CheckAvailabilityAsync_parses_reason() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/availability").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"available":false,"reason":"reserved"}""").WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var avail = await client.CheckAvailabilityAsync(server.Urls[0], "tok", "acme", CancellationToken.None);
        await Assert.That(avail!.Available).IsFalse();
        await Assert.That(avail.Reason).IsEqualTo("reserved");
    }

    [Test]
    public async Task GetStatusAsync_parses_active_with_workosOrgId() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"state":"active","url":"https://acme.kcap.ai","workosOrgId":"org_live"}""")
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var status = await client.GetStatusAsync(server.Urls[0], "tok", "acme", CancellationToken.None);
        await Assert.That(status!.State).IsEqualTo("active");
        await Assert.That(status.WorkosOrgId).IsEqualTo("org_live");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "*TenantProvisioningClientTests*"`
Expected: FAIL — `TenantProvisioningClient` does not exist (compile error).

- [ ] **Step 3: Implement the client**

Create `src/Capacitor.Cli.Core/Auth/TenantProvisioningClient.cs`:
```csharp
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Auth;

// Result of a provision POST: HTTP status + parsed body (202 provisioning /
// 200 active / 400 / 409). Not serialized — no JSON context entry.
public sealed record ProvisionOutcome(int StatusCode, ProvisionResponse? Body);

// Talks to kcap-web /api/signup/* with a WorkOS bearer access token.
public sealed class TenantProvisioningClient(HttpClient http) {
    public async Task<AvailabilityResponse?> CheckAvailabilityAsync(
            string baseUrl, string token, string slug, CancellationToken ct) {
        using var req = Get($"{baseUrl}/api/signup/availability?slug={Uri.EscapeDataString(slug)}", token);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.AvailabilityResponse, ct);
    }

    public async Task<ProvisionOutcome> ProvisionAsync(
            string baseUrl, string token, string orgName, string slug, CancellationToken ct) {
        var payload = JsonSerializer.Serialize(
            new ProvisionRequest { OrgName = orgName, Slug = slug, Tier = "free" },
            CapacitorJsonContext.Default.ProvisionRequest);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/signup/provision") {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new("Bearer", token);

        using var resp = await http.SendAsync(req, ct);
        ProvisionResponse? body = null;
        try { body = await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ProvisionResponse, ct); }
        catch (JsonException) { /* empty/non-JSON body — leave null */ }
        return new((int)resp.StatusCode, body);
    }

    public async Task<StatusResponse?> GetStatusAsync(
            string baseUrl, string token, string slug, CancellationToken ct) {
        using var req = Get($"{baseUrl}/api/signup/status?slug={Uri.EscapeDataString(slug)}", token);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.StatusResponse, ct);
    }

    static HttpRequestMessage Get(string url, string token) {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", token);
        return req;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "*TenantProvisioningClientTests*"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Auth/TenantProvisioningClient.cs test/Capacitor.Cli.Tests.Unit/TenantProvisioningClientTests.cs
git commit -m "feat(setup): add TenantProvisioningClient for kcap-web signup API"
```

---

### Task B4: `ITenantProvisioner` + `WorkOSDiscovery` wiring

**Files:**
- Create: `src/Capacitor.Cli.Core/Auth/ITenantProvisioner.cs`
- Modify: `src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs` (extract `SwitchAndSaveAsync`, add `provisioner` param, replace zero-tenant branch)
- Test: `test/Capacitor.Cli.Tests.Unit/WorkOSDiscoveryTests.cs` (add cases)

**Interfaces:**
- Produces: `interface ITenantProvisioner { Task<ProvisionOffer> OfferCreateAsync(string workosAccessToken, CancellationToken ct = default); }`; `enum ProvisionOfferStatus { Created, Declined, InProgress, Failed }`; `sealed record ProvisionedTenant(string OrganizationId, string Slug, string DisplayName, string Origin)`; `sealed record ProvisionOffer(ProvisionOfferStatus Status, ProvisionedTenant? Tenant)` with statics `Created(t)`, `Declined`, `InProgress`, `Failed`.
- Consumes: existing `WorkOSDiscovery.RunAsync` / `RunWithLiveAuthAsync` signatures (add optional `ITenantProvisioner? provisioner = null`).

- [ ] **Step 1: Create the abstraction types**

Create `src/Capacitor.Cli.Core/Auth/ITenantProvisioner.cs`:
```csharp
namespace Capacitor.Cli.Core.Auth;

public enum ProvisionOfferStatus { Created, Declined, InProgress, Failed }

public sealed record ProvisionedTenant(
    string OrganizationId, string Slug, string DisplayName, string Origin);

// Result of offering to create a tenant. The provisioner OWNS all user-facing
// messaging for Declined/InProgress/Failed; the caller must not print a second,
// conflicting message (e.g. the legacy "ask your admin" dead-end).
public sealed record ProvisionOffer(ProvisionOfferStatus Status, ProvisionedTenant? Tenant) {
    public static ProvisionOffer Created(ProvisionedTenant t) => new(ProvisionOfferStatus.Created, t);
    public static readonly ProvisionOffer Declined   = new(ProvisionOfferStatus.Declined,   null);
    public static readonly ProvisionOffer InProgress = new(ProvisionOfferStatus.InProgress, null);
    public static readonly ProvisionOffer Failed     = new(ProvisionOfferStatus.Failed,     null);
}

public interface ITenantProvisioner {
    // Interactive: prompt -> provision -> poll. Returns Created (with the tenant)
    // on success; Declined/InProgress/Failed otherwise.
    Task<ProvisionOffer> OfferCreateAsync(string workosAccessToken, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing tests**

In `test/Capacitor.Cli.Tests.Unit/WorkOSDiscoveryTests.cs`, add these tests to the class (they use the existing `Cleanup`, `TokensDir`, NSubstitute patterns):
```csharp
[Test]
public async Task RunAsync_provisions_when_no_tenants_and_provisioner_creates() {
    var proxyConfig = new ProxyConfigResponse { WorkOSClientId = "client_d" };
    var proxy = Substitute.For<IAuthProxyClient>();
    proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
         .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

    var provisioner = Substitute.For<ITenantProvisioner>();
    provisioner.OfferCreateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(ProvisionOffer.Created(
                   new ProvisionedTenant("org_new", "acme", "Acme Inc", "https://acme.kcap.ai"))));

    var orgless  = new WorkOSAuthResponse { User = new() { Id = "user_x", FirstName = "Ada" }, AccessToken = "acc", RefreshToken = "rt" };
    var switched = new WorkOSAuthResponse { User = new() { Id = "user_x" }, OrganizationId = "org_new", AccessToken = "acc2", RefreshToken = "rt2" };

    var exit = await WorkOSDiscovery.RunAsync(
        "https://auth.kcap.ai", proxyConfig, proxy, Substitute.For<ITenantPicker>(),
        orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(orgless),
        orgSwitch:    (_, _) => Task.FromResult<WorkOSAuthResponse?>(switched),
        provisioner:  provisioner);

    await Assert.That(exit).IsEqualTo(0);

    var stored = await TokenStore.LoadAsync("acme");
    await Assert.That(stored).IsNotNull();
    await Assert.That(stored!.AccessToken).IsEqualTo("acc2");

    var cfg = await AppConfig.LoadProfileConfig();
    await Assert.That(cfg.ActiveProfile).IsEqualTo("acme");
    await Assert.That(cfg.Profiles["acme"].ServerUrl).IsEqualTo("https://acme.kcap.ai");
}

[Test]
public async Task RunAsync_returns_1_without_legacy_error_when_provisioner_declines() {
    var proxy = Substitute.For<IAuthProxyClient>();
    proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
         .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

    var provisioner = Substitute.For<ITenantProvisioner>();
    provisioner.OfferCreateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(ProvisionOffer.Declined));

    var switchCalled = false;
    var exit = await WorkOSDiscovery.RunAsync(
        "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
        proxy, Substitute.For<ITenantPicker>(),
        ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
        (_, _) => { switchCalled = true; return Task.FromResult<WorkOSAuthResponse?>(null); },
        provisioner: provisioner);

    await Assert.That(exit).IsEqualTo(1);
    await Assert.That(switchCalled).IsFalse();
}
```
(The pre-existing `RunAsync_errors_when_no_tenants` test — which passes NO provisioner — must still pass, proving the headless/no-provisioner path keeps the legacy error + exit 1.)

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "*WorkOSDiscoveryTests*"`
Expected: FAIL — `RunAsync` has no `provisioner` parameter (compile error).

- [ ] **Step 4: Extract `SwitchAndSaveAsync` and add the provisioner param**

In `src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs`, change the `RunAsync` signature to add the trailing optional parameter:
```csharp
    public static async Task<int> RunAsync(
            string                                          proxyUrl,
            ProxyConfigResponse                             proxyConfig,
            IAuthProxyClient                                proxy,
            ITenantPicker                                   picker,
            Func<Task<WorkOSAuthResponse?>>                 orglessLogin,
            Func<string, string, Task<WorkOSAuthResponse?>> orgSwitch,     // args: refreshToken, organizationId
            ITenantProvisioner?                             provisioner = null) {
```

Replace the current tenant-selection block (from `if (result.Tenants.Length == 0) { ... }` through the final `return 0;` at the end of the method — `WorkOSDiscovery.cs:65-112`) with:
```csharp
        if (result.Tenants.Length == 0) {
            if (provisioner is null) {
                await Console.Error.WriteLineAsync("No Capacitor tenants are linked to your account. Ask your admin to invite you.");
                return 1;
            }

            var offer = await provisioner.OfferCreateAsync(auth.AccessToken);
            if (offer.Status != ProvisionOfferStatus.Created || offer.Tenant is null) {
                // Declined / InProgress / Failed — the provisioner already printed the
                // outcome-appropriate message; don't stack the legacy dead-end on top.
                return 1;
            }

            var created = new DiscoveredTenant {
                Provider       = AuthProvider.WorkOS,
                OrganizationId = offer.Tenant.OrganizationId,
                Slug           = offer.Tenant.Slug,
                DisplayName    = offer.Tenant.DisplayName,
                Origin         = offer.Tenant.Origin
            };
            return await SwitchAndSaveAsync(created, [created], auth, proxyConfig.WorkOSClientId!, orgSwitch);
        }

        var picked = result.Tenants.Length == 1 ? result.Tenants[0] : picker.Pick(result.Tenants);
        if (picked is null) {
            await Console.Error.WriteLineAsync("No tenant selected.");
            return 1;
        }

        return await SwitchAndSaveAsync(picked, result.Tenants, auth, proxyConfig.WorkOSClientId!, orgSwitch);
    }

    // Org-switch into the chosen tenant, persist its profile + org-bound tokens.
    // Shared by the picked-tenant path and the freshly-provisioned-tenant path.
    static async Task<int> SwitchAndSaveAsync(
            DiscoveredTenant                                picked,
            DiscoveredTenant[]                              tenants,
            WorkOSAuthResponse                              auth,
            string                                          clientId,
            Func<string, string, Task<WorkOSAuthResponse?>> orgSwitch) {
        if (string.IsNullOrEmpty(picked.OrganizationId)) {
            await Console.Error.WriteLineAsync($"Tenant {picked.Label} is missing an organization id; cannot complete sign-in.");
            return 1;
        }

        // Org-switch once into the chosen org. The resulting refresh token stays org-bound
        // (spike-confirmed), so later refreshes need no organization_id.
        var switched = await orgSwitch(auth.RefreshToken!, picked.OrganizationId);
        if (switched is null) {
            await Console.Error.WriteLineAsync($"Could not switch to organization {picked.Label}.");
            return 1;
        }

        var username = OAuthLoginFlow.WorkOSDisplayName(auth.User);

        var cfg = await AppConfig.LoadProfileConfig();
        cfg = TenantDiscovery.MergeProfiles(cfg, tenants, picked);
        await AppConfig.SaveProfileConfig(cfg);

        await TokenStore.SaveAsync(
            picked.ProfileName,
            new StoredTokens {
                AccessToken    = switched.AccessToken,
                RefreshToken   = switched.RefreshToken,
                ExpiresAt      = TokenStore.JwtExpiry(switched.AccessToken),
                GitHubUsername = username,
                Provider       = AuthProvider.WorkOS,
                ClientId       = clientId
            });

        await Console.Out.WriteLineAsync($"Logged in as {username} → {picked.Label}");
        return 0;
    }
```

Then thread the parameter through `RunWithLiveAuthAsync`:
```csharp
    public static Task<int> RunWithLiveAuthAsync(
            string proxyUrl, ProxyConfigResponse proxyConfig, IAuthProxyClient proxy, ITenantPicker picker,
            ITenantProvisioner? provisioner = null) {
        var clientId = proxyConfig.WorkOSClientId ?? "";

        return RunAsync(proxyUrl, proxyConfig, proxy, picker,
            orglessLogin: () => OAuthLoginFlow.AuthenticateWorkOSAsync(clientId, organizationId: null, new LoopbackBrowser()),
            orgSwitch: async (refreshToken, organizationId) => {
                using var http = new HttpClient();
                return await OAuthLoginFlow.SwitchWorkOSOrgAsync(http, WorkOSApiBase, clientId, refreshToken, organizationId);
            },
            provisioner: provisioner);
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "*WorkOSDiscoveryTests*"`
Expected: PASS — the 2 new tests plus all 4 pre-existing WorkOSDiscovery tests (picked-tenant path and no-provisioner legacy path unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/Auth/ITenantProvisioner.cs src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs test/Capacitor.Cli.Tests.Unit/WorkOSDiscoveryTests.cs
git commit -m "feat(setup): offer tenant creation in WorkOS discovery when none exist"
```

---

### Task B5: `SpectreTenantProvisioner` + `kcap setup` wiring

**Files:**
- Create: `src/Capacitor.Cli/Commands/SpectreTenantProvisioner.cs`
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs:424-425` (RunDiscoveryAsync)
- Test: manual verification (interactive Spectre prompts are not unit-tested in this repo; logic lives in the already-tested `SlugValidator` + `TenantProvisioningClient`).

**Interfaces:**
- Consumes: `ITenantProvisioner` (B4), `TenantProvisioningClient` (B3), `ProvisioningEndpoint` + `SlugValidator` (B1), `ProvisionOffer`/`ProvisionedTenant` (B4).

- [ ] **Step 1: Implement `SpectreTenantProvisioner`**

Create `src/Capacitor.Cli/Commands/SpectreTenantProvisioner.cs`:
```csharp
using Capacitor.Cli.Core.Auth;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

// Interactive create-a-tenant flow for `kcap setup` when WorkOS discovery finds
// none. Prompts, provisions via kcap-web, polls until live. OWNS all user-facing
// messaging for its non-Created outcomes.
public sealed class SpectreTenantProvisioner(TenantProvisioningClient client, string baseUrl) : ITenantProvisioner {
    const int PollIntervalMs = 4000;
    const int MaxPolls       = 150; // ~10 minutes (server budget is 15)

    public async Task<ProvisionOffer> OfferCreateAsync(string workosAccessToken, CancellationToken ct = default) {
        AnsiConsole.MarkupLine("  [yellow]No Capacitor tenant is linked to your account.[/]");
        var create = AnsiConsole.Prompt(new ConfirmationPrompt("  Create one now?") { DefaultValue = true });
        if (!create) {
            AnsiConsole.MarkupLine("  [dim]No tenant created.[/]");
            return ProvisionOffer.Declined;
        }

        var orgName = AnsiConsole.Prompt(
            new TextPrompt<string>("  Organization name:").Validate(n =>
                string.IsNullOrWhiteSpace(n) ? ValidationResult.Error("Enter a name") : ValidationResult.Success()));

        var slug = await PromptSlugAsync(orgName, workosAccessToken, ct);
        if (slug is null) return ProvisionOffer.Declined;

        var origin = $"https://{slug}.kcap.ai";
        var confirm = AnsiConsole.Prompt(
            new ConfirmationPrompt($"  Create tenant [cyan]{Markup.Escape(orgName)}[/] at [cyan]{origin}[/]?") { DefaultValue = true });
        if (!confirm) {
            AnsiConsole.MarkupLine("  [dim]No tenant created.[/]");
            return ProvisionOffer.Declined;
        }

        var outcome = await client.ProvisionAsync(baseUrl, workosAccessToken, orgName, slug, ct);
        switch (outcome.StatusCode) {
            case 200 when outcome.Body?.WorkosOrgId is { Length: > 0 } orgId:
                return ProvisionOffer.Created(new ProvisionedTenant(orgId, slug, orgName, outcome.Body.Url ?? origin));
            case 202 or 200:
                return await PollAsync(workosAccessToken, slug, orgName, origin, ct);
            case 400:
                AnsiConsole.MarkupLine($"  [red]✗[/] {Reason400(outcome.Body?.Reason)}");
                return ProvisionOffer.Failed;
            case 409:
                AnsiConsole.MarkupLine($"  [red]✗[/] {Reason409(outcome.Body?.Reason, slug)}");
                return ProvisionOffer.Failed;
            default:
                AnsiConsole.MarkupLine($"  [red]✗[/] Provisioning failed (HTTP {outcome.StatusCode}). Try again later.");
                return ProvisionOffer.Failed;
        }
    }

    async Task<string?> PromptSlugAsync(string orgName, string token, CancellationToken ct) {
        var suggestion = SlugValidator.Derive(orgName);
        while (true) {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>("  Workspace URL slug:")
                    .DefaultValue(suggestion.Length > 0 ? suggestion : "")
                    .ShowDefaultValue());
            var slug = SlugValidator.Canonicalize(input);

            var check = SlugValidator.Validate(slug);
            if (!check.Ok) {
                AnsiConsole.MarkupLine(check.Reason == "blocked"
                    ? $"  [yellow]![/] '{Markup.Escape(slug)}' is reserved — pick another."
                    : "  [yellow]![/] Use lowercase letters, digits and single hyphens (no leading/trailing hyphen), max 40 chars.");
                continue;
            }

            var avail = await AnsiConsole.Status().StartAsync($"Checking {slug}.kcap.ai…",
                async _ => await client.CheckAvailabilityAsync(baseUrl, token, slug, ct));

            if (avail is null) {
                AnsiConsole.MarkupLine("  [yellow]![/] Couldn't check availability. Try again.");
                continue;
            }
            if (avail.Available || avail.Reason == "yours") return slug;

            AnsiConsole.MarkupLine(avail.Reason switch {
                "reserved" => $"  [yellow]![/] '{Markup.Escape(slug)}' is being provisioned by someone else — pick another.",
                "taken"    => $"  [yellow]![/] '{Markup.Escape(slug)}' is taken — pick another.",
                "blocked"  => $"  [yellow]![/] '{Markup.Escape(slug)}' is reserved — pick another.",
                _          => $"  [yellow]![/] '{Markup.Escape(slug)}' is unavailable — pick another."
            });
        }
    }

    async Task<ProvisionOffer> PollAsync(string token, string slug, string orgName, string origin, CancellationToken ct) {
        return await AnsiConsole.Status().StartAsync($"Provisioning {slug}.kcap.ai — this can take a few minutes…", async _ => {
            for (var i = 0; i < MaxPolls; i++) {
                await Task.Delay(PollIntervalMs, ct);
                var status = await client.GetStatusAsync(baseUrl, token, slug, ct);
                switch (status?.State) {
                    case "active" when status.WorkosOrgId is { Length: > 0 } orgId:
                        return ProvisionOffer.Created(new ProvisionedTenant(orgId, slug, orgName, status.Url ?? origin));
                    case "failed":
                        AnsiConsole.MarkupLine("  [red]✗[/] Provisioning failed. Re-run [cyan]kcap setup " + Markup.Escape(slug) + "[/] to retry.");
                        return ProvisionOffer.Failed;
                }
            }
            AnsiConsole.MarkupLine($"  [yellow]![/] Still provisioning. Re-run [cyan]kcap setup {Markup.Escape(slug)}[/] once it's ready.");
            return ProvisionOffer.InProgress;
        });
    }

    static string Reason400(string? reason) => reason switch {
        "disposable_email" => "Provisioning requires a non-disposable email address.",
        "blocked"          => "That slug is reserved. Pick another and re-run.",
        _                  => "Invalid organization name or slug."
    };

    static string Reason409(string? reason, string slug) => reason switch {
        "owned_by_other" => $"'{slug}' is owned by someone else. Pick another and re-run.",
        _                => $"'{slug}' is already taken. Pick another and re-run."
    };
}
```

- [ ] **Step 2: Wire it into `kcap setup` (interactive only)**

In `src/Capacitor.Cli/Commands/SetupCommand.cs`, `RunDiscoveryAsync`, replace the WorkOS branch call (lines 424-425):
```csharp
        if (provider == AuthProvider.WorkOS) {
            var exit = await WorkOSDiscovery.RunWithLiveAuthAsync(
                AuthProxyEndpoint.Url, proxyConfig, proxyClient, new SpectreTenantPicker());
```
with:
```csharp
        if (provider == AuthProvider.WorkOS) {
            // Offer inline tenant creation only in an interactive session; headless
            // setup keeps the legacy "ask your admin" dead-end (provisioner is null).
            ITenantProvisioner? provisioner = HeadlessEnvironment.IsHeadless()
                ? null
                : new SpectreTenantProvisioner(new TenantProvisioningClient(new HttpClient()), ProvisioningEndpoint.Url);

            var exit = await WorkOSDiscovery.RunWithLiveAuthAsync(
                AuthProxyEndpoint.Url, proxyConfig, proxyClient, new SpectreTenantPicker(), provisioner);
```
(The rest of the branch — reading the active profile after `RunWithLiveAuthAsync` — is unchanged.)

- [ ] **Step 3: Build and verify no AOT warnings**

Run:
```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo "no AOT warnings"
```
Expected: build succeeds; grep prints `no AOT warnings`.

- [ ] **Step 4: Run the full unit suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: PASS (all tests, including B1-B4).

- [ ] **Step 5: Manual verification (record the result)**

Against a dev/preview kcap-web (or with `KCAP_SIGNUP_URL` pointed at a local `npm run dev`), run `kcap setup` as a WorkOS user with **no** tenants and confirm:
1. The "No Capacitor tenant … Create one now?" prompt appears.
2. Org name → a derived slug default → availability check (try a taken slug like `kcap` to see the reserved rejection, then a free one).
3. Confirmation → provisioning spinner → on `active`, setup continues (profile saved, "Logged in as … → …", steps 3-5 proceed).
4. Declining at the first prompt exits without the "ask your admin" message.

Note in the PR description what was exercised. (No automated test — interactive Spectre prompts aren't unit-tested here.)

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli/Commands/SpectreTenantProvisioner.cs src/Capacitor.Cli/Commands/SetupCommand.cs
git commit -m "feat(setup): interactive tenant creation prompt in kcap setup"
```

---

### Task B6: Docs (README + help-setup.txt)

**Files:**
- Modify: `README.md` (quick-start + `setup` command section)
- Modify: `src/Capacitor.Cli.Core/Resources/help-setup.txt`

**Interfaces:** none (documentation).

- [ ] **Step 1: Locate the setup docs**

Run:
```bash
grep -n "setup" README.md | head
```
Expected: shows the getting-started/quick-start mention of `kcap setup` and the per-command `setup` section.

- [ ] **Step 2: Update `README.md`**

In the getting-started/quick-start area, add one sentence after the `kcap setup` description:
```markdown
If you sign in and don't yet have a Capacitor tenant, `kcap setup` offers to create one for you (name + workspace URL), provisions it, and continues once it's live.
```
In the per-command `setup` section, add a short bullet:
```markdown
- **New tenant:** when signing in via Kurrent's hosted auth and you have no tenant yet, `setup` prompts to create one (organization name + `<slug>.kcap.ai` workspace URL) and waits for it to come online. Non-interactive runs (`--no-prompt`) skip this and exit with guidance.
```

- [ ] **Step 3: Update `help-setup.txt`**

Read the file, then add a brief line under the description of what setup does:
```
When you sign in with Kurrent's hosted auth and have no tenant yet, setup can
create one for you (organization name + workspace slug) and wait for it to go live.
```

- [ ] **Step 4: Verify the mentions are consistent**

Run:
```bash
grep -n "create" README.md src/Capacitor.Cli.Core/Resources/help-setup.txt
```
Expected: the new create-tenant copy appears in both files.

- [ ] **Step 5: Commit**

```bash
git add README.md src/Capacitor.Cli.Core/Resources/help-setup.txt
git commit -m "docs: document kcap setup create-tenant behavior"
```

---

## Final verification (both repos)

- [ ] **kcap-web:** `npm test` (from `/Users/alexey/dev/eventstore/kcap-web`) — all green.
- [ ] **kcap-cli unit:** `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` — all green.
- [ ] **kcap-cli AOT:** `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — empty.
- [ ] **PRs:** two PRs (kcap-web, kcap-cli). The kcap-cli PR references the Linear issue + GitHub issue with a closing keyword (per CLAUDE.md). Note the cross-repo dependency: kcap-web must deploy before the CLI flow works end-to-end (the CLI's contract tests use WireMock and don't require it).
