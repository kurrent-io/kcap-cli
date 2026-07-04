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
