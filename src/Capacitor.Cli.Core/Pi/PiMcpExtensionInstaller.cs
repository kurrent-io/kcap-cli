namespace Capacitor.Cli.Core.Pi;

/// <summary>
/// Installs / removes kcap's MCP-bridge extension for Pi. Pi has no built-in MCP, so
/// instead of a JSON <c>mcpServers</c> config kcap ships a TypeScript extension
/// (<c>~/.pi/agent/extensions/kcap-mcp.ts</c>) that spawns the <c>kcap mcp &lt;name&gt;</c>
/// servers and registers their tools as native Pi tools. Sibling of
/// <see cref="PiExtensionInstaller"/> with a distinct file + marker; <see cref="ExtensionContent"/>
/// is the embedded source of truth (const, not a resource — NativeAOT-safe).
/// </summary>
public static class PiMcpExtensionInstaller {
    public const string MarkerFileName = ".kcap-mcp-extension-version";

    /// <summary>
    /// The kcap Pi MCP-bridge extension. Untyped (<c>pi: any</c>) so it carries no
    /// runtime dependency on the <c>@earendil-works/pi-coding-agent</c> types, and
    /// fail-safe so a kcap/server hiccup never disrupts the pi session: each server
    /// is spawned/handshaken independently and failures are logged and skipped.
    /// </summary>
    public const string ExtensionContent =
        """
        // kcap-mcp.ts — Kurrent Capacitor MCP-bridge extension for Pi.
        // Pi has no built-in MCP, so this bridges the kcap stdio servers (`kcap mcp <name>`)
        // into Pi as native tools: spawn each, handshake, register its tools. Dependency-free
        // (node:child_process only) and fail-safe — one bad server never blocks the rest.

        import { spawn } from "node:child_process";

        // The kcap MCP servers to bridge, in `kcap mcp <name>` form. `flows` starts a
        // *paid* hosted reviewer only on explicit invocation; its tool descriptions
        // (surfaced verbatim from the server) name that cost so the model/user see it.
        const KCAP_MCP_SERVERS = ["review", "sessions", "flows", "memory"];

        // Bound the initialize + tools/list handshake per server so a hung server can
        // never stall pi's startup indefinitely.
        const HANDSHAKE_TIMEOUT_MS = 10000;

        // Bound each tools/call so a stalled (but not exited) server can't hang a pi tool
        // forever. Generous — well above the flows server's own long-poll/round timeouts,
        // since it's only a backstop; on timeout the error surfaces as tool-result content.
        const TOOL_CALL_TIMEOUT_MS = 1200000; // 20 min

        // A minimal line-delimited JSON-RPC 2.0 client over one `kcap mcp <name>`
        // subprocess. No external MCP SDK.
        class McpStdioClient {
          server: string;
          child: any = null;
          nextId = 1;
          pending = new Map<number, { resolve: (v: any) => void; reject: (e: any) => void }>();
          buffer = "";
          closed = false;

          constructor(server: string) {
            this.server = server;
          }

          // spawn + MCP handshake (initialize -> notifications/initialized).
          async start(): Promise<void> {
            const child = spawn("kcap", ["mcp", this.server], { stdio: ["pipe", "pipe", "pipe"] });
            this.child = child;
            child.on("exit", () => this.fail(new Error("kcap mcp " + this.server + " exited")));
            child.on("error", (e: any) => this.fail(e instanceof Error ? e : new Error(String(e))));
            child.stdout.setEncoding("utf8");
            child.stdout.on("data", (chunk: string) => this.onData(chunk));
            // Forward the server's stderr (its primary diagnostics channel) with a prefix so
            // failures are debuggable; kept off stdout so it never corrupts the JSON-RPC stream.
            if (child.stderr) {
              child.stderr.setEncoding("utf8");
              child.stderr.on("data", (chunk: string) => {
                for (const line of String(chunk).split("\n")) {
                  if (line.trim()) console.error("[kcap-mcp " + this.server + "] " + line);
                }
              });
            }

            await this.request("initialize", {
              protocolVersion: "2024-11-05",
              capabilities: {},
              clientInfo: { name: "kcap-pi-bridge", version: "1" },
            });
            // Per the MCP spec the client must send `notifications/initialized`
            // after the initialize response and before any further requests.
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

          // Reject every in-flight call when the subprocess dies so execute() never
          // hangs; mark closed so later calls fail fast too.
          fail(err: Error) {
            if (this.closed) return;
            this.closed = true;
            for (const p of this.pending.values()) p.reject(err);
            this.pending.clear();
          }

          send(obj: any) {
            if (this.closed || !this.child || !this.child.stdin || !this.child.stdin.writable) {
              throw new Error("kcap mcp " + this.server + " not available");
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
            if (this.closed) return Promise.reject(new Error("kcap mcp " + this.server + " not available"));
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
                  if (this.pending.delete(id)) reject(new Error("kcap mcp " + this.server + " " + method + " timed out"));
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

          stop() {
            this.fail(new Error("kcap mcp " + this.server + " stopped"));
            const child = this.child;
            this.child = null;
            if (!child) return;
            // Close stdin (EOF) to let a well-behaved server exit, then kill it.
            try {
              if (child.stdin) child.stdin.end();
            } catch {
              // ignore
            }
            try {
              child.kill();
            } catch {
              // ignore
            }
          }
        }

        function sanitizeToolName(name: string): string {
          return String(name)
            .toLowerCase()
            .replace(/[^a-z0-9_]+/g, "_")
            .replace(/^_+|_+$/g, "");
        }

        function withTimeout<T>(p: Promise<T>, ms: number, what: string): Promise<T> {
          return new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error(what + " timed out")), ms);
            p.then(
              (v) => { clearTimeout(timer); resolve(v); },
              (e) => { clearTimeout(timer); reject(e); },
            );
          });
        }

        // Async factory: pi awaits it before session_start, so the bridged tools are
        // registered before the first turn. Pi reloads/rebinds extensions per session,
        // so each session gets its own subprocesses, torn down on session_shutdown.
        export default async function (pi: any) {
          const clients: McpStdioClient[] = [];

          await Promise.all(
            KCAP_MCP_SERVERS.map(async (server) => {
              const client = new McpStdioClient(server);
              let tools: any[];
              try {
                // One 10s budget for the whole handshake (spawn + initialize + tools/list).
                tools = await withTimeout(
                  (async () => {
                    await client.start();
                    return client.listTools();
                  })(),
                  HANDSHAKE_TIMEOUT_MS,
                  "kcap mcp " + server + " handshake",
                );
              } catch (e: any) {
                // Log why this server was skipped so its tools' absence is explainable.
                console.error("[kcap-mcp] " + server + " unavailable, skipping: " + ((e && e.message) || String(e)));
                client.stop(); // skip this server; leave the others registered
                return;
              }

              clients.push(client);

              for (const tool of tools) {
                const mcpName = tool && tool.name;
                if (!mcpName) continue;
                const toolName = sanitizeToolName("kcap_" + server + "_" + mcpName);
                if (!toolName) continue;
                const description = String((tool && tool.description) || ("kcap " + server + " " + mcpName));
                pi.registerTool({
                  name: toolName,
                  label: "kcap " + server + ": " + mcpName,
                  description,
                  // Surface it in the system prompt's Available tools section too.
                  promptSnippet: "kcap " + server + " — " + description.split("\n")[0],
                  // MCP inputSchema is already JSON Schema; pi forwards `parameters`
                  // to the provider as-is.
                  parameters: (tool && tool.inputSchema) || { type: "object", properties: {} },
                  async execute(_toolCallId: string, params: any) {
                    try {
                      const result = await client.callTool(mcpName, params);
                      const content =
                        result && Array.isArray(result.content) && result.content.length
                          ? result.content
                          : [{ type: "text", text: typeof result === "string" ? result : JSON.stringify(result || {}) }];
                      // AgentToolResult has no isError field — an MCP error surfaces as
                      // content text so the model can see it.
                      return { content, details: { server, tool: mcpName, isError: !!(result && result.isError) } };
                    } catch (e: any) {
                      return {
                        content: [{ type: "text", text: "kcap " + server + " " + mcpName + " failed: " + ((e && e.message) || String(e)) }],
                        details: { server, tool: mcpName, isError: true },
                      };
                    }
                  },
                });
              }
            }),
          );

          let done = false;
          const onExit = () => {
            for (const c of clients) c.stop();
          };
          const shutdown = () => {
            if (done) return;
            done = true;
            onExit();
            // Pi reloads/rebinds this extension per session switch and re-runs the
            // factory, so drop our process-exit listener to avoid accumulating one
            // per session (Node's MaxListeners warning).
            try {
              process.removeListener("exit", onExit);
            } catch {
              // ignore
            }
          };
          // session_shutdown fires on session switch AND on process exit
          // (Ctrl+C/Ctrl+D/SIGHUP/SIGTERM) — the primary teardown hook.
          pi.on("session_shutdown", async () => shutdown());
          // Sync backstop for an abrupt exit where the async handler may not finish.
          try {
            process.once("exit", onExit);
          } catch {
            // ignore
          }
        }
        """;

    /// <summary>
    /// True when kcap-mcp.ts (or its marker) is present. Marker covers the case
    /// where a user deleted kcap-mcp.ts but kept the dir.
    /// </summary>
    public static bool IsInstalled(string extensionPath) {
        if (File.Exists(extensionPath)) return true;
        var dir = Path.GetDirectoryName(extensionPath);
        return dir is not null && File.Exists(Path.Combine(dir, MarkerFileName));
    }

    public static string? ReadMarker(string extensionPath) {
        var dir = Path.GetDirectoryName(extensionPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    public static void WriteMarker(string extensionPath) {
        var dir = Path.GetDirectoryName(extensionPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    public static void DeleteMarker(string extensionPath) {
        var dir = Path.GetDirectoryName(extensionPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
    }

    public static bool Install(string extensionPath) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(extensionPath)!);
            File.WriteAllText(extensionPath, ExtensionContent);
            WriteMarker(extensionPath);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>Removes kcap-mcp.ts + marker. Returns true if kcap-mcp.ts existed.</summary>
    public static bool Remove(string extensionPath) {
        var existed = File.Exists(extensionPath);
        try {
            if (existed) File.Delete(extensionPath);
            DeleteMarker(extensionPath);
        } catch {
            return false;
        }
        return existed;
    }
}
