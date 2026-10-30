const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "kcap.js"), "utf8");

function scenario({ newer = false, installed = true, upgradeStatus = 0, npmStatus = 0, reportFails = false } = {}) {
  const calls = [];
  const output = [];
  const module = { exports: {} };
  const prefix = path.resolve("/fixture/node_modules");
  const binary = path.join(prefix, "@kurrent/kcap-osx-arm64/bin/kcap");
  const fakeRequire = (name) => ({
    child_process: {
      execFileSync(exe, args) {
        calls.push([exe, args]);
        if (exe === "npm") return prefix;
        if (args[0] === "update") return JSON.stringify({ newer, current: "1.4.1", latest: newer ? "1.4.2" : "1.4.1", install_tag: "latest" });
        if (args[0] === "report-version" && reportFails) throw new Error("offline");
        return "";
      },
      spawnSync(exe, args) {
        calls.push([exe, args]);
        return { status: exe === "npm" ? npmStatus : upgradeStatus };
      },
    },
    path,
    fs: { realpathSync: (p) => p, existsSync: () => installed, accessSync() {}, constants: fs.constants },
    os: { homedir: () => path.resolve("/fixture/home") },
    "./resolve": {},
    "./refresh": { runRefreshes: () => calls.push(["refresh", []]) },
  }[name] ?? require(name));
  const context = {
    require: fakeRequire, module,
    __filename: path.join(prefix, "@kurrent/kcap/bin/kcap.js"),
    __dirname: path.join(prefix, "@kurrent/kcap/bin"),
    console: { log: (m) => output.push(m), error: (m) => output.push(m) },
    process: { platform: "darwin", env: {}, exit: (exitCode) => {
      process.stdout.write(JSON.stringify({ calls, output, exitCode, binary }));
      process.exit(0);
    } },
    binary,
  };
  vm.runInNewContext(source + "\nrunUpdate(binary, []);", context);
}

if (process.argv[2] === "scenario") scenario(JSON.parse(process.argv[3]));

function update(options = {}) {
  const result = require("node:child_process").spawnSync(process.execPath, [__filename, "scenario", JSON.stringify(options)], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
}

for (const [upgradeStatus, expectedExit] of [[0, 0], [3, 0], [4, 0], [1, 1], [2, 1], [null, 1]]) {
  const result = update({ upgradeStatus });
  assert.equal(result.exitCode, expectedExit);
  assert.deepEqual(result.calls.filter(([, args]) => args[0] === "local"), [
    [result.binary, ["local", "upgrade", "--if-installed", "--no-update-check"]],
  ]);
  assert.equal(result.calls.some(([exe, args]) => exe === "npm" && args[0] === "install"), false);
  assert.equal(result.calls.some(([exe]) => exe === "refresh"), false);
  if (upgradeStatus === 4 || expectedExit === 1) assert(result.output.some((m) => m.includes("kcap local upgrade")));
}

assert.equal(update({ installed: false }).calls.some(([, args]) => args[0] === "local"), false);
for (const reportFails of [false, true]) {
  const result = update({ newer: true, reportFails });
  assert.equal(result.exitCode, 0);
  assert.deepEqual(result.calls.filter(([exe, args]) => exe === "refresh" || args[0] === "install" || args[0] === "local" || args[0] === "report-version"), [
    ["npm", ["install", "-g", "@kurrent/kcap@latest", "--prefer-online", "--ignore-scripts"]],
    ["refresh", []],
    [result.binary, ["local", "upgrade", "--if-installed", "--no-update-check"]],
    [result.binary, ["report-version", "--no-update-check"]],
  ]);
}
const failedInstall = update({ newer: true, npmStatus: 1 });
assert.equal(failedInstall.exitCode, 1);
assert.equal(failedInstall.calls.some(([exe, args]) => exe === "refresh" || args[0] === "local"), false);
console.log("ok");
