# Grok Build MCP name precedence probe — 2026-10-21

**Subject:** `grok 1.0.62 (1474f9cbdd91) [stable]`, macOS / arm64, `grok agent --no-leader stdio` in a throwaway git
repository. The user's `~/.cursor/mcp.json` registered `kcap-workitems` (and two other kcap servers) on a binary
that no longer exists. **Driver:** `probe_collision.py <workspace> <out-dir> mcp_tag.py`, which runs `initialize` and
`session/new` only (no model turn) with three stdio servers, each a `mcp_tag.py` that writes a marker file when it
starts. `KCAP_HOSTED_GROK` names the driver, so kcap's own Grok hook stands down.

## Arms

- **A.** The workspace also holds `.cursor/mcp.json` declaring `probe-dup`. `session/new` passes `kcap-workitems`,
  `probe-dup` and `probe-unique`.
- **B.** The same `session/new` in a workspace with no project config.

## Results

- **A `session/new` server runs in place of the same-named server Grok imports from the user's Cursor config.** In
  both arms the injected `kcap-workitems` started and reported `_x.ai/mcp/server_status` `ready`, while Grok's
  stderr showed spawn failures for the other two broken imported servers and none for `kcap-workitems`. The init
  total counted it once. `_x.ai/mcp/servers_updated` still lists the imported entry, so that notification is not
  evidence of which one runs; the spawn is.
- **An untrusted project-scope entry with the same name suppresses both.** In arm A neither `probe-dup` started
  (`session/new` passes through a `folder_trust` setup phase first). In arm B the injected `probe-dup` started.
  Agent worktrees have workspace MCP config removed, so this does not reach a hosted launch.
- **Grok gives every server it starts its own session id.** Each marker recorded `GROK_SESSION_ID` equal to the
  `sessionId` `session/new` returned.
