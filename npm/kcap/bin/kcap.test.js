const assert = require("node:assert");
const { resolveInstallSpec } = require("./kcap.js");

assert.strictEqual(resolveInstallSpec({ install_tag: "beta" }), "@kurrent/kcap@beta");
assert.strictEqual(resolveInstallSpec({ install_tag: "latest" }), "@kurrent/kcap@latest");
assert.strictEqual(resolveInstallSpec({}), "@kurrent/kcap@latest");          // missing → latest
assert.strictEqual(resolveInstallSpec(null), "@kurrent/kcap@latest");        // no probe → latest
assert.strictEqual(resolveInstallSpec({ install_tag: "" }), "@kurrent/kcap@latest");
console.log("ok");
