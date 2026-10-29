# Claude reviewer restart resume: live probe

Does a Claude review-flow reviewer in a daemon-owned worktree come back across a daemon restart,
under the same agent id, remembering its earlier rounds?

## Setup

- Daemon `alexey` under launchd, built from this branch at `47a18819` (`kcap 1.4.0-beta.4.42`),
  installed over the npm platform package (`kcap`, `kcap-daemon`, `libpty_shim.dylib`); the
  released 1.4.0-beta.4 binaries are backed up in `~/.config/kcap/probe-backup-1872/`
- Claude Code 2.1.309
- Flow: `spec-review`, reviewer vendor `claude`, run from a hosted interactive session of the same
  daemon

## Steps and outcomes

| Step | Outcome |
|---|---|
| Install branch build, `kcap daemon restart` | interactive resume failed for the two agents whose ledger held a dashless vendor session (learned by transcript discovery after an earlier `kcap continue` fallback): Claude got `--resume <32-hex>`, read it as a picker search, and the pasted nudge landed in it. Fixed in `a34d4d1b` (launcher passes the dashed form) |
| Reinstall with the fix, restart: the same two agents resume | failed: the server refused all three ids (`recorded_failure`) and the daemon continued them under new ids. After the previous restart's resume, the hook-side sweep posted `parent_exited` for each old process: their claims, written by the released kcap, record no agent id, so the fence could not place them, and the server's death bridge reaped the resumed agent by session id. Fixed in `02672eec` (the fence finds the agent by session) |
| Round 1 | flow `6889c9b8`, reviewer `d4b638ab`, participant entry running, vendor session `4736ee85…`; findings returned |
| Restart between rounds: same reviewer agent id | daemon side passed (`1.4.0-beta.4.45`): `d4b638ab` relaunched with `--resume 4736ee85…`, answered the nudge with round 1 in context, no registration refused; this session also resumed in place |
| Round 2 recalls a detail only round 1 saw | failed: the server launched a fresh reviewer `913fde9c` (session `83082113`) for round 2 without sending `d4b638ab` any input, then stopped `d4b638ab`; the fresh reviewer had no round 1. The successor's orphan reap had reported the old process gone with its flow run and role, so the server cleared the role. Fixed in `b8a40e09` (a reap of a participant that resumes under its id names no flow role) |

## Rerun on the merged build

Built from this branch after merging `main` (`kcap 1.4.1-alpha.0.19`, Claude Code 2.1.312). Each
round 1 gave the reviewer a codeword and colour that appear in no file and no other session.

| Step | Outcome |
|---|---|
| Restart between rounds (flow `7cf02ded`, reviewer `be7b098b`) | failed: the stop marked the entry resuming, but the successor failed it at boot without a local log line. The reason, once logged (`da118cc7`), was `account_unavailable`: the entry records the default account a reviewer runs on, the resume command named it, and a review-flow launch that names an account is refused. Fixed in `4ef2ec0e` (a participant's resume command names no account) |
| Restart between rounds (flow `65cae2a9`, reviewer `ef1af894`) | passed: relaunched under the same id with the same Claude session `029473b2…`; entry running → resuming → running; no registration refused, no fresh reviewer |
| Round 2 recalls round 1's codeword and colour | passed: answered both from memory; its only tool call was the result submit |
| `kcap daemon restart --force` mid-round (round 3, 106 of 125 file reads done) | passed: same id and session; the reviewer took up the round where it stopped, finished it, recalled the codeword and colour, and submitted once |
