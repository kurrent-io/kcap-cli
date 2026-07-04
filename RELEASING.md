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
