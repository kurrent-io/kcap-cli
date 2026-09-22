namespace Capacitor.Cli.Core.Harness.Pi;

/// <summary>
/// The MCP stdio client shared by kcap's Pi extensions: a minimal line-delimited
/// JSON-RPC 2.0 client over one child process, plus the process-wide child registry
/// and exit hook that keep teardown orphan-free across repeated per-session extension
/// loads. Embedded TypeScript, spliced into an extension's own <c>Header</c>/<c>Body</c>
/// constants — never used standalone.
/// </summary>
public static class PiMcpStdioClientSource {
    public const string Text =
        """
        // Process-wide child registry + a single exit hook, guarded on globalThis so repeated
        // per-session extension loads never accumulate process listeners (Node's MaxListeners).
        const KCAP_BRIDGE = ((globalThis as any).__kcapPiMcpBridge ||= { children: new Set(), exitHooked: false });

        function trackChild(child: any) {
          KCAP_BRIDGE.children.add(child);
          child.once("exit", () => KCAP_BRIDGE.children.delete(child));
        }

        function ensureExitHook() {
          if (KCAP_BRIDGE.exitHooked) return;
          KCAP_BRIDGE.exitHooked = true;
          // Synchronous-only backstop: an `exit` handler can't await the graceful ladder, so it
          // hard-SIGKILLs any child still tracked at process teardown (no orphans).
          try {
            process.on("exit", () => {
              for (const c of KCAP_BRIDGE.children) { try { c.kill("SIGKILL"); } catch { /* ignore */ } }
            });
          } catch {
            // ignore
          }
        }

        function waitExit(child: any, ms: number): Promise<boolean> {
          return new Promise((resolve) => {
            if (child.exitCode !== null || child.signalCode !== null) { resolve(true); return; }
            let settled = false;
            const onExit = () => { if (!settled) { settled = true; resolve(true); } };
            child.once("exit", onExit);
            setTimeout(() => {
              if (!settled) { settled = true; try { child.removeListener("exit", onExit); } catch { /* ignore */ } resolve(false); }
            }, ms);
          });
        }

        // EOF -> SIGTERM -> SIGKILL ladder: a child may ignore EOF and SIGTERM, so escalate.
        async function killLadder(child: any) {
          try { if (child.stdin) child.stdin.end(); } catch { /* ignore */ }
          if (await waitExit(child, KILL_GRACE_MS)) return;
          try { child.kill("SIGTERM"); } catch { /* ignore */ }
          if (await waitExit(child, KILL_GRACE_MS)) return;
          try { child.kill("SIGKILL"); } catch { /* ignore */ }
        }

        // A minimal line-delimited JSON-RPC 2.0 client over one `kcap mcp <name>` subprocess.
        class McpStdioClient {
          label: string;
          command: string;
          args: string[];
          env: Record<string, string>;
          child: any = null;
          nextId = 1;
          pending = new Map<number, { resolve: (v: any) => void; reject: (e: any) => void }>();
          buffer = "";
          closed = false;
          protocolVersion: string | null = null;

          constructor(label: string, command: string, args: string[], env: Record<string, string>) {
            this.label = label;
            this.command = command;
            this.args = args;
            this.env = env;
          }

          // spawn + MCP handshake (initialize -> validate -> notifications/initialized).
          async start(): Promise<void> {
            const child = spawn(this.command, this.args, {
              stdio: ["pipe", "pipe", "pipe"],
              env: { ...process.env, ...this.env },
            });
            this.child = child;
            trackChild(child);
            ensureExitHook();
            child.on("exit", () => this.fail(new Error("kcap mcp " + this.label + " exited")));
            child.on("error", (e: any) => this.fail(e instanceof Error ? e : new Error(String(e))));
            child.stdout.setEncoding("utf8");
            child.stdout.on("data", (chunk: string) => this.onData(chunk));
            // Forward the server's stderr (its primary diagnostics channel) with a prefix so
            // failures are debuggable; kept off stdout so it never corrupts the JSON-RPC stream.
            if (child.stderr) {
              child.stderr.setEncoding("utf8");
              child.stderr.on("data", (chunk: string) => {
                for (const line of String(chunk).split("\n")) {
                  if (line.trim()) console.error("[kcap-mcp " + this.label + "] " + line);
                }
              });
            }
            // A just-closed/dead stdin can surface EPIPE (or another stream error) asynchronously;
            // route it through fail() — rejecting pending calls — instead of letting it crash pi.
            if (child.stdin) child.stdin.on("error", (e: any) => this.fail(e instanceof Error ? e : new Error(String(e))));

            // Full MCP initialize; validate + capture the negotiated protocol version, THEN send
            // notifications/initialized before any tools/list or tools/call (a spec-strict server
            // rejects requests that arrive before it).
            const result = await this.request("initialize", {
              protocolVersion: "2024-11-05",
              capabilities: {},
              clientInfo: { name: "kcap-pi-bridge", version: "1" },
            }, HANDSHAKE_TIMEOUT_MS);
            this.protocolVersion = (result && result.protocolVersion) || "2024-11-05";
            this.notify("notifications/initialized", {});
          }

          onData(chunk: string) {
            this.buffer += chunk;
            let nl: number;
            while ((nl = this.buffer.indexOf("\n")) >= 0) {
              const line = this.buffer.slice(0, nl).trim();
              this.buffer = this.buffer.slice(nl + 1);
              if (!line) continue;
              let msg: any;
              try {
                msg = JSON.parse(line);
              } catch {
                continue; // ignore non-JSON lines (defensive)
              }
              const id = msg && msg.id;
              if (typeof id === "number" && this.pending.has(id)) {
                const p = this.pending.get(id)!;
                this.pending.delete(id);
                if (msg.error) p.reject(new Error((msg.error && msg.error.message) || "MCP error"));
                else p.resolve(msg.result);
              }
            }
          }

          // Reject every in-flight call when the subprocess dies so execute() never hangs;
          // mark closed so later calls fail fast too.
          fail(err: Error) {
            if (this.closed) return;
            this.closed = true;
            for (const p of this.pending.values()) p.reject(err);
            this.pending.clear();
          }

          send(obj: any) {
            if (this.closed || !this.child || !this.child.stdin || !this.child.stdin.writable) {
              throw new Error("kcap mcp " + this.label + " not available");
            }
            this.child.stdin.write(JSON.stringify(obj) + "\n");
          }

          notify(method: string, params: any) {
            try {
              this.send({ jsonrpc: "2.0", method, params });
            } catch {
              // notifications are best-effort
            }
          }

          request(method: string, params: any, timeoutMs?: number): Promise<any> {
            if (this.closed) return Promise.reject(new Error("kcap mcp " + this.label + " not available"));
            const id = this.nextId++;
            return new Promise((resolve, reject) => {
              let timer: any = null;
              const clear = () => {
                if (timer) clearTimeout(timer);
              };
              this.pending.set(id, {
                resolve: (v) => { clear(); resolve(v); },
                reject: (e) => { clear(); reject(e); },
              });
              if (timeoutMs && timeoutMs > 0) {
                timer = setTimeout(() => {
                  if (this.pending.delete(id)) reject(new Error("kcap mcp " + this.label + " " + method + " timed out"));
                }, timeoutMs);
              }
              try {
                this.send({ jsonrpc: "2.0", id, method, params });
              } catch (e) {
                this.pending.delete(id);
                clear();
                reject(e);
              }
            });
          }

          async listTools(): Promise<any[]> {
            const res = await this.request("tools/list", {}, HANDSHAKE_TIMEOUT_MS);
            return res && Array.isArray(res.tools) ? res.tools : [];
          }

          callTool(name: string, args: any): Promise<any> {
            return this.request("tools/call", { name, arguments: args || {} }, TOOL_CALL_TIMEOUT_MS);
          }

          // Graceful teardown via the EOF -> SIGTERM -> SIGKILL ladder.
          async stop(): Promise<void> {
            this.fail(new Error("kcap mcp " + this.label + " stopped"));
            const child = this.child;
            this.child = null;
            if (!child) return;
            await killLadder(child);
          }
        }
        """;
}
