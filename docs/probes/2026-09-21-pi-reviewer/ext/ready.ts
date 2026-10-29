// Measures the load lifecycle a reviewer extension depends on: whether Pi waits for an async factory
// before serving RPC, what a throwing factory does, and whether `session_start` can report the tool
// set Pi will actually offer. Behaviour is switched by env so one file serves every arm.
import { writeFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export default async function (pi: any) {
  const dir = process.env.PROBE_MARK_DIR!;
  mkdirSync(dir, { recursive: true });

  await sleep(Number(process.env.PROBE_FACTORY_DELAY_MS ?? "0"));
  if (process.env.PROBE_FACTORY_THROW === "1") throw new Error("probe-factory-refused");

  for (const name of ["submit_review_result", "read_file"]) {
    pi.registerTool({
      name,
      label: name,
      description: name,
      parameters: { type: "object", properties: {} },
      async execute() {
        return { content: [{ type: "text", text: "ok" }] };
      },
    });
  }
  writeFileSync(join(dir, "factory-done"), String(Date.now()));

  pi.on("session_start", async () => {
    const all = pi.getAllTools().map((t: any) => t.name).sort();
    const active = [...pi.getActiveTools()].sort();
    writeFileSync(join(dir, "ready.json"), JSON.stringify({ at: Date.now(), active, all }));
  });
}
