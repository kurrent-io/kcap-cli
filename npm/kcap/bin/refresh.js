// Refreshes user-scope kcap agent installations so users pick up new or updated
// skills, Codex/Cursor/Copilot hook commands, and Claude plugin registration.
//
// Shared by:
// - postinstall.js — runs after `npm install -g @kurrent/kcap` (incl. upgrades),
//   when the package manager allows install scripts to run.
// - kcap.js `update` — runs after a user-initiated `kcap update`, which works
//   even when the package manager blocks postinstall scripts.

const { spawnSync } = require("child_process");

// One entry per agent. Order is independent — each refresh is gated by its own
// marker via `--if-installed`, which no-ops unless the user has previously
// opted in (marker file present OR pre-marker install detected).
const REFRESHES = [
  ["plugin", "install", "--skills",  "--if-installed"],
  ["plugin", "install", "--codex",   "--if-installed"],
  ["plugin", "install", "--cursor",  "--if-installed"],
  ["plugin", "install", "--copilot", "--if-installed"],
  ["plugin", "install",              "--if-installed"], // Claude
];

// Runs each refresh via the given launcher (an absolute path to kcap.js).
// Each refresh runs independently: a failure, timeout, or unexpected exit code
// from one never prevents the others and never throws to the caller — a failed
// refresh must never break `npm install` or report `kcap update` as failed.
function runRefreshes(launcherPath) {
  for (const argv of REFRESHES) {
    try {
      spawnSync(process.execPath, [launcherPath, ...argv], {
        stdio: "ignore",
        env: process.env,
        // Hard ceiling so a stalled child can never hang the caller.
        timeout: 60_000,
        killSignal: "SIGKILL",
        windowsHide: true,
      });
    } catch {
      // Never fail the caller.
    }
  }
}

module.exports = { runRefreshes };
