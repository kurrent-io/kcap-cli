# One record per path kcap owns

Amends the ownership and crash-recovery sections of
`2026-09-18-repo-local-skill-materialization-design.md`. Everything else in that document stands:
the lock order, the absence of any shared lock across a network request, the target catalogue, the
exclusion block, and the overlap rule that protects a global copy another repository still owns.

## Why

The ledger keeps two collections. `skills` is keyed by document and carries the path that document
was written to; `pending_prunes` is keyed by path and carries the document it held. A third field,
`prune_anchors`, exists only to keep the second collection's paths authorisable after the first
collection's anchor field is overwritten.

Four rounds of review have each fixed defects here and introduced new ones of the same kind. Every
one was an instance of two things: the two collections disagreeing about which physical paths we
own, and a run persisting state that misleads the next.

These are not separate bugs. Ownership of a physical path is derived, per operation, from collections
keyed by something other than the path, recording intent rather than outcome.

## The record

One collection, `owned`, one row per physical path kcap has written or still owes something for.
Keyed by path. Replaces `skills`, `pending_prunes` and `prune_anchors`.

**Path equality is canonical and fails closed.** Two rows are the same row when their paths resolve
to the same location under the containment walk. A path that cannot be fully resolved matches
nothing and is never acted on.

Each row carries:

- **`path`**, **`root`**, **`anchor`** — the directory, the skills root it sits in, and the anchor
  that authorised it, all recorded from the run's own resolved anchor while it was live.
- **`origin`** — `repository` or `legacy`. A legacy row is authorised against that target's
  independently configured vendor legacy root, never against a repository anchor and never against
  the row's own recorded parent.
- **`confirmed`** — what we last *successfully* wrote: the file hash, and the document it served.
  Null only if no write has ever completed here.
- **`prepared`** — a write we have authorised and are about to perform, or performed without yet
  recording the outcome: the intended document, the intended file hash, and an operation id. Null
  when no operation is in flight.
- **`state`** — one of the five below.
- **`cause`** — why a deletion is owed: `superseded`, `revoked`, or `retired`. Null unless owed.
- **`identity_retired`** — the account a `retired` deletion belongs to, because the replacement
  catalogue is saved under the new account and nothing else would remember whose files these are.

These are the only legal combinations. Any other is a corrupt row: refuse it, report it, act on
nothing.

| `state` | `confirmed` | `prepared` | `cause` | what it is |
| -- | -- | -- | -- | -- |
| `reserved` | null | set or null | null | a destination that is ours to write; `prepared` set means an attempt is in flight, null means it is awaiting a retry |
| `published` | set | null or set | null | ours, verified; `prepared` present means an update is in flight |
| `unverified` | null | null | null | a converted legacy claim whose bytes we cannot vouch for |
| `owed` | set or null | null or set | set | a deletion we owe; null `confirmed` when the write never landed |
| `settled` | null | null | null | a directory left standing because it is not ours to remove |

`reserved` and `unverified` grant no deletion authority, never satisfy publication, and never make a
conditional request eligible.

**A reservation does not bootstrap ownership.** A destination may be reserved only when nothing is
there, or when the row being written is one we already own. An intended hash is never a reason to
take over a directory that already exists and is not ours.

**A reservation outlives the attempt that made it.** A first write that fails before the file exists
clears its operation and keeps the reservation, which is why `reserved` is legal with no operation in
flight. The destination stays ours, so the next run can retry it rather than finding a directory the
attempt created and refusing it as someone else's.

**A reservation is released when the reason for it goes away** — the document is no longer served, or
it is published somewhere else instead. Releasing removes the directory if we created it and it is
empty, and drops the row. If the directory is not empty, it holds something we did not write: the
row becomes `settled` and the directory stays. A reservation is never released merely because an
attempt failed.

## Publication is two operations and recovery must say which happened

Writing a file and saving the ledger cannot be made atomic. The record therefore has to let a later
run decide, from the rows alone, whether a write landed.

**Prepare, write, complete.** Before any byte is written, the row is saved with `prepared` set to the
intended document, the intended hash, and a fresh operation id, and `confirmed` left exactly as it
was. The file is then written. The row is then saved again with `confirmed` replaced by what was
written and `prepared` cleared.

**Recovery reads the file and compares three ways.** For a row with a `prepared` operation:

- the file hashes to `prepared.hash` — the write landed. Complete it: promote to `confirmed` and
  clear `prepared`.
- the file hashes to `confirmed.hash`, or is absent and `confirmed` is null — the write did not land.
  Discard `prepared` and retry the operation from the plan.
- the file is present and matches neither — somebody other than us wrote there. Refuse, keep
  `confirmed` as it stands, report the path. Do not adopt the bytes and do not delete them.
- the file is absent and `confirmed` is not null — nothing is there to judge. Absence is not evidence
  that a third party wrote anything. What follows depends on why we are here: an operation being
  retried reserves and writes again, and an operation being retired is cancelled, not replayed.

**An intended hash never authorises anything but completing its own operation.** It is not evidence
of ownership, it is not evidence for relocation to some other place, and it is not a licence to
delete.

**A write can land and then travel before it is recorded.** Prepare at A, write, crash, and the
checkout moves before the retry. The row still names A, whose file is now genuinely absent, while the
bytes sit at the corresponding path under the new anchor. Resolve it with the same two-sided test
relocation uses, applied to the operation rather than to `confirmed`: when A's path is positively
absent and the path the operation named under the new anchor holds `prepared.hash`, the operation
landed there. Complete it at that path.

This is bounded to the one destination the operation itself named. A match at any other path proves
nothing and authorises nothing.

## Ownership transfers on outcome

A refused write leaves the row exactly as it was: its `confirmed`, its document, its state and its
retirement cause all survive. Only the attempted operation is abandoned, and no conditional-fetch
cache advances on a run that refused one.

**A deletion becomes owed only against a `confirmed` replacement.** A row moves to `owed` when the
snapshot stops serving its document, or when a *directed supersession* has recorded that a specific
other row now serves it.

**Supersession is directed and committed with the publication that causes it.** Publishing a
document at a new path writes, in one ledger save, the new row's completion and the transition of
the specific rows it supersedes. Two rows for one document without a recorded direction is a corrupt
state, not a race to resolve later. Requesting a replacement never clears a retirement cause.

**An operation prepared under one account is resolved before the next one retires anything.** A
prepared operation carries the account and server it was authorised under, and keeps them until it
is resolved. When the run's identity differs from the operation's, the order is fixed and the
operation is never replayed:

1. Decide whether its bytes landed, by the rules above. Deleting first would compare the old
   `confirmed` against bytes the old account itself wrote and refuse its own file.
2. If they landed, record them as `confirmed` under the account that wrote them. This updates what we
   know was written; it does not publish, and it never clears a retirement cause or revives a row
   already retired.
3. If they did not, cancel the operation. Retrying it would republish the previous account's document
   in the middle of retiring that account.
4. Then retire the applicable rows, durably, before any fetch under the new identity.

## Deletion

**Only the managed file, only what we confirmed writing.** Delete `SKILL.md` and only `SKILL.md`,
and only when it hashes to the row's `confirmed.hash`, with the standing containment and link checks
applied to the path first. Then remove the containing directory only if it is empty. Never
recursive.

**Then reach a terminal outcome.** After the file is gone the row has no further deletion authority
and must not keep claiming the path. If the directory went too, drop the row. If the directory
remains because it holds something we did not write, the row becomes `settled`: it records that the
directory is not ours to remove, it grants no authority over any future file at that path, and it
does not make the target outstanding for the throttle. A live claim over a file that no longer exists
is the defect this rule exists to prevent.

**A file already absent is a completed deletion,** not a refusal. A file that is present but does not
match is a refusal: keep the row owed and report it.

**What this floor does and does not guarantee.** A bookkeeping mistake that survives this design
leaves a stale directory rather than removing a file we never wrote. Three limits are accepted
rather than claimed away:

- **A byte-identical copy of a managed file, at a path we own, is indistinguishable from ours** and
  will be deleted.
- **Genuine absence at a source plus matching bytes at a destination is consistent with a move**, and
  also with an independent copy whose source was deleted. Relocation accepts the ambiguity.
- **Nothing here is safe against a concurrent writer.** Hashing a file and then unlinking it are two
  operations, and an editor saving between them loses that save. Our locks exclude other kcap runs
  and nothing else. The guarantee holds only where the tree is not being modified underneath us, and
  an unattended background sync does not provide more than that.

## Relocation

A checkout can be renamed or moved with its files. The ledger lives inside that worktree's git
directory, so it travels with them.

**Relocation requires positively established absence at the old path and a match at the new one.**
Absence means we looked and nothing is there. A file that is present but does not match, a file we
cannot read, and a path we cannot resolve are each a different outcome, and none of them is absence.
In every one of those the row keeps its tuple and is reported. Content alone is not provenance, and
a vanished source is what distinguishes a move from a copy.

**Every row with a physical copy is a candidate** — `published`, `owed`, and a `reserved` or
in-flight operation through the rule above — because an owed row owns a copy too and a rename
interrupted between write and prune leaves two. A `settled` row is never a candidate: it holds no
claim and relocation must not give it one.

**A row keeps its old tuple until one outcome or the other is verified.** Re-anchoring in place
would discard the old path's ownership while its copy may still exist. When the old path is present
and matching, the anchor changed without the files moving: keep owning the old path and materialize
fresh at the new one.

## Reading what is already on disk

The pre-branch ledger has `etag`, `synced_at` and a `skills` array whose entries carry `doc_id`,
`slug`, `version`, `content_hash`, `path` and `file_hash`. No identity, no journal, no anchor. Every
existing installation has one, per repository and in the user-global trees this branch migrates away.

Each entry converts to a `published` row with `confirmed` taken from `file_hash` and its document,
`origin` set from where the ledger was found, and `anchor` null. An entry whose `file_hash` is
missing converts to an `unverified` row instead: it is a claim we cannot vouch for, so it is
reported and never deleted, and the current bytes on disk are never adopted as proof.

**A converted row's authority comes from its origin, not from its recorded parent.** A legacy row is
actionable only against that target's independently configured vendor legacy root, with the
direct-child and containment checks applied there. A converted row never satisfies repository-local
publication and never makes the repository's conditional cache eligible.

**Retirement intent is durable before the identity changes.** An account transition records the
obligation to retire outstanding legacy ownership, at the scope of the repository and target, before
the local catalogue's identity is replaced. That obligation survives a legacy ledger that cannot yet
be read or migrated, and is retried before any fetch until it is discharged.

**A co-owner relinquishing hands its evidence over, and hands it over by merging.** Under the
standing overlap rule the last owner out deletes the files. Two owners of one global path can hold
different receipts, because whichever wrote last is the one the bytes match. Replacing a survivor's
receipt with the leaver's loses the evidence exactly as often as keeping it does; only which owner
leaves first decides, and that is arbitrary.

So relinquishment merges. The survivor's row carries both receipts, and when it comes to delete it
uses whichever one the file actually matches. Choosing between hashes we have already recorded is
not the same as learning a hash from disk, which remains forbidden. If the file matches neither, the
row becomes `unverified`: reported, never deleted.

**The handoff is ordered and idempotent.** The survivor's merged evidence is saved before the
leaver's row is removed, and a crash between those two saves leaves a state the next run resolves
without loss. The migration lock serialises the owners; it does not make two ledger saves one.

Sibling scans and conversion recognise both ledger formats throughout.

## What the envelope keeps

Per ledger, unchanged in meaning: the repository and target it belongs to, the account and server it
was fetched under, the exposure, the etag and the refresh stamp. **Cache eligibility binds to
completed publication**: a conditional request is only eligible when every planned destination for
that identity reached `confirmed` at its intended path.

## What the implementation must hold to

- **One grouped save, one code path.** A completion and the supersessions it causes are one ledger
  replacement, and recovery uses the same code path as ordinary execution rather than a parallel one.
  A partially updated group is never visible.
- **An unresolved operation bypasses the throttle and forbids a conditional request.** A row with a
  `prepared` operation that has not been resolved makes its target outstanding, so the refresh
  interval cannot hide it, and no etag is sent until every planned destination for that identity has
  reached `confirmed` at the path it named.
- **Crash means process interruption.** These rules assume a ledger save is atomic through a rename
  and that a completed rename survives. Power loss, which can reorder or lose a rename that appeared
  to complete, is out of scope: recovery aims to leave a stale directory, not to be correct against
  an unordered store.

## Testing

Each of these must fail before the change and pass after.

- A crash after the file is written and before the outcome is saved completes on the next run rather
  than rewriting or orphaning.
- The same crash, followed by a checkout move before the retry, completes at the moved path rather
  than losing the written file.
- A first write interrupted after its reservation and before the file leaves a row the validator
  accepts, and the destination is not adopted from an intended hash.
- A first write that fails keeps its reservation, and the next run retries that destination rather
  than refusing the directory the failed attempt created.
- A reservation whose document stops being served is released, taking an empty directory with it and
  leaving a non-empty one standing with no claim over it.
- An operation prepared under one account and resumed under another is resolved without being
  replayed, and completing it does not clear or revive a retirement.
- A source that is present but edited, or unreadable, is not treated as absent, and the row keeps
  its old path.
- A crash between the prepared save and the write retries, and does not adopt whatever is on disk.
- A third party's bytes at a prepared path are refused, reported, neither adopted nor deleted.
- A refused write leaves the previous row's content, document and retirement cause intact, and does
  not advance the conditional cache.
- A refused write does not let the deletion of what it would have replaced proceed.
- A rename interrupted between the write and the prune, followed by a checkout move, relocates both
  copies and removes the stale one.
- An anchor change while the old files remain keeps owning them and materializes fresh.
- A directory holding an authored file beside ours loses only ours, and the row then holds no claim.
- A ledger in the pre-branch shape, with no identity and no anchor, converts and then migrates, and a
  converted entry without a file hash is reported rather than deleted.
- An account transition whose legacy cleanup is blocked retries that retirement on a later run
  without fetching, after the blocker clears.
- A co-owner relinquishing a shared global path leaves the survivor able to retire it, whichever
  owner wrote the bytes and whichever leaves first, and a crash between the two saves loses nothing.
