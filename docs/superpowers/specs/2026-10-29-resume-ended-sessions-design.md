# Resume or hand off an ended session from the desktop app

## Intent

The desktop app shows ended sessions (the past-sessions feature), but their chat is read-only: the
input bar is replaced by "Read-only: This session has ended". Picking work back up means leaving the
app, finding the right directory and vendor, and re-explaining the context. The goal is to continue
an ended session straight from its input bar, in one of two ways:

- **Resume**: natively resume the same vendor session (`claude --resume`, `codex resume`, ...) so
  the conversation carries on in place, on the same kcap session.
- **Hand off**: start a new session that takes over the ended one's context, work items and plans.
  It works for any session the user has full access to: their own from another machine, or a
  teammate's.

## Goals

- An ended session's input bar is enabled, with Resume and Hand off where Send normally is.
- What the user types is the first message of the resumed or handed-off session. An empty box is
  allowed: Resume then just relaunches, and Hand off uses the default continuation prompt.
- Resume is offered only when it can work: the session is the viewer's own, it was recorded on this
  machine, and its working directory still exists here.
- Hand off is offered for every session the viewer has full access to, and lets the user choose
  vendor, model, effort and permissions the way the main launcher does.
- A failed Resume falls back to Hand off, with the same text, and says so.

## Non-goals

- Recreating a deleted worktree to make Resume possible. When the original directory is gone,
  Resume is not offered: Hand off is the answer.
- Forking from a mid-session turn (the past-sessions spec's slice 4).
- Changing vendor, model or permissions on Resume. Resume relaunches the session's own vendor.
- Changing whether ordinary launcher launches create a `capacitor/agent-*` branch.

## Anti-goals

- Offering Resume where it cannot work, so the user clicks it and waits for a failure. The fallback
  exists for genuine failures (the vendor lost the session), not as the normal path.
- Losing what the user typed: a failure on either path, before or after the agent appears, keeps
  the text in the box or carries it into the fallback.
- A hand-off that silently starts from the wrong code, or that claims the previous session's work is
  all there when that cannot be shown.
- Degrading silently: a server or daemon that does not understand a resume or hand-off launch must
  refuse it, never run it as an ordinary launch.
- Deleting anything the feature did not create: a branch, a directory, the vendor's project link.

## Behaviour

### What the input bar offers

| Session | Resume | Hand off |
|---|---|---|
| Mine, recorded on this machine, directory exists | yes | yes |
| Mine, recorded on this machine, directory gone | no | yes |
| Mine, recorded on another machine | no | yes |
| Someone else's, full access | no | yes |
| Someone else's, below full access | no | disabled, with the reason |
| Vendor or session without a usable native resume id | no | yes |
| Server or local daemon without support for the feature | no | disabled, with the reason |

"Mine" is `OwnerIsViewer == true`. "Recorded on this machine" compares the session's machine id
(new, from the server: see Constraints) with this app's own machine id. "Directory exists" is a
local check of the session's working directory when the composer is shown.

**Below full access**, the listing withholds branch, working directory, last prompt and PR links,
and the chat lease is denied. Hand off is shown disabled ("You need full access to this session to
hand it off") rather than launched from unknown code or through a widened data boundary. *My call.*

### Layout (option C from the mockups)

Mockups: https://kurrent.kcap.ai/pages/8440352f2e6244d294f8ba5b568bf69e

- The composer card looks like a live session's: attach button, text box, bottom row. The
  "Read-only" banner is not shown for ended sessions the viewer can continue.
- Bottom row: hint text, then **Resume** (when offered) and **Hand off** (primary).
- Clicking Hand off the first time opens a strip above the text box with the launcher's agent
  (vendor and model), effort and permission chips (and the Codex sandbox/approval chips for Codex),
  labelled with where the new session will start (see Hand off, "Resolving the starting code"). The
  button becomes **Start hand-off** and a **Cancel** ghost button appears; Cancel or Esc collapses
  the strip. While the strip is open, Resume is hidden.
- When Resume is not offered, the strip is open from the start and the button reads
  **Start hand-off**.
- The strip's chips start from the ended session's vendor and the user's launcher defaults for that
  vendor. Reuse the launcher's chips and pickers (`LauncherPaneView`) rather than building new ones;
  they follow the `kcapChip` / `kcapPanel` styling rules in CLAUDE.md.
- Resume shows no settings. It relaunches with the user's launcher defaults for that vendor's
  model, effort and permission mode (the ended session does not record them), under the session's
  recording account. *My call.*

### Resume

**Where it runs.** In the session's own working directory, **in place**: no worktree is created, and
nothing is deleted when the resumed agent ends. That directory is either the user's own checkout (a
terminal or borrowed session) or a daemon-owned worktree that was retained because it held unsaved
work. A cleanly ended daemon worktree is deleted, fails the "directory exists" check, and is never
offered.

**In place is a new launch mode, not `Borrowed`.** *My call.* `Borrowed` is the borrowed-review
containment path: Codex is forced read-only, ACP presets are refused, and Cursor/Copilot get a
snapshot worktree instead of the real directory. A resumed session must be writable in the real
directory, so it gets its own work location with the same permission, posture and preset rules as a
normal interactive launch. It passes the consent gate like any server-driven launch.

An in-place path is **protected while its agent is active or recoverable**, independently of whether
the daemon may delete it: the startup orphan sweep must not remove a retained daemon worktree that a
resumed agent is using, even after that worktree becomes clean. Directory ownership (may delete) and
execution (must not sweep) are tracked separately in the resume ledger.

**Offered only when all of these hold**, in addition to the table above. *My calls:*

- The native resume id is known. For Claude, Codex and the ACP vendors it equals the listed session
  id, except where the server listed an opaque ACP id in its encoded `senc1…` form, which cannot be
  turned back into the native id: those sessions do not offer Resume. Vendors whose resume token is
  not the session id (Pi's session file, Muse, Antigravity) do not offer Resume yet.
- The local daemon advertises in-place resume for that vendor, and the server advertises the resume
  launch (see Capability negotiation).
- The session has a hard `Ended` status, not just stale. The daemon also refuses when it finds the
  vendor session still alive locally, so two agents never share one transcript.
- The daemon would be allowed to work in the directory (its `AllowedRepoPaths` / git-repository
  rule, as `ProbeBorrowSource` checks for borrows). The allowlist is not relaxed for Resume.
- The session's **vendor session store** is the one Resume will use. A vendor keeps sessions per
  config directory, so another store would not find the session. The session's **recording store**
  is recorded with it: the account id when there was one, and always the physical config directory
  it ran under (resolved, so a symlink retarget or a changed environment default is detectable). An
  unknown recording store is not the same as the default: it means no Resume. Resume is offered
  only when the daemon resolves the recorded store the way restart recovery does
  (`AccountResolution.ResolveForResume`, which keeps the recorded directory rather than choosing
  today's default) and that directory still exists and is the same physical directory. This covers
  both account-registry vendors (Claude, Codex) and vendors without account support (the ACP
  vendors), which record their default directory.

**How it launches.** A resume is not an ordinary launch with `ResumeSessionId` set. It goes through
the same protocol as the daemon's restart resume (`NativeResumeAsync` / `SurvivesReadinessAsync`):
the new runtime is bound to the **original** kcap session from the start, and its transcript is
located by the native id. For Codex PTY in particular, the agent's session id is initialised from
the validated source session rather than from fresh-rollout discovery, because
`CodexSessionRolloutLocator` skips rollouts that predate the spawn and only scans recent days. Both
an empty and a non-empty first message must work for a session that is a week old.

**What counts as success.** The original server session is active again and accepting the resumed
agent's events. Registration alone is not success: the transcript bind (ACP) and the source claim
(Codex) happen after registration and can still be refused.

**Failure.** Any failure, before or after a live row appears (launch refused, vendor session
missing, runtime exits, transcript bind refused, source claim refused), is reported to the app as a
resume failure with a reason. The daemon retires the failed runtime completely before reporting, so
the fallback cannot race it. The app then shows an info notice ("Couldn't resume this session here
(reason). Hand off starts a new session with its context."), keeps the text, and opens the hand-off
strip with Resume gone. *My call:* the fallback does not start the hand-off by itself; the user
clicks Start hand-off, so nothing launches that they did not ask for.

On success the open view stays on the same transcript and becomes live: the composer turns into a
normal live composer.

### Hand off

**Where it runs.** In a new daemon-owned worktree of the session's repository on this machine. The
daemon owns the **directory**; it owns a **branch** only if it created one. A pre-existing branch is
never deleted, by normal completion, a failed start, creation rollback or daemon recovery: branch
ownership is recorded separately from directory ownership in the worktree record and the resume
ledger. When there is no local checkout of the repository, Hand off is disabled with that reason.
*My call.*

**Resolving the starting code (before launch).** When the strip opens, the app asks the local
daemon to resolve the starting point. The result is shown on the strip and passed into the launch,
so what the user saw is what launches:

- **Inputs**: the session's repository identity, its recorded branch, and **one selected PR**. The
  server selects it (see Constraints): only PRs whose base repository is the session's repository
  are candidates (a session's links can span repositories); the open one wins, then the most
  recently updated. For the selected PR the server provides head repository, head ref, head SHA and
  whether the head repository is a fork of the base. A head repository that is neither the base nor
  a verified fork of it is not used; the recorded branch on origin is resolved instead.
- **Fetch** from the head repository. A fetch or authentication failure is an error the strip shows
  ("Couldn't reach origin: …"), with Start disabled. It is never treated as "the branch is gone".
- **Outcomes**, each with an exact SHA that the launched HEAD must equal:
  1. **The branch exists on origin.** Check out that branch at the fetched SHA, so pushes update the
     same branch and PR. This holds for a teammate's branch too. *My calls* for an existing
     same-named local branch:
     - absent: created at the fetched SHA, tracking origin. The hand-off created this branch, so it
       owns it, but it still never deletes it while it has commits not on origin;
     - equal to the fetched SHA: checked out as is;
     - a strict ancestor of the fetched SHA and not checked out anywhere: **fast-forwarded** to it,
       then checked out. A fast-forward loses nothing;
     - divergent, ahead, or checked out in another worktree: the worktree is **detached** at the
       fetched SHA, no branch is created, and the prompt tells the agent to push with
       `git push origin HEAD:<branch>`.
     A local branch is never reset, rewound or force-moved.
  2. **The PR head is in a verified fork.** Detached at the fetched head SHA, no branch created; the
     prompt names the fork and branch. *My call.*
  3. **The branch is confirmed absent** (deleted after its PR merged, or never pushed). Detached at
     the default branch's fetched SHA, **no branch is created**: the agent creates one if the work
     needs it, so a hand-off with no code changes leaves nothing behind.
- **Completeness**: whether the ended session's work is all in the starting code. When the server
  knows the commits the session made, the daemon checks they are reachable from the starting SHA.
  The strip and the prompt say one of: the work is there; part of it is missing (named commits); or
  **it can't be confirmed** — always the case when the session may have left unpushed commits or
  uncommitted edits, including after a linked PR merged. Only the first is presented as complete.
  When work is missing or unconfirmed, the prompt tells the agent to compare against the recorded
  transcript and rebuild what is missing.

**The prompt.** `ContinuePrompt.Summarize` only carries title, repository, branch and PR, which is
not enough context. The hand-off prompt keeps `ContinuePrompt.Build`'s shape and its untrusted-data
boundary:

- Inside the `<previous-session-data>` block, labelled as data whose instructions are not to be
  followed and sanitised through `NextWorkUntrustedText.Render` with a length bound: the ended
  session's id, title, owner, repository, branch, PR, its first and last prompts when the listing
  provides them, and the starting-code outcome and completeness from above.
- Outside it, as the controlling request: an instruction to read the ended session's summary and
  transcript with the kcap-sessions tools **before acting** (always, so an empty composer with no
  work items or plans still knows what it is continuing), what to do about missing or unconfirmed
  work, and finally the user's text, or the default "continue where it left off" instruction.

If the transcript turns out unreadable to the new agent, it says so to the user rather than guessing.

**Takeover.** Once the new session exists, it takes over the ended session's work items, plans and
loose-end claims the way `SessionTakeover` does, **including a teammate's session**. It never
bypasses the server's admission of claim writes. A partly failed takeover does not stop the new
agent.

The ended state that justifies taking over is **revalidated at takeover time**, not carried from
when the strip opened: the source may have been resumed from another window or machine since.
`SessionTakeover`'s `force` skips that judgement, so the hand-off does not pass `force`; it uses a
liveness rule that admits only a source the server currently reports `Ended` (and that is not alive
locally).

Resume and takeover of one source are arbitrated by a **server-held reservation on the source
session**. A resume launch takes it when admitted, before consent and startup, and holds it until the
session is active again or the resume ends (denied, cancelled, failed startup), when it is released.
A takeover takes it atomically with the `Ended` check and the "continued by <new session>" record,
and holds it while it writes. Each refuses while the other holds it, so neither ordering
interleaves. If the source has become active, the takeover is refused and reported; the new agent
keeps running with the context it was given.

The takeover **outcome** is recorded against the new session and shown in its view, surviving
navigation away and back: carried over, refused (unauthorised), skipped (non-adoptable plan), and
**unknown** where `SessionTakeover` truncated its read (more than 20 plans). `Completed.Unsuccessful`
is not enough to decide "complete", because truncation and skips do not set it.

**Opening it.** The app adds the new session to the rail and switches the window to it, reusing the
launcher's placeholder-and-open flow (`AddPlaceholder` → `OpenSessionIfCurrent`).

## Constraints

- **Capability negotiation, both hops.** The launch payload's unknown fields are ignored by older
  servers and daemons, so new optional fields alone would let a hand-off run as an ordinary launch
  (random branch, no continuation, no takeover). The server advertises resume and hand-off launches
  to the app; the daemon advertises them to the server and, for the pre-launch probes, to the app
  over local control (a new capability, assembled beside its handler per the append-only rule). The
  server refuses a resume or hand-off to a daemon that has not advertised it. The app hides Resume
  and disables Hand off when either hop lacks support. The existing local `resume/1` capability is
  the ledger-resume API and does not count.
- **Server data the listing must add** (kcap-server): the recording machine id, daemon name, vendor
  account id and resolved recording config directory; the selected PR (per Hand off's selection rule) with its head repository, head
  ref, head SHA and fork relationship; and, where known, the commits the session made. A missing
  machine id (older server) means "not this machine".
- **Server source reservation** (kcap-server): the per-source reservation that Resume and takeover
  share (see Takeover), released on every end of a resume attempt, and the `Ended` check plus the
  "continued by" record taken in the same step as a takeover's reservation.
- **Who produces the recording identity.** Machine id and recording store must be captured for
  **every** recorded session, not only daemon launches: a session started directly in a terminal
  has no daemon launch, and it is the main in-place Resume case. The SessionStart hook path
  (`ClaudeHookCommand`, `CodexHookCommand`, and the ACP vendors' equivalents) adds them. The store
  is derived from **vendor-supplied evidence first** — the transcript or rollout path the vendor
  hands the hook, as `ClaudeHookCommand.ClaudeLayoutFor` already does for accounts — because a hook
  can receive a scrubbed `CLAUDE_CONFIG_DIR` (`AccountPaths`). The inherited environment is used
  only when it agrees with that evidence or no evidence exists for a vendor that has no per-store
  layout. When the store cannot be established, it is recorded as **unknown**, never as the
  default. It is stored as a physical path plus the matching registered account id, if any. A daemon
  launch reports the same fields from its own resolution. Both carry through ingestion to the
  listing. Older records without them stay unknown: no Resume.
- **Launches go through the server** (`RequestLaunchAgentV2` → `LaunchAgentCommand`). New fields:
  the resume source session for Resume (with its in-place directory), the resolved starting point
  (ref, SHA, checkout shape) and continuation source session for Hand off. Each is trailing and
  optional per the wire-compat rule, and guarded by the capability negotiation above.
- **Every server-driven launch passes the consent gate.** The requester is always the app's signed-in
  user, launching on their own daemon.
- **In-place resume and pre-existing branches are never cleaned up** by the feature: no worktree
  deletion for an in-place path, no project-link deletion, no `git branch -D` of a branch the daemon
  did not create (`WorktreeManager.RemoveAsync` deletes the branch by default today).
- **The prompt never goes on the agent's command line**: the hand-off prompt is delivered like any
  hosted launch prompt.
- **No worktree pruning** is introduced by this feature (CLAUDE.md invariant).
- README and `help-*.txt` change only if a CLI surface changes.

## Acceptance cases

- Resume: Claude and Codex PTY sessions, a week old, with empty and non-empty first message; an ACP
  session with a plain id resumes, one listed as `senc1…` offers no Resume.
- Resume failure after a live row appears (ACP transcript bind refused, Codex source claim refused):
  the draft survives, the fallback opens, and no second runtime is left running.
- Resume of a retained daemon worktree; the tree is then pushed and cleaned, and the daemon
  restarted: the directory is not swept while the agent is active or recoverable.
- Capability gaps: new app with an old server, and new server with an old daemon. No ordinary launch
  results from a resume or hand-off request.
- Hand off, per starting-code case:

  | Case | Launches | HEAD | At | Branch created |
  |---|---|---|---|---|
  | Branch on origin, no local branch | yes | on the branch | fetched SHA | yes, owned |
  | Local branch equal to origin | yes | on the branch | fetched SHA | no |
  | Local branch strictly behind, not checked out | yes | on the branch (fast-forwarded) | fetched SHA | no |
  | Local branch divergent or ahead | yes | detached | fetched SHA | no |
  | Branch checked out in another worktree | yes | detached | fetched SHA | no |
  | Verified fork PR head | yes | detached | fetched head SHA | no |
  | Linked PR from an unrelated repository only | yes | resolved from the recorded branch, per the rows above | | |
  | Branch deleted after merge / never pushed | yes | detached | default branch's fetched SHA | no |
  | Fetch or authentication failure | **no** | | | |

- Hand off of a session linked to PRs in two repositories: the PR from the session's own
  repository is selected.
- Hand off where the source is resumed elsewhere after the strip opened: the takeover is refused and
  reported, the source keeps its work items.
- The reverse order: a Resume admitted and paused at consent, then a hand-off's takeover from
  another machine: the takeover is refused. Denying the consent releases the reservation and a
  retried takeover succeeds.
- End to end from a terminal: start Claude and Codex directly in a terminal (one under the default
  config directory, one under a non-default `CLAUDE_CONFIG_DIR` / `CODEX_HOME`), end them, and the
  desktop app offers Resume and resumes each in its own store.
- A terminal Claude session in a non-default config directory whose hook sees a scrubbed
  `CLAUDE_CONFIG_DIR`: the recorded store is the transcript's real store, or unknown (no Resume),
  never the default.
- A session recorded under an unregistered default directory whose configured directory or symlink
  target changes before Resume: Resume is not offered. A session with no recorded store: no Resume.
- A previous prompt containing the block's closing delimiter and instructions that contradict the
  user's text: it stays inside the data block, and the user's text governs.
- Resume eligibility through the daemon's account resolution: Claude under a registered
  non-default account; Claude whose recording account was removed (no Resume); an ACP session
  recorded with no account id.
- Hand off with work on a pushed branch plus later unpushed commits: the strip and prompt say the
  work is incomplete or unconfirmed, never complete.
- Hand off that ends normally, fails to start, and is recovered after a daemon restart: in each case
  a pre-existing branch and its commits remain.
- Empty-input hand-off of a session with no work items or plans: the agent reads the transcript
  before acting.
- A teammate's session below full access: Hand off disabled, nothing launched.
- Takeover with more than 20 plans, a non-adoptable plan, and a claim-write refusal: the new agent
  runs, and its view shows the itemised outcome.

## Left to the builder

- Where the hand-off prompt and takeover run: daemon-side after launch (like the restart fallback's
  `LaunchContinuationAsync` / `StartContinuationTakeover`), or server-side. Daemon-side reuses the
  most existing code.
- How the app checks "directory exists" and "local checkout exists" without blocking the UI thread.
- The probe's transport (local control directly, or through the server) within the capability rule.
- How the takeover outcome reaches the app (server-recorded and read with the session, or reported
  by the daemon), provided it survives navigation.
- The exact wording of hints and notices, within the copy shown in the mockups.
