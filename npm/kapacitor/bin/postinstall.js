#!/usr/bin/env node

// Runs after `npm install -g @kurrent/kapacitor` (including upgrades).
//
// Refreshes installed kapacitor agent skills under ~/.agents/skills/ so users
// pick up new or updated skills without manually re-running `kapacitor setup`.
//
// Contract:
// - Only runs on global installs. Skipping non-global installs avoids
//   touching ~/.agents/skills/ during unrelated local/transitive installs on
//   already-opted-in machines.
// - `--if-installed` makes this a no-op when no marker file exists (fresh
//   install, user hasn't completed setup yet). Setup is still the path that
//   stamps the marker; postinstall only refreshes what setup put there.
// - Any failure, timeout, or unexpected exit code exits 0. A failed refresh
//   must never break `npm install`.

const { spawnSync } = require("child_process");
const path = require("path");

const isGlobal =
  process.env.npm_config_global === "true" ||
  process.env.npm_config_location === "global";

if (!isGlobal) {
  process.exit(0);
}

const launcher = path.join(__dirname, "kapacitor.js");

try {
  spawnSync(
    process.execPath,
    [launcher, "plugin", "install", "--skills", "--if-installed"],
    {
      stdio: "ignore",
      env: process.env,
      // Hard ceiling so a stalled child can never hang `npm install`.
      // The launcher resolves the native binary and execs it; the full
      // refresh path is a tight loop of file copies, so 60s is plenty.
      timeout: 60_000,
      killSignal: "SIGKILL",
      windowsHide: true,
    }
  );
} catch {
  // Never fail npm install.
}

process.exit(0);
