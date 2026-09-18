# Repo-local skill materialization — design

Issue: [#778](https://github.com/kurrent-io/kcap-cli/issues/778) (AI-2526). Umbrella: AI-2828.
Evidence: `docs/probes/2026-09-16-skills-discovery/` — `capability-matrix.md`, `findings.md`, and the
642-row `matrix.json` every claim about harness behaviour below cites.

## Problem

`kcap skills sync` fetches a snapshot addressed to the current repository and writes it into
user-global harness trees (`~/.claude/skills`, `~/.agents/skills`, `~/.kiro/skills`,
`~/.gemini/skills`). A skill approved for repository A is therefore offered to a session working in
repository B, and two worktrees of one repository share a single set of files and a single manifest.
The name and description of a skill carry no enforcement.

The ownership ledger, the reconciliation planner, the drift rule and the conditional fetch are
already correct and already keyed per repository. What is wrong is the destination.

## Decisions

Four decisions were taken before this design, and the rest follows from them.

1. **A vendor-restricted skill is delivered and its exposure recorded.** A repository-local
   `.claude/skills` is read by Claude, Copilot, Cursor and OpenCode; `.codex/skills` by Codex and
   Cursor. Placement cannot enforce a vendor restriction for those two. The skill is written anyway,
   and the manifest records which harnesses can read the tree, for the startup adapter (#962) to
   surface.
2. **Four trees per checkout**, mirroring today's target set.
3. **The anchor is an explicit argument**, defaulting to the checkout or worktree root, so #962 can
   materialize at a session's own launch directory without changing the materializer.
4. **The manifest lives in the worktree's own git directory**, which makes it per worktree by
   construction and removes it with the worktree.

## Destinations

A resolver turns an anchor directory into the four roots. Each vendor's `Paths` type gains a
repository-relative skills directory beside the user-global one it already exposes, so vendor
knowledge stays in `Harness/<Vendor>/` as the layout rule requires: `ClaudePaths`, `AgentsPaths`,
`KiroPaths` and `GeminiPaths`.

| Target key | Root under the anchor | Fetched with vendor | Harnesses measured to read it |
| -- | -- | -- | -- |
| `agents` | `.agents/skills` | none | Codex, Copilot, Cursor, OpenCode, Pi, Antigravity |
| `claude` | `.claude/skills` | `claude` | Claude, Copilot, Cursor, OpenCode |
| `kiro` | `.kiro/skills` | `kiro` | Kiro |
| `gemini` | `.gemini/skills` | none | none measured; kept on the vendor's documentation |

`SkillsTarget` becomes `(Key, RelativePath, Vendor, Readers)` with a `Root(anchor)` accessor.
`Readers` is the measured list above and is what the manifest records as exposure. The `gemini` row
carries an empty `Readers` list and a note that no session has confirmed it: Gemini could not be
measured on the probe account, and Antigravity reads `.agents/skills` and `.agent/skills`, not
`.gemini/skills`.

A vendor-less target is still fetched without a vendor, so the server's unknown-excludes rule keeps
vendor-restricted documents out of a tree several vendors read. A vendored target is fetched with its
vendor as today, and decision 1 accepts that other harnesses may read it.

Adoption is unchanged: a target is synced when a consuming harness is installed, or when a manifest
for it already exists, so a revocation still reaches a tree whose harness has since been removed.

## The anchor

`HandleSync` resolves the current directory to the enclosing checkout or linked worktree root and
passes it down. Every function below takes the anchor as a parameter and holds no ambient state.

Pi, Kiro and Cursor find skills only under the directory a session was launched from, not the
repository root, so a session started in a subdirectory will not see skills anchored at the root.
That limitation is recorded per target for #962 to act on; this piece does not write into
subdirectories.

## The manifest

One file per target at `<git-dir>/kcap/skills/<target>.json`, where `<git-dir>` is the worktree's own
git directory: `.git` in a main checkout, `<main>/.git/worktrees/<name>` in a linked worktree.
`GitRepository` already parses the `gitdir:` pointer that resolves this.

Existing fields are unchanged: `doc_id`, `slug`, `version`, `content_hash`, `path`, `file_hash`.
Three are added to the manifest itself:

- `anchor` — the directory the files were written for.
- `server` and `profile` — the server URL and active profile name the snapshot came from.
- `exposure` — the target's `Readers`, so a consumer can warn without re-deriving it.

A manifest whose `anchor`, `server` or `profile` does not match the current run is treated as no
ledger: the snapshot is fetched unconditionally, its stored etag is not sent, and the paths it
records are still pruned so nothing is stranded. This is what keeps an authentication or server
change from reusing another identity's conditional request or leaving its catalogue behind.

The lock keeps its mechanism and inherits the new key. Because the manifest path is now per worktree,
two worktrees of one repository no longer contend, which they do today.

## Reconciliation and migration

The planner, the drift rule, the prune safety rule and the whole-snapshot slug validation are
unchanged. The same document delivered to several repositories is several independent
materializations keyed by one stable document id, which the planner already handles; a document
homed at a project or organisation arrives through the same per-repository request, so wider homes
need no client change.

Migration runs after a successful repository-local sync for a target, in an order that survives a
crash:

1. Write the repository-local files.
2. Save the repository-local manifest.
3. Prune the entries the old global manifest owns.
4. Delete the old global manifest.

An interruption at any point leaves the global copy in place and the next sync retries. Pruning walks
only kcap's own manifest, so user-authored skills, other plugins and other repositories are
untouched. A second worktree finds the global manifest already gone and does nothing.

## Git exclusion

One managed block in the worktree's `info/exclude`, delimited by marker comments and rewritten whole,
so it can be added and removed cleanly. Git resolves that file to the shared common directory, so a
single block covers the repository and all its worktrees.

The patterns name only kcap's own directories — `/.agents/skills/kcap-*/` and the three siblings —
never a whole tree, because a repository may have its own committed skills there. When the anchor is
not the repository root, the patterns are written relative to the root.

The probes verified the part that matters: with `info/exclude` in place, every measured harness still
discovers the skill and loads its full body. The code cites that measurement rather than re-checking
at runtime.

## Failure modes

All refuse loudly and none falls back to a global tree, since that fallback is what this issue
removes.

| Condition | Behaviour |
| -- | -- |
| Not inside a git repository | Existing refusal, unchanged |
| Git directory cannot be resolved | Refuse, naming the path |
| Anchor not writable | Refuse, naming the path |
| Snapshot contains an unsafe slug | Existing whole-snapshot refusal, nothing written |
| Manifest unreadable | Existing abort; a corrupt manifest is still moved aside and re-synced |

## Testing

- **Pure units.** Anchor to root per vendor; manifest path for a main checkout and for a linked
  worktree; an identity mismatch reading as no ledger; the exclusion block idempotent to add and
  complete to remove.
- **A real temporary repository with a linked worktree.** Two worktrees receive independent
  materializations; a second repository receives none of the first's skills; user-authored skills
  survive a prune; generated files are untracked and still readable.
- **The command path end to end against a mocked snapshot API.** Manifest read, fetch, plan, write,
  exclude, save, migrate, including the ordering that makes migration retryable. `SyncTargetAsync`
  has no test today, so this is new coverage rather than a rewrite.

`SkillsTargetCatalogTests` pins the four keys, their vendors and that each root's leaf directory is
`skills`; it is updated for anchor-relative roots and the new `Readers` field.

## Out of scope

- Automatic delivery at session start, including the reload and registration routes the probes
  measured (#962).
- Server-side coverage deciding which repositories a document answers for (AI-2525). Its client side
  is complete once this lands, because a project-homed document arrives in each member repository's
  snapshot.
- The bundled-skills installer, a separate system that writes similarly named directories into the
  global trees. Once this output is repository-local the two no longer share a directory.

## Acceptance criteria

- [ ] A sync in repository A leaves a session in repository B with none of A's skills.
- [ ] Two worktrees of one repository have independent, correct materializations and independent
      manifests.
- [ ] A vendor-restricted skill is delivered, and its manifest records every harness that can read
      the tree it landed in.
- [ ] New, changed, renamed, revoked, missing and hand-edited managed files reconcile without
      touching authored skills.
- [ ] Generated files are untracked, and discovery plus full-body loading still pass for a harness.
- [ ] Manifest-owned global copies migrate away, and an interrupted migration is retryable.
- [ ] A server or profile change neither reuses another identity's conditional request nor leaves its
      catalogue in place.
