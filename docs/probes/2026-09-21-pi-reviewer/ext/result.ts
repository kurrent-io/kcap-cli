// Probe stand-in for a reviewer result channel: registers one LLM-callable tool and records
// every load and every call under PROBE_MARK_DIR, so the driver reads facts off disk instead of
// trusting a model's account of what happened.
import { appendFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";

function mark(name: string, payload: unknown) {
  const dir = process.env.PROBE_MARK_DIR;
  if (!dir) return;
  mkdirSync(dir, { recursive: true });
  appendFileSync(join(dir, name), JSON.stringify(payload) + "\n");
}

export default function (pi: any) {
  mark("result-loaded", { pid: process.pid });
  pi.registerTool({
    name: "submit_review_result",
    label: "submit review result",
    description: "Report the review verdict.",
    parameters: {
      type: "object",
      properties: {
        round_token: { type: "string" },
        kind: { type: "string", enum: ["findings", "clean"] },
      },
      required: ["round_token", "kind"],
    },
    async execute(toolCallId: string, params: any) {
      mark("result-called", {
        toolCallId,
        params,
        flowAgentId: process.env.KCAP_FLOW_AGENT_ID ?? null,
      });
      return { content: [{ type: "text", text: "recorded" }] };
    },
  });
}
