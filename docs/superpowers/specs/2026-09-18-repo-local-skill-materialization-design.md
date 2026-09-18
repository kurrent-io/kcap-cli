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
already correct and already keyed per repository. What is wrong is the destination — and, once the
destination moves, four things that the current design gets for free stop being free: identity,
crash recovery, serialization of what remains shared, and containment.

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

### What decision 1 changes about the acceptance contract

The issue's original criterion — "another harness in the same checkout cannot discover a skill
restricted to a different vendor" — is **not** met by this design and is deliberately replaced. A
skill restricted to Claude lands in `.claude/skills`, which Copilot, Cursor and OpenCode read. The
replacement criterion is that the exposure is recorded and reported. Enforcing the original would
require withholding the skill, which decision 1 rejects.

## Destinations

A resolver turns an anchor directory into the four roots. Each vendor's `Paths` type gains a
repository-relative skills directory: `ClaudePaths`, `AgentsPaths` and `KiroPaths` already expose a
user-global one beside it; `GeminiPaths` exposes none today, because the `gemini` target currently
borrows `AntigravityPaths.SkillsDir`, so it gains the first.

| Target key | Root under the anchor | Fetched with vendor | Harnesses measured to read it |
| -- | -- | -- | -- |
| `agents` | `.agents/skills` | none | Codex, Copilot, Cursor, OpenCode, Pi, Antigravity |
| `claude` | `.claude/skills` | `claude` | Claude, Copilot, Cursor, OpenCode |
| `kiro` | `.kiro/skills` | `kiro` | Kiro |
| `gemini` | `.gemini/skills` | none | none measured; kept on the vendor's documentation |

`SkillsTarget` becomes `(Key, RelativePath, Vendor, Consumers, Readers)` with a `Root(anchor)`
accessor. The two lists are deliberately different things. `Consumers` is the documented set of
harnesses a target is meant to serve, and it decides adoption. `Readers` is the measured set above,
and it is what the manifest records as exposure. `gemini` has a consumer and no measured reader,
which is precisely the state the evidence supports.

Two notes on that table. Antigravity reads a repository-local `.agents/skills`, which contradicts the
assumption in `AntigravityPaths.SkillsDir` that it reads no agent-agnostic tree; that comment
describes the user-global tree, which the probes did not re-test. And nothing was measured reading a
repository-local `.gemini/skills`: Gemini never ran on the probe account, and Antigravity reads
`.agents/skills` and `.agent/skills` instead.

**Adoption follows the target's consumers.** A target is adopted when any harness in its
`Consumers` list is installed, or when a manifest for it already exists, or when a legacy global
manifest for it exists so its files can still be migrated away. Today's hand-written mapping sends an
Antigravity-only machine to `.gemini/skills` and skips `.agents/skills`, which is the tree
Antigravity actually reads; a Gemini-only machine must still adopt `.gemini/skills`, which it does
today and which an adoption rule driven by measured readers alone would silently stop doing.

**Which restrictions can be delivered at all.** Only a restriction to Claude or Kiro has a tree
fetched with that vendor. A skill restricted to Codex, Copilot, Cursor, OpenCode, Pi or Antigravity
is excluded from every request this design makes, because the only tree those harnesses read is
fetched without a vendor. That is a bounded limitation of the four-tree decision, recorded for #962,
not a promise this design keeps.

## The anchor

`HandleSync` resolves the current directory to the enclosing checkout or linked worktree root and
passes it down. Every function below takes the anchor as a parameter and holds no ambient state.

Pi, Kiro and Cursor find skills only under the directory a session was launched from, not the
repository root, so a session started in a subdirectory will not see skills anchored at the root.
That limitation is recorded per target for #962 to act on; this piece does not write into
subdirectories.

**An anchor change is a transition of its own.** A manifest recorded under a different anchor has the
right documents at the wrong paths, and neither the conditional request nor the planner would notice:
the etag would return `304`, and the planner compares document, version, hash and slug, never the
destination. So a mismatched anchor discards the etag, treats every entry as a write at the new
paths, and moves every old path into the pending-prune journal below, each recorded with the root
that authorises it, to be deleted once the new copies exist. When an identity change arrives at the
same time, the identity rule wins and the old paths go first; the precedence is stated once, under
crash recovery.

## Containment

A writable anchor does not prove the destination stays inside it. `<repo>/.agents/skills` may be a
symlink to `~/.agents/skills`, in which case a naive writer republishes repository content globally
and the prune guard, which compares paths lexically, does not notice.

Every destination is therefore resolved the way this repository already resolves a containment
boundary: walk the raw components from the anchor, splice each link target unnormalized, and resolve
the boundary the same way, as the symlink containment work established. A destination that escapes
the anchor, or a `SKILL.md` that is itself a link, refuses the write and the prune. The check covers
every component, not only the leaf.

## Identity and ownership

The manifest lives at `<git-dir>/kcap/skills/<target>.json`, where `<git-dir>` is the worktree's own
git directory: `.git` in a main checkout, `<main>/.git/worktrees/<name>` in a linked worktree.
`GitRepository` already parses the `gitdir:` pointer that resolves this.

Existing entry fields are unchanged: `doc_id`, `slug`, `version`, `content_hash`, `path`,
`file_hash`. Entries additionally carry the server-provided `home` and `applicability` metadata the
issue requires, with the repository home derived from the request for servers that do not send it.

The manifest itself records:

- `anchor` — the directory the files were written for.
- `identity` — the effective credential identity the snapshot was fetched under: the account the
  token authenticates as, together with the server URL. The profile name alone is not identity,
  because signing in again replaces the credentials inside one profile and server.
- `exposure` — the target's `Readers`.
- `pending` and `pending_prunes` — see the next section.

**Ownership survives an identity change; the conditional-fetch cache does not.** When the recorded
identity differs from the current one, the etag is discarded and the recorded paths are still owned.
The old catalogue is pruned *before* the replacement snapshot is requested, and the manifest is saved
owning nothing. A failed replacement fetch — 401, 403, 404 or a transport error — therefore leaves no
files from the previous identity, and the next successful sync materializes from scratch. The
current code returns early on those branches, which would leave the old catalogue in place.

**The transition also settles outstanding legacy ownership**, under the migration lock and by the
same overlap rule, without waiting for a replacement snapshot. Migration otherwise runs only after a
successful local sync, so an account switch whose first fetch fails would leave the previous
account's global copies loadable — exactly the outcome the paragraph above forbids for local
files.

## Crash recovery

Two mechanisms, because the manifest is the only thing that makes a file prunable.

**Ownership is recorded before the write, for both directions.** The manifest is saved with the
planned entries, `pending: true`, and a `pending_prunes` journal before any file is written, then
re-saved with `pending: false` and an empty journal once the writes and prunes succeed.

The journal holds paths awaiting deletion, deliberately not as document-keyed entries. The entries
hold one row per document, so a rename — same document, new slug — cannot record the old path and the
new one at once, and saving only the planned entries would lose the old path entirely. A crash
between the pending save and the prune would then leave `kcap-old` on disk with nothing owning it,
even if the document is revoked before the next attempt.

**Each journal path carries the root that authorises it.** A path is recorded as a pair: the
directory to delete, and the skills root it was a direct child of. Containment is defined against an
anchor, and the prune guard admits only a direct child of the root it is given, so after a move from
one anchor to another the new anchor's root cannot authorise the old anchor's paths. Recovery
validates each pair against its own recorded root, with the same prune-safety and containment rules
as any other prune. A journal is merged, never replaced: recording a new transition cannot discard
paths an earlier one is still waiting to delete.

**And reconciled against the plan before it is published.** Merging alone would delete a live file. A
document renamed away and back — `kcap-x` to `kcap-y`, then back to `kcap-x` before the retry —
leaves an outstanding intent to delete `kcap-x` while the new plan writes exactly that path; acting
on the intent afterwards removes the live copy and commits a manifest claiming it exists. So every
pending publication first cancels the intents whose paths are live destinations in the plan, keeping
their ownership in the document entries, and compares canonically resolved destinations so an alias
cannot slip past the check. An anchor moved away and back has the same shape. Identity retirement is
unaffected, because it deletes the previous identity's paths before any plan exists.

A crash between the write and the manifest save likewise leaves every published path owned, so a
later sync prunes or rewrites it whatever the snapshot has since done.

**Precedence, when more than one transition is outstanding.** An identity retirement runs first and
unconditionally: every entry and every journal path belonging to the retired identity is deleted
before any snapshot is requested, whether or not a replacement exists. An anchor movement is
copy-before-delete, and only while the identity is unchanged: a journal path is deleted once its
replacement has been written, or once a snapshot confirms the document is gone. So a crash straight
after a pending save, with no replacement yet written and the identity unchanged, leaves the old copy
in place and retries; the same crash followed by an account switch deletes it before fetching.

**Publication is atomic.** `SKILL.md` is written to a temporary file in its own directory and moved
into place, as the manifest already is. An interrupted write must never leave a half-file that the
drift hash then treats as an edit.

A manifest found with `pending: true` forces a drift re-check of every entry and forfeits the
conditional request, which is the path that already exists for drift.

The no-change paths matter here. A `304`, and a plan with no writes and no prunes, both return early
today; both must still clear a pending flag, run migration, and refresh the exclusion block.

## Serialization

The per-worktree manifest lock no longer covers what stays shared: the legacy global manifest, the
global directories it owns, and `info/exclude`, which Git resolves to one file for the repository and
all its worktrees.

Two further locks, both in the config root, and always acquired in this order: the migration lock,
then the repository lock, then the per-worktree manifest lock. No path takes them in another order.

The **repository lock** covers the read-modify-write of the exclusion block, which Git resolves to one
file for the repository and all its worktrees.

The **migration lock is machine-wide and single-keyed**, not per repository, because legacy ownership
crosses repositories. Two repositories under two different keys would each observe the other still
owning a shared directory, each skip deleting it, and each then delete its own manifest, leaving the
directory globally visible with no manifest able to prune it. The whole legacy critical section —
scanning the other global manifests, deciding, removing entries and deleting the manifest — happens
under that one lock, so the last owner out is the one that deletes the files.

**Overlapping legacy ownership.** A global path carries no repository identity — `SkillDirFor`
combines the root with `kcap-<slug>` and nothing else — so two repositories' global manifests can
name the same directory, which is exactly what a project-homed skill produces. Migration therefore
removes a global directory only when no other global manifest under the config root still owns it;
otherwise it drops its own entry and leaves the files. Deleting a directory another repository still
serves would break the issue's promise that other repositories are untouched.

**A retired identity refines that rule rather than overriding it.** When an identity is retired and a
remaining owner records the same retired identity, the directory is deleted: nobody is serving it
under a live credential. It survives only while some other manifest owns it under a different, live
identity — a case the manifest records, and which resolves when that owner migrates or retires in
turn. The guarantee about previous-identity files is bounded accordingly in the acceptance criteria:
unconditional for copies this repository alone owns, conditional for a copy another repository is
still serving.

**Lock lifetime.** No shared lock is ever held across a network request. The per-worktree manifest
lock keeps today's lifetime, spanning the fetch for one target, and the manifest is re-read under it
after the fetch, as it is today. The migration lock covers only the legacy critical section, which is
local filesystem work, and the repository lock only the exclusion block's rewrite. Holding either
across a snapshot request would serialize unrelated repositories on someone else's network.

**Contention is reported, never counted as done.** Auto mode may skip a periodic refresh when another
process holds the lock, as it does today. It may not skip pending recovery or an identity retirement:
those run ahead of the six-hour throttle, and a run that could not take a lock it needed reports the
work as incomplete rather than returning success, so the next start retries it instead of assuming a
lock holder did it.

## Reconciliation and migration

The planner, the drift rule, the prune safety rule and the whole-snapshot slug validation are
unchanged. The same document delivered to several repositories is several independent
materializations keyed by one stable document id, which the planner already handles.

Migration runs under the machine-wide migration lock, after a successful repository-local sync for a
target:

1. Write the repository-local files, with ownership already recorded and publication atomic.
2. Save the repository-local manifest with `pending: false`.
3. Prune the entries the legacy global manifest owns, skipping any path another global manifest
   still owns.
4. Delete the legacy global manifest.

An interruption at any point leaves the global copy in place and the next sync retries, including a
sync that reaches step 3 by the no-change path.

## Git exclusion

One managed block in the worktree's `info/exclude`, delimited by marker comments and rewritten whole
under the repository lock, so it can be added and removed cleanly and cannot interleave with
another worktree's rewrite. Git resolves that file to the shared common directory, so a single block
covers the repository and all its worktrees.

The patterns name only kcap's own directories — `/.agents/skills/kcap-*/` and the three siblings —
never a whole tree, because a repository may have its own committed skills there. When the anchor is
not the repository root, the patterns are written relative to the root.

## What the probes measured, and what they did not

The probes wrote into each harness's **native** root. That is exactly the evidence for the reader
table above, which comes from the cross-vendor scenario. It is **not** evidence for two other
properties at the compatibility root this design mostly writes to:

- That both Git exclusions keep a skill loadable was measured at native roots, not at
  `.agents/skills`.
- That Pi, Kiro and Cursor anchor on the launch directory was measured at `.pi/skills`,
  `.kiro/skills` and `.cursor/skills`, not at `.agents/skills`.

Before either property is relied on for the compatibility root, the merged probe kit runs its
exclusion and nested-directory scenarios against `.agents/skills` for the harnesses that read it.
That is a small extension to an existing kit, and it is a task in the implementation plan rather
than an assumption in this design.

## Failure modes

All refuse loudly and none falls back to a global tree, since that fallback is what this issue
removes.

| Condition | Behaviour |
| -- | -- |
| Not inside a git repository | Existing refusal, unchanged |
| Git directory cannot be resolved | Refuse, naming the path |
| Anchor not writable | Refuse, naming the path |
| Destination escapes the anchor through a link | Refuse that target, naming the resolved path |
| Identity changed and the replacement fetch fails | Old catalogue already pruned; manifest owns nothing; next sync rebuilds |
| Snapshot contains an unsafe slug | Existing whole-snapshot refusal, nothing written |
| Manifest unreadable | Existing abort; a corrupt manifest is still moved aside and re-synced |

## Testing

- **Pure units.** Anchor to root per vendor; manifest path for a main checkout and for a linked
  worktree; an identity change keeping ownership while discarding the etag; the exclusion block
  idempotent to add and complete to remove; consumer-based adoption for an Antigravity-only and for a
  Gemini-only machine; home and applicability round-tripping through the manifest.
- **A real temporary repository with a linked worktree.** Two worktrees receive independent
  materializations; a second repository receives none of the first's skills; user-authored skills
  survive a prune; generated files are untracked and still readable; a destination symlinked outside
  the anchor is refused.
- **Interruption and concurrency.** A crash after the write and before the manifest save, with the
  snapshot changing before the retry, still prunes the orphan; a crash straight after the pending
  save and before each prune, starting from a skill that is then renamed or revoked, still deletes
  the old path from the journal; two worktrees migrating concurrently serialize; two repositories
  migrating concurrently through final cleanup leave no ownerless global directory; an account switch
  after an interrupted migration, whose replacement fetch then fails, leaves no previous-identity
  files either locally or globally, except a global copy another repository still serves, which is
  recorded; an anchor change with an identical snapshot and etag still materializes the new paths and
  removes the old ones; a pending move from one anchor to another, resumed at a third, prunes the
  first anchor's paths through the roots recorded beside them, with the identity unchanged and with
  it changed, and with the replacement fetch failing; two repositories owning one global copy through
  an account switch leave it exactly when a live owner remains; a repository needing recovery while
  another holds a lock across a slow fetch reports incomplete work and retries rather than reporting
  success; a document renamed away and back across a crash, and an anchor moved away and back, both
  keep the live copy and clear the stale intent.
- **The command path end to end against a mocked snapshot API.** Manifest read, fetch, plan, write,
  exclude, save, migrate, including the no-change and 304 paths, a same-profile account replacement,
  and a failed replacement fetch. `SyncTargetAsync` has no test today, so this is new coverage rather
  than a rewrite.

`SkillsTargetCatalogTests` pins the four keys, their vendors and that each root's leaf directory is
`skills`; it is updated for anchor-relative roots, the `Consumers` and `Readers` fields, and
consumer-based adoption.

## Out of scope

- Automatic delivery at session start, including the reload and registration routes the probes
  measured (#962).
- Server-side coverage deciding which repositories a document answers for (AI-2525). A project-homed
  document already arrives in each member repository's snapshot, so no new client concept is needed;
  what this design adds for it is preserving the home and applicability metadata, and refusing to
  delete a global copy another repository still owns.
- The bundled-skills installer, a separate system that writes similarly named directories into the
  global trees. Once this output is repository-local the two no longer share a directory.

## Acceptance criteria

- [ ] A sync in repository A leaves a session in repository B with none of A's skills.
- [ ] Two worktrees of one repository have independent, correct materializations and independent
      manifests, and two concurrent migrations do not race.
- [ ] A vendor-restricted skill that this design can fetch is delivered, and its manifest records
      every harness that can read the tree it landed in. A restriction to a vendor with no vendored
      tree is recorded as undeliverable rather than silently dropped.
- [ ] New, changed, renamed, revoked, missing and hand-edited managed files reconcile without
      touching authored skills, including a file orphaned by a crash before the manifest was saved.
- [ ] Generated files are untracked, and discovery plus full-body loading still pass for a harness at
      the tree actually written to.
- [ ] Manifest-owned global copies migrate away, an interrupted migration is retryable, a copy
      another repository still owns is left alone, and two repositories finishing at once leave no
      copy that no manifest can prune.
- [ ] A credential, server or account change neither reuses another identity's conditional request
      nor leaves its catalogue in place, including when the replacement fetch fails and when a
      migration was interrupted before it. Unconditional for every local copy and every global copy
      this repository alone owns; a global copy another repository still serves under a live identity
      survives, and the manifest records that it did.
- [ ] A sync at a different anchor materializes the new paths and removes the old ones, even when the
      snapshot and its etag are unchanged.
- [ ] Server-provided home and applicability metadata survive materialization, with the repository
      home derived for servers that do not send it.
