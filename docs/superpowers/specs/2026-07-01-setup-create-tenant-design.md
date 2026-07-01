# Design: `kcap setup` — offer to create a tenant when none exists

**Date:** 2026-07-01
**Repos touched:** `kcap-cli` (this repo) + `kcap-web` (sibling, backend endpoint)

## Problem

When `kcap setup` runs the WorkOS discovery path and the user has **zero
tenants**, the CLI dead-ends today with:

> "No Capacitor tenants are linked to your account. Ask your admin to invite you."
> — `src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs:65-69`

A brand-new user with no tenant has no self-service way forward from the CLI.
The web UI (`kcap-web`) already provisions tenants self-service; we want the CLI
to offer the same "create a tenant" experience inline instead of dead-ending.

## Decisions (agreed during brainstorming)

- **Provisioning path:** full in-CLI provisioning (prompt → provision → poll),
  not a browser hand-off.
- **Backend home:** `kcap-web`. It already owns the entire provisioning stack
  (D1 tenants DB, WorkOS org creation, GitHub `repository_dispatch`, trial
  guard, Slack/email queue, state machine). The `kcap-server` auth proxy has
  **zero** provisioning code and would have to duplicate all of it — rejected.
- **Scope this cycle:** CLI side **and** the backend endpoint, end-to-end
  functional.
- **Tier:** `free` only. No team/trial handling in the CLI.
- **Headless:** interactive-only. `--no-prompt` keeps today's error and exits;
  no auto-provisioning in CI.
- **GitHub-App discovery path:** unchanged (self-service provisioning is
  WorkOS-only). It keeps its existing "install the Kurrent GitHub App /
  use `--server-url`" guidance.

## How provisioning works today (reference)

Web browser flow (all in `kcap-web`, served from `https://capacitor.kurrent.io`):

1. Authenticated user (sealed `wos-session` cookie) POSTs
   `/api/signup/provision` with `{ orgName, slug, tier }`.
2. `runProvision` (`src/server/signup/provision.ts`): validate slug → reserve
   D1 row → `getOrCreateOrgByExternalId` (WorkOS) → `upsertOwnerMembership`
   (owner) → `fireRepositoryDispatch` to `kurrent-io/kcap-deployments`
   (`event_type: "kcap-signup"`). State machine: `reserved → provisioning →
   active | failed`.
3. Client polls `/api/signup/status?slug=` (probes `https://{slug}.kcap.ai/auth/config`)
   until `state == "active"`; tenant lives at `https://{slug}.kcap.ai`.

The **only** thing blocking the CLI from reusing this: the `/api/signup/*`
endpoints authenticate via a sealed **session cookie**. The CLI holds a WorkOS
**bearer access token** (from its org-less discovery login), not that cookie.

Key already-decoupled seam: `runProvision(env, user, input, workos, ctx)` takes
a plain `user: { id, email, firstName?, lastName? }` — independent of how that
user was authenticated. Cookie extraction lives in the thin `handleProvision`
wrapper, not in `runProvision`.

## Part A — kcap-web backend (accept a WorkOS bearer token)

**Single seam.** All three signup handlers authenticate through one function:
`requireVerifiedUser(request, env)` in `src/server/auth/session.ts`
(`handleAvailability`, `handleProvision`, `handleStatus` all call it). Make that
function **try bearer first, then fall back to the existing cookie flow**. No
handler and no `runProvision` change.

New `authenticateBearer(request, env): Promise<SessionResult | null>`:

1. Read `Authorization: Bearer <token>`. Absent → return `null` → the existing
   cookie path runs unchanged (browser flow untouched).
2. Verify the WorkOS access-token JWT (signature + lifetime) against WorkOS
   JWKS `https://api.workos.com/sso/jwks/{WORKOS_CLIENT_ID}` using `jose`
   (`createRemoteJWKSet` cached at module scope + `jwtVerify`). Issuer/audience
   validation **off**, matching how the `kcap-server` auth proxy already
   validates these exact tokens (`DiscoverTenantsWorkOSEndpoint.cs`). Invalid →
   `{ ok: false, status: 401 }`.

   **Load-bearing invariant (must hold or bearer auth breaks):** the CLI's
   org-less token is minted by the auth proxy's WorkOS client id
   (`proxyConfig.WorkOSClientId` from `auth.kcap.ai/config`;
   `WorkOSDiscovery.cs:23-26`). JWKS is scoped per client id, so kcap-web can
   only verify that token if its own `WORKOS_CLIENT_ID` **equals** the auth
   proxy's WorkOS client id. Today both are the same shared production AuthKit
   Application (`client_01KTVSZN2HXGP8NFH0S0HA8PAV`:
   `kcap-web/wrangler.jsonc:83` == the auth-proxy's `AuthProxy__WorkOS__ClientId`
   in `kcap-deployments`). This invariant is documented next to the verification
   code; if the two apps ever split WorkOS clients, this endpoint must gain a
   dedicated discovery-token client id to fetch the right JWKS. No new env var is
   added *because* the invariant holds.
3. Extract `sub` (WorkOS user id) → `workos.userManagement.getUser(sub)` →
   `{ id, email, emailVerified, firstName, lastName }`. Reuse the existing
   `verified()` mapper; `!emailVerified` → `{ ok: false, status: 403 }`.
4. Return `{ ok: true, user }`. No `refreshedCookie`, so `withSession()` is a
   no-op on the bearer path.

`requireVerifiedUser` becomes: `const bearer = await authenticateBearer(...);
if (bearer) return bearer;` then the current cookie logic verbatim.

**New dependency:** `jose` (Workers-compatible, pure ESM). Alternative
considered: hand-roll RS256 verification with WebCrypto (~40 lines, no
dependency). Recommendation: `jose` — it handles JWKS fetch/cache/rotation and
base64url robustly, and is the pattern WorkOS's own docs use.

**Unchanged / preserved:** slug validation + reserved set, D1 reservation and
state machine, GitHub dispatch, trial guard, disposable-email block, owner
scoping (`row.owner_user_id === user.id`), the entire browser cookie flow.

**Config:** no new env vars — `WORKOS_CLIENT_ID`, `WORKOS_API_KEY` already
present in `Env` (`src/server/env.ts`) — **conditional on the client-id
invariant above**.

**Backend response change — `workosOrgId` (mandatory).** The CLI must org-switch
into the newly created WorkOS org after provisioning, which requires the org id
(`WorkOSDiscovery.cs:78,86`). The current `ProvisionResult`/status bodies omit
it. Add an additive `workosOrgId: string` field to the **`active`** responses:
- `runProvision` `200 { slug, state: "active", url, workosOrgId }`
  (`row.workos_org_id`)
- `handleStatus` active body `{ state: "active", url, workosOrgId }`
This is backward-compatible for the browser flow (extra field, ignored by the
web client). The `202 provisioning` body is unchanged (the CLI only needs the id
once `active`). kcap-web unit tests assert the field is present on `active`.

**Tests (kcap-web):** `authenticateBearer` matrix — valid token, expired token,
bad signature, unverified email (403), and no `Authorization` header falling
through to the cookie path. Mock JWKS + `getWorkOS`.

## Part B — kcap-cli (offer + provision)

**Entry point:** `WorkOSDiscovery.RunAsync`, the `result.Tenants.Length == 0`
branch (`WorkOSDiscovery.cs:65-69`). Replace the dead-end with an **injected**
provisioning offer, mirroring the existing `ITenantPicker` injection so it stays
unit-testable.

```csharp
public interface ITenantProvisioner {
    // Interactive: prompt → provision → poll. The provisioner OWNS all
    // user-facing messaging for its outcomes (see below); the caller must not
    // print a second, conflicting message.
    Task<ProvisionOffer> OfferCreateAsync(string workosAccessToken, CancellationToken ct);
}

// Richer than a nullable tenant so the caller can distinguish "the user said no"
// from "we started provisioning but it isn't live yet" from "it failed" — each
// needs different follow-up, and none should surface the generic
// "ask your admin to invite you" dead-end (finding 5).
public enum ProvisionOfferStatus { Created, Declined, InProgress, Failed }

public sealed record ProvisionOffer(
    ProvisionOfferStatus Status,
    ProvisionedTenant?   Tenant);   // non-null iff Status == Created

public sealed record ProvisionedTenant(
    string OrganizationId, string Slug, string DisplayName, string Origin);
```

Flow change inside `RunAsync` (zero-tenant branch):

- **No provisioner injected** (headless / other call sites) → keep today's
  "No Capacitor tenants … ask your admin to invite you" error + exit 1.
  Unchanged.
- **Provisioner injected** → call `OfferCreateAsync`. The provisioner has
  already printed the outcome-appropriate message, so the caller does **not**
  print the legacy error; it only maps status → next step:
  - `Created` → treat `Tenant` exactly like a *picked* tenant: reuse the
    existing `orgSwitch(auth.RefreshToken!, Tenant.OrganizationId)` closure for
    an org-bound token, then `MergeProfiles` + `SaveProfileConfig` +
    `TokenStore.SaveAsync` (same code as the picked-tenant path). Print
    "Logged in as {user} → {DisplayName}". Return 0.
  - `Declined` → return 1 silently (user chose not to create; provisioner
    already acknowledged, no error spam).
  - `InProgress` (poll cap hit) → return 1; provisioner already printed
    "still provisioning; re-run `kcap setup {slug}` when ready".
  - `Failed` → return 1; provisioner already printed the failure reason.

**Call-site wiring (explicit — finding 4).** `RunWithLiveAuthAsync` and
`RunAsync` gain an optional `ITenantProvisioner? provisioner = null` parameter.
Only **`kcap setup`** passes a real `SpectreTenantProvisioner`
(`SetupCommand.RunDiscoveryAsync`, `SetupCommand.cs:424-425`). **`kcap login
--discover`** (`Program.cs:719-721`) passes `null`, so its behavior is
unchanged — tenant creation is a `setup` affordance, not a `login` one. (Login
can opt in later by passing a provisioner; the seam already supports it.) Unit
tests inject a fake provisioner directly into `RunAsync`.

The CLI already holds the **org-less** WorkOS access token (`auth.AccessToken`)
and refresh token (`auth.RefreshToken`) at this point in `RunAsync`, so no extra
login is needed: the access token authenticates the provisioning call, and the
refresh token drives the post-provision org-switch into the new org.

**Provisioning client** — `TenantProvisioningClient(HttpClient)` in
`Capacitor.Cli.Core` (no Spectre; WireMock-testable). Talks to
`ProvisioningEndpoint.Url` sending `Authorization: Bearer <org-less workos access token>`:

- `GET  /api/signup/availability?slug=` → `{ available: bool, reason?: string }`
- `POST /api/signup/provision` body `{ orgName, slug, tier: "free" }`
  → `202 { slug, state: "provisioning" }`
  | `200 { slug, state: "active", url, workosOrgId }`
  | `400 { reason: "invalid"|"blocked"|"disposable_email" }`
  | `409 { reason: "taken"|"owned_by_other"|"trial_exists" }`
- `GET  /api/signup/status?slug=`
  → `{ state: "reserved"|"provisioning"|"active"|"failed", url?, workosOrgId? }`
  (`workosOrgId` present only when `active`)

**JSON contract is camelCase, but `CapacitorJsonContext` is globally
`SnakeCaseLower` (`Models.cs:725`) — so every request/response record MUST carry
explicit `[JsonPropertyName("...")]` on every field** (finding 1). Without it the
request serializes as `org_name`, which kcap-web rejects as `400 invalid`
(`provision.ts:131`). This mirrors the existing `WorkOSAuthResponse` /
`DiscoveredTenant` records, which already annotate every property for the same
reason. **Add raw-wire tests** asserting the serialized request contains
`"orgName"` (not `org_name`) and that responses deserialize from the literal
camelCase names (`workosOrgId`, `available`, `reason`, `state`, `url`).

`ProvisioningEndpoint`: `const string DefaultUrl = "https://capacitor.kurrent.io"`,
env override `KCAP_SIGNUP_URL` for dev/preview — same pattern as
`AuthProxyEndpoint` (`KCAP_AUTH_PROXY_URL`). New JSON records registered in
`CapacitorJsonContext` (AOT-safe source-gen). Local slug pre-validation
replicates `SLUG_PATTERN` + the reserved set for instant feedback; the server
remains the source of truth (provision re-validates).

**Interactive UX** — `SpectreTenantProvisioner` in `Capacitor.Cli/Commands`:

1. "No Capacitor tenant is linked to your account. Create one now? [Y/n]" — no →
   return `Declined` (caller exits 1 silently; no legacy error).
2. Prompt **Organization name** (e.g. "Acme Inc").
3. Derive a default **slug** from the name (lowercase, strip diacritics,
   non-alphanumeric → `-`, collapse repeats, trim to ≤40 chars). Show it, let the
   user edit. Validate locally; re-prompt on invalid.
4. **Availability** check (spinner). `taken` / `reserved` / `blocked` → explain
   and re-prompt the slug. `yours` is treated as available-to-you (resume).
5. Confirm: "Create tenant 'Acme Inc' at https://acme.kcap.ai? [Y/n]".
6. `POST /provision`. Map `400` (`invalid` / `blocked` / `disposable_email`) and
   `409` (`taken` / `owned_by_other`) to clear messages; re-prompt the slug where
   sensible, otherwise abort with guidance.
7. On `202`/`200`, poll `GET /status` every ~4s with a spinner ("Provisioning
   acme.kcap.ai — this can take a few minutes"). CLI cap ~10 min (server budget
   is 15 min).
   - `active` → return `ProvisionOffer(Created, tenant)` where
     `OrganizationId = workosOrgId` (from the `active` provision/status body —
     now a mandatory field), `Slug`, `DisplayName = org name`, `Origin = url`.
   - poll cap hit while still `provisioning` → print "still provisioning; re-run
     `kcap setup acme` when it's ready" and return `InProgress`.
   - `failed` → print the reason, suggest retry, return `Failed`.
   Any 400/409 that can't be re-prompted, or a network error → print guidance,
   return `Failed`.

**After provisioning (back in `WorkOSDiscovery.RunAsync`):** identical to the
picked-tenant path — `orgSwitch` → `MergeProfiles` → save config + tokens →
"Logged in as … → …". `SetupCommand.RunDiscoveryAsync` then continues setup
normally against the new `https://{slug}.kcap.ai`.

## Testing

- **kcap-cli — wire contract:** raw-serialization test that the `/provision`
  request body contains the literal `"orgName"` (not `org_name`) and `"tier"`;
  deserialization tests parsing literal camelCase responses including
  `"workosOrgId"`. Guards against the global `SnakeCaseLower` policy (finding 1).
- **kcap-cli — client:** `TenantProvisioningClient` against WireMock —
  availability shapes; `202 → poll → active` (asserts `workosOrgId` flows into
  `ProvisionedTenant.OrganizationId`); `409`/`400` handling; poll-cap timeout.
- **kcap-cli — discovery wiring:** `WorkOSDiscovery.RunAsync` with a fake
  provisioner — `Created` → org-switch/save path (uses `OrganizationId`);
  `Declined`/`InProgress`/`Failed` → exit 1, **no** legacy "ask your admin"
  message; **no provisioner (headless)** → legacy error preserved.
  A test asserting `kcap login --discover`'s call site passes no provisioner
  (behavior unchanged, finding 4).
- **kcap-web unit:** `authenticateBearer` matrix (valid/expired/bad-sig/
  unverified-email/no-header→cookie-fallback); `active` provision + status
  bodies include `workosOrgId` (finding 2).
- **AOT:** `dotnet publish -c Release` clean of IL3050/IL2026; new records in
  the source-gen JSON context.

## Docs

- `README.md`: getting-started + the `setup` command section — document that
  `kcap setup` now offers to create a tenant when you have none.
- `src/Capacitor.Cli.Core/Resources/help-setup.txt`: same, briefly.

(Per CLAUDE.md: user-facing CLI surface changes must update `README.md` in the
same PR — not just the help text.)

## Out of scope

- Team tier / trials.
- Headless provisioning flags (`--create-tenant` etc.).
- GitHub-App self-service provisioning.
- Any change to the web browser signup flow (cookie path is untouched).

## Resolved contract decisions (from spec review)

- **`workosOrgId`**: mandatory additive field on the `active`
  provision/status responses in kcap-web; the CLI reads it to org-switch. (Not
  an open question — see the backend response change in Part A.)
- **Client-id invariant**: kcap-web `WORKOS_CLIENT_ID` == the auth-proxy WorkOS
  client id; bearer verification depends on it. Documented at the verification
  site. (Part A.)
- **JSON property names**: explicit `[JsonPropertyName]` on every CLI
  request/response record + raw-wire tests, because `CapacitorJsonContext` is
  globally `SnakeCaseLower`. (Part B.)
- **Call-site wiring**: `setup` only; `kcap login --discover` passes no
  provisioner and is unchanged. (Part B.)
- **Outcome messaging**: `ProvisionOffer{Created,Declined,InProgress,Failed}`;
  the provisioner owns messaging, the caller never prints the legacy dead-end
  when a provisioner ran. (Part B.)
