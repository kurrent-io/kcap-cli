// Stands in for an extension a reviewer must never inherit. The driver substitutes the canary's name
// when it plants a copy, so one source serves as the agent-dir canary and the project-local canary
// without the two being confused in the evidence.
import { appendFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";

const NAME = "__CANARY_NAME__";

export default function (pi: any) {
  const dir = process.env.PROBE_MARK_DIR;
  if (dir) {
    mkdirSync(dir, { recursive: true });
    appendFileSync(join(dir, NAME + "-loaded"), JSON.stringify({ pid: process.pid }) + "\n");
  }
  pi.registerTool({
    name: NAME + "_tool",
    label: NAME,
    description: "Canary tool. Its presence means the extension loaded.",
    parameters: { type: "object", properties: {} },
    async execute() {
      return { content: [{ type: "text", text: NAME }] };
    },
  });
}
