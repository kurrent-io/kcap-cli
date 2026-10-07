const assert = require("node:assert");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

// ── require-order regression ─────────────────────────────────────────────────
// refresh.js is required by runUpdate BEFORE kcap.js's final module.exports
// assignment, so it must never require kcap.js (it would see partial exports).
// This require runs first in this process; the cache proves the independence.
const { runRefreshes } = require("./refresh.js");
assert(
  !Object.keys(require.cache).some((k) => k.endsWith(`${path.sep}kcap.js`)),
  "refresh.js must not (transitively) require kcap.js",
);

// A stand-in launcher that records the argv it was given. The space in the
// directory name: the spawn must survive spaced prefixes.
function makeFakeLauncher(body) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "kcap refresh test-"));
  const launcher = path.join(root, "kcap.js");
  const record = path.join(root, "argv.json");
  fs.writeFileSync(launcher, body(record));
  return { root, launcher, record };
}

// ── runRefreshes hands the whole refresh to `kcap refresh` ───────────────────
{
  const { root, launcher, record } = makeFakeLauncher((rec) =>
    `require("fs").writeFileSync(${JSON.stringify(rec)}, JSON.stringify(process.argv.slice(2)));\n`);
  try {
    runRefreshes(launcher);
    assert.deepStrictEqual(JSON.parse(fs.readFileSync(record, "utf8")), ["refresh"]);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

// ── a failing refresh never throws to the caller ─────────────────────────────
{
  const { root, launcher } = makeFakeLauncher(() => "process.exit(3);\n");
  try {
    assert.doesNotThrow(() => runRefreshes(launcher, () => {}));
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

// ── a failing refresh passes its stderr on, with a hint to retry ──────────────
{
  const { root, launcher } = makeFakeLauncher(() =>
    `process.stderr.write("Some agent integrations were not refreshed:\\n  kcap plugin install --codex --if-installed: exit 1\\n");\nprocess.exit(1);\n`);
  try {
    const warnings = [];
    runRefreshes(launcher, (m) => warnings.push(m));
    assert.strictEqual(warnings.length, 2);
    assert.match(warnings[0], /--codex --if-installed: exit 1/);
    assert.match(warnings[1], /run `kcap refresh` to retry/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

// ── a clean refresh says nothing ─────────────────────────────────────────────
{
  const { root, launcher } = makeFakeLauncher(() => "process.exit(0);\n");
  try {
    const warnings = [];
    runRefreshes(launcher, (m) => warnings.push(m));
    assert.deepStrictEqual(warnings, []);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

// ── nor does a launcher that does not exist ──────────────────────────────────
assert.doesNotThrow(() => runRefreshes(path.join(os.tmpdir(), "kcap-no-such-launcher", "kcap.js"), () => {}));

console.log("refresh.test.js: ok");
