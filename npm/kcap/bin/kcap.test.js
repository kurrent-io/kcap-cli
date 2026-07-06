const assert = require("node:assert");
const path = require("node:path");
const {
  resolveInstallSpec,
  probeArgs,
  trashDirFor,
  trashDirFromLauncher,
  filterKcapProcesses,
  describeRole,
} = require("./kcap.js");

assert.strictEqual(resolveInstallSpec({ install_tag: "beta" }), "@kurrent/kcap@beta");
assert.strictEqual(resolveInstallSpec({ install_tag: "latest" }), "@kurrent/kcap@latest");
assert.strictEqual(resolveInstallSpec({}), "@kurrent/kcap@latest");          // missing → latest
assert.strictEqual(resolveInstallSpec(null), "@kurrent/kcap@latest");        // no probe → latest
assert.strictEqual(resolveInstallSpec({ install_tag: "" }), "@kurrent/kcap@latest");

assert.deepStrictEqual(probeArgs([]), ["update", "--check", "--no-update-check"]);
assert.deepStrictEqual(probeArgs(["--beta"]), ["update", "--check", "--no-update-check", "--beta"]);
assert.deepStrictEqual(probeArgs(["--stable"]), ["update", "--check", "--no-update-check", "--stable"]);
assert.deepStrictEqual(probeArgs(["--foo", "--beta", "-x"]), ["update", "--check", "--no-update-check", "--beta"]); // only channel flags forwarded
assert.deepStrictEqual(probeArgs(undefined), ["update", "--check", "--no-update-check"]); // defensive

// trashDirFor: sibling of node_modules (same volume as the install tree).
assert.strictEqual(
  trashDirFor(path.join("C:", "npm", "node_modules")),
  path.join("C:", "npm", ".kcap-trash"),
);

// trashDirFromLauncher: derives the same dir from the launcher's location…
assert.strictEqual(
  trashDirFromLauncher(path.join("C:", "npm", "node_modules", "@kurrent", "kcap", "bin")),
  path.join("C:", "npm", ".kcap-trash"),
);
// …and refuses non-node_modules layouts (dev checkout, packed tarball).
assert.strictEqual(trashDirFromLauncher(path.join("C:", "git", "kcap", "npm", "kcap", "bin")), null);

// filterKcapProcesses: keeps only processes under the install root
// (case-insensitive), tolerates a bare object (ConvertTo-Json unwraps
// single-element arrays), null entries, and missing ExecutablePath.
const installRoot = "C:\\Users\\u\\AppData\\Roaming\\npm\\node_modules\\@kurrent";
const exePath = `${installRoot}\\kcap\\node_modules\\@kurrent\\kcap-win-x64\\bin\\kcap.exe`;
assert.deepStrictEqual(
  filterKcapProcesses(
    [
      { ProcessId: 11, ExecutablePath: exePath.toUpperCase(), CommandLine: `"${exePath}" mcp sessions` },
      { ProcessId: 22, ExecutablePath: "C:\\other\\kcap.exe", CommandLine: "kcap.exe mcp review" }, // foreign install
      { ProcessId: 33 },       // no path (access denied)
      null,                    // defensive
    ],
    installRoot,
  ),
  [{ pid: 11, name: "KCAP.EXE", role: "MCP server — open Claude Code/agent session" }],
);
assert.deepStrictEqual(
  filterKcapProcesses({ ProcessId: 7, ExecutablePath: exePath, CommandLine: `"${exePath}" hook --claude` }, installRoot),
  [{ pid: 7, name: "kcap.exe", role: "kcap process" }],
);
assert.deepStrictEqual(filterKcapProcesses(null, installRoot), []);

// describeRole: daemon beats mcp (the daemon exe name matches first), mcp
// detected as a word, everything else generic.
assert.strictEqual(describeRole("C:\\x\\kcap-daemon.exe run --name main"), "daemon");
assert.strictEqual(describeRole("kcap.exe daemon status"), "daemon");
assert.strictEqual(describeRole("kcap.exe mcp memory"), "MCP server — open Claude Code/agent session");
assert.strictEqual(describeRole("kcap.exe watch"), "kcap process");

console.log("ok");
