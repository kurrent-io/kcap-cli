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
| Install branch build, `kcap daemon restart` | pending |
| Round 1 | pending |
| Restart between rounds: same reviewer agent id | pending |
| Round 2 recalls a detail only round 1 saw | pending |
| `kcap daemon restart --force` mid-round: same agent id answers the round | pending |
