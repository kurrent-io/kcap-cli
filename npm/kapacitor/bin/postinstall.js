#!/usr/bin/env node

// Runs after `npm install -g @kurrent/kapacitor` (including upgrades).
//
// Refreshes installed kapacitor agent skills under ~/.agents/skills/ so users
// pick up new or updated skills without manually re-running `kapacitor setup`.
//
// Contract:
// - `--if-installed` makes this a no-op when no marker file exists (fresh
//   install, user hasn't completed setup yet). Setup is still the path that
//   stamps the marker; postinstall only refreshes what setup put there.
// - Any failure exits 0. A failed refresh must never break `npm install`.

const { spawnSync } = require("child_process");
const path = require("path");

const launcher = path.join(__dirname, "kapacitor.js");

try {
  const result = spawnSync(
    process.execPath,
    [launcher, "plugin", "install", "--skills", "--if-installed"],
    { stdio: "ignore", env: process.env }
  );

  // Swallow non-zero exits silently — the user can still run
  // `kapacitor plugin install --skills` by hand if something went wrong.
  if (result.error) {
    // Likewise: any spawn error is non-fatal here.
  }
} catch {
  // Never fail npm install.
}

process.exit(0);
