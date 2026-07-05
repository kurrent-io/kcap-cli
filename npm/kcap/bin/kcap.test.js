const assert = require("node:assert");
const { resolveInstallSpec, probeArgs } = require("./kcap.js");

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

console.log("ok");
