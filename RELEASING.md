# Releasing kcap

The CLI version tracks the server version (consolidated bump). npm dist-tags
gate who receives a release:

- **Prerelease tag** `vX.Y.Z-beta.N` → published to the **`beta`** dist-tag.
  `latest` is untouched. Use this for anything deployed **internal-tenants-first**.
- **Release tag** `vX.Y.Z` → published to **`latest`**. Everyone on `stable`/`micro`
  who runs `kcap update` (or `npm i -g @kurrent/kcap`) gets it.

## Invariant (do not break)

Cut a bare `vX.Y.Z` tag **only once the matching server version is available to
the cohorts that consume `latest`** (stable + micro). For an internal-first
rollout, cut `vX.Y.Z-beta.N` instead — otherwise `@kurrent/kcap@latest` moves
ahead of those users' servers and reintroduces version skew.

Internal-tenant users opt into the beta CLI with `kcap update --beta`.

## Cutting a beta release

1. Confirm the matching server version is deployed to the internal tenant(s) you're testing.
2. Pick a SemVer prerelease tag `vX.Y.Z-beta.N`, where `X.Y.Z` matches the server version
   (consolidated bump). Start at `beta.1`; bump `N` for each iteration on the same `X.Y.Z`.
3. Tag and push. The `Release` workflow triggers on the `v*` tag, waits for CI to pass,
   builds every platform binary, and publishes all packages to the `beta` dist-tag
   (`latest` is left untouched):

   ```bash
   git tag vX.Y.Z-beta.1
   git push origin vX.Y.Z-beta.1
   ```

4. Verify the publish landed on `beta`, not `latest`:

   ```bash
   npm dist-tag ls @kurrent/kcap
   # latest: <unchanged stable>   beta: X.Y.Z-beta.1
   ```

5. Internal testers opt in (only after the beta is published):

   ```bash
   kcap update --beta             # existing installs — persists the choice per profile
   npm i -g @kurrent/kcap@beta    # fresh installs
   ```

   `kcap update --stable` switches back to the stable channel.

   The desktop app follows its own choice: a stable install takes betas only after
   **Settings → About → Get beta updates** is on, and **Check for Updates…** (app menu,
   tray, or that page) checks the feed right away.

## Promoting a beta to stable

Once the matching server version is live for the `stable`/`micro` cohorts (see the
invariant above), cut the plain release tag from the same — or a newer — commit. This
publishes to `latest`, so everyone on `kcap update` / `npm i -g @kurrent/kcap` receives it:

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

## How the dist-tag is chosen

Only `v*` tags trigger a release. The tag's SemVer shape alone decides the dist-tag —
a hyphenated prerelease (`-beta.N`, `-rc.N`, …) → `beta`; a bare `vX.Y.Z` → `latest` —
via `scripts/npm-dist-tag.sh`. No manual dist-tag step is needed.

## Windows code signing

A release signs its Windows binaries with Azure Trusted Signing (Azure Artifact Signing) once the
repository variables below are set. Without them the Windows binaries ship unsigned and the desktop
build logs a warning; SmartScreen then warns on the first download.

### What is signed, and where

| File | Signed in | Why there |
|---|---|---|
| `kcap-daemon.exe` | `cli-build.yml`, win-x64 leg, right after the daemon publish | The CLI embeds the daemon's digest and refuses a daemon that does not match it, so the daemon is signed before the digest is computed. |
| `kcap.exe`, `kcap-service.exe` | `cli-build.yml`, win-x64 leg, after the AOT publishes | Before `package-cli.sh` builds the npm package and `kcap-win-x64.zip`, so npm, the GitHub Release and the install manifest's checksums all cover signed bytes. |
| `kcap-desktop.exe`, its Velopack stub, `Update.exe`, `Setup.exe` | `desktop.yml`, inside `vpk pack` (`--azureTrustedSignFile`) | Velopack signs while it packs, before the feed manifest records hashes. It skips files already signed and also excludes the CLI, daemon and service by name: the bundle must carry the bytes npm ships. |

`e_sqlite3-win-x64.dll` stays unsigned: the CLI verifies it against a SHA-256 pinned in source.

Both jobs then run `scripts/verify-windows-signatures.sh` (`signtool verify /pa`, plus a required
timestamp: a Trusted Signing certificate is valid for three days, so an untimestamped signature
stops verifying almost at once; and a signer equal to `AZURE_TRUSTED_SIGNING_PUBLISHER` whose
certificate carries `AZURE_TRUSTED_SIGNING_EKU`, because Velopack keeps a file someone else already
signed and another publisher can hold a trusted certificate with the same common name). The desktop job checks the installed tree that
`Setup.exe` lays down, and that its CLI, daemon and service are byte-identical to what the CLI build
signed.
`assert-bundle-digest.sh` separately proves the bundled daemon still matches the CLI's digest.

### Gating

Signing runs only in the `Release` workflow on a pushed `v*` tag: it passes `sign: true` to
`cli-build.yml` and `release: true` to `desktop.yml`, which CI never does, so pull requests (forks
included) never reach it. On top of that, `AZURE_TRUSTED_SIGNING_ACCOUNT` or
`AZURE_TRUSTED_SIGNING_PROFILE` must be set. Either one switches signing on, and from then on a
missing or malformed variable, a failed sign-in, a failed signature or a failed verification fails
the release instead of letting it ship unsigned. Clearing both variables switches it off.

Authentication is GitHub OIDC through `azure/login`: the `Release` workflow's `id-token: write`
reaches the reusable workflows, and no client secret is stored. Only the signing jobs enter the
`windows-signing` environment, which is what the federated credential trusts.

### Turning it on

In Azure:

1. Use the Trusted Signing account Kurrent already has, or create one. Its region fixes the
   endpoint; copy the **Account URI** from the account's Overview page, for example
   `https://eus.codesigning.azure.net` (East US), `https://wus2.codesigning.azure.net` (West US 2),
   `https://neu.codesigning.azure.net` (North Europe) or `https://weu.codesigning.azure.net`
   (West Europe).
2. Use a **Public Trust** certificate profile on that account, existing or new. A new one needs
   Kurrent's completed identity validation on the same account.
3. Create a Microsoft Entra app registration (or a user-assigned managed identity) for kcap-cli.
   Give it no client secret. Add a federated credential:
   - Scenario: GitHub Actions deploying Azure resources
   - Organization `kurrent-io`, repository `kcap-cli`, entity **Environment**, name `windows-signing`
   - which yields issuer `https://token.actions.githubusercontent.com`, subject
     `repo:kurrent-io/kcap-cli:environment:windows-signing`, audience `api://AzureADTokenExchange`
4. Assign that identity the **Trusted Signing Certificate Profile Signer** role (listed as
   **Artifact Signing Certificate Profile Signer** in newer portals) on the certificate profile, or
   on the account to cover every profile in it.

In GitHub (repository settings):

5. Create the environment `windows-signing`. Under deployment branches and tags choose
   **Selected branches and tags** and add the tag rule `v*`. Required reviewers would hold every
   release's Windows jobs until someone approves.
6. Add these as **repository** variables (Secrets and variables → Actions → Variables), not
   environment variables: the workflows read them to decide whether to enter the environment.

   | Variable | Value |
   |---|---|
   | `AZURE_TRUSTED_SIGNING_ENDPOINT` | The account URI from step 1 |
   | `AZURE_TRUSTED_SIGNING_ACCOUNT` | The signing account name |
   | `AZURE_TRUSTED_SIGNING_PROFILE` | The certificate profile name |
   | `AZURE_TRUSTED_SIGNING_CLIENT_ID` | The app registration's Application (client) ID |
   | `AZURE_TRUSTED_SIGNING_TENANT_ID` | The Directory (tenant) ID |
   | `AZURE_TRUSTED_SIGNING_PUBLISHER` | The certificate's subject common name, exactly as `signtool verify /v` prints it under "Issued to" for the signer |
   | `AZURE_TRUSTED_SIGNING_EKU` | The certificate profile's own EKU, `1.3.6.1.4.1.311.97.` followed by values unique to the profile — not `1.3.6.1.4.1.311.97.1.0`, which every Public Trust certificate carries. Read it from any file the profile signed: `(Get-AuthenticodeSignature <file>).SignerCertificate.EnhancedKeyUsageList`. It stays the same across the daily certificate renewals, but deleting and recreating the profile assigns a new one |

   None of these is a secret, and no secret is needed.

### Checking the first signed release

1. Cut a beta tag. In the run, the `CLI / Build (win-x64)` job must show **Sign the Windows
   daemon**, **Sign the Windows CLI and service** and **Verify the Windows signatures** as run,
   not skipped; `Desktop (Windows) / Build` must show **Verify the Windows signatures**, and its
   **Pack** log must list the code-signed files with no "ships unsigned" warning.
2. On a Windows machine, check what users download: `kcap-win-x64.zip` and
   `kcap-<version>-win-x64-Setup.exe` from the GitHub Release, and the `bin\` of
   `npm pack @kurrent/kcap-win-x64@<version>`. For each `.exe`,
   `Get-AuthenticodeSignature <file> | Format-List` must report `Valid` with Kurrent as signer and
   a timestamp, or run `signtool verify /pa /v <file>`.
3. `npm i -g @kurrent/kcap@beta`, then `kcap daemon start`: the daemon must start, which proves the
   CLI's embedded digest matches the signed daemon.
4. Download the Setup.exe in Edge. SmartScreen should name Kurrent as publisher rather than
   "Unknown publisher"; a reputation prompt can persist until the certificate builds reputation.
