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
| Round 2 recalls a detail only round 1 saw | failed: the server launched a fresh reviewer `913fde9c` (session `83082113`) for round 2 without sending `d4b638ab` any input, then stopped `d4b638ab`; the fresh reviewer had no round 1 |
| `kcap daemon restart --force` mid-round: same agent id answers the round | pending |
