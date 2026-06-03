#!/usr/bin/env node

// Runs after `npm install -g @kurrent/kcap` (including upgrades).
//
// Refreshes user-scope kcap agent installations so users pick up
// new or updated skills, Codex hook commands, Cursor hook commands, and
// Claude plugin registration without manually re-running `kcap setup`.
//
// Contract:
// - Only runs on global installs. Skipping non-global installs avoids
//   touching ~/.agents/, ~/.codex/, ~/.cursor/, or ~/.claude/ during unrelated
//   local/transitive installs on already-opted-in machines.
// - Each refresh uses `--if-installed`, which no-ops unless the user
//   has previously opted in (marker file present OR pre-marker install
//   detected via existing kcap entries in the target file).
// - Each refresh runs independently. A failure, timeout, or unexpected
//   exit code from one does not prevent the others. The script always
//   exits 0 — a failed refresh must never break `npm install`.

const { spawnSync } = require("child_process");
const path = require("path");

const isGlobal =
  process.env.npm_config_global === "true" ||
  process.env.npm_config_location === "global";

if (!isGlobal) {
  process.exit(0);
}

const launcher = path.join(__dirname, "kcap.js");

// One entry per agent. Order is independent — each refresh is gated by
// its own marker.
const refreshes = [
  ["plugin", "install", "--skills", "--if-installed"],
  ["plugin", "install", "--codex",  "--if-installed"],
  ["plugin", "install", "--cursor", "--if-installed"],
  ["plugin", "install",             "--if-installed"], // Claude
];

for (const argv of refreshes) {
  try {
    spawnSync(process.execPath, [launcher, ...argv], {
      stdio: "ignore",
      env: process.env,
      // Hard ceiling so a stalled child can never hang `npm install`.
      // Each refresh is bounded independently.
      timeout: 60_000,
      killSignal: "SIGKILL",
      windowsHide: true,
    });
  } catch {
    // Never fail npm install.
  }
}

process.exit(0);
