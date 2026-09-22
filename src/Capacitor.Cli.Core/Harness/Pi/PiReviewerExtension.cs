namespace Capacitor.Cli.Core.Harness.Pi;

/// <summary>
/// The extension an unattended Pi reviewer is launched with, and the only one it loads. It bridges the
/// MCP servers named in a per-launch manifest and supplies read-only file tools confined to the
/// worktree. Written per launch and never installed into the operator's agent directory.
/// </summary>
public static class PiReviewerExtension {
    public const string FileName       = "kcap-reviewer.ts";
    public const string ManifestEnvVar = "KCAP_PI_REVIEWER_MANIFEST";
    public const string ReadyFileName  = "ready.json";

    public const string Content = Header + PiMcpStdioClientSource.Text + Body;

    const string Header =
        """
        // kcap-reviewer.ts — the sole extension of an unattended Pi reviewer.
        // Everything that can fail happens inside the async factory and throws: Pi awaits the factory
        // before it serves a single command and exits if it throws, so a reviewer never starts with
        // part of its tool surface.

        import { spawn } from "node:child_process";
        import { closeSync, openSync, readFileSync, readdirSync, readSync, realpathSync, renameSync, statSync, writeFileSync } from "node:fs";
        import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
        import { StringDecoder } from "node:string_decoder";

        const HANDSHAKE_TIMEOUT_MS = 10000;
        const TOOL_CALL_TIMEOUT_MS = 1200000;
        const KILL_GRACE_MS = 2000;

        const MAX_READ_LINES = 2000;
        const MAX_READ_BYTES = 262144;
        const READ_CHUNK_BYTES = 65536;
        const MAX_DIR_ENTRIES = 1000;
        const MAX_MATCHES = 200;
        const MAX_SEARCH_FILE_BYTES = 1048576;
        const SEARCH_BUDGET_MS = 10000;
        const MAX_GLOB_LENGTH = 1024;
        const MAX_GLOB_DOUBLE_STARS = 8;

        """;

    const string Body =
        """

        function fail(what: string): never {
          throw new Error("kcap-reviewer: " + what);
        }

        function withTimeout<T>(p: Promise<T>, ms: number, what: string): Promise<T> {
          return new Promise<T>((res, rej) => {
            const timer = setTimeout(() => rej(new Error("kcap-reviewer: " + what + " timed out")), ms);
            p.then((v) => { clearTimeout(timer); res(v); }, (e) => { clearTimeout(timer); rej(e); });
          });
        }

        function textResult(text: string) {
          return { content: [{ type: "text", text }] };
        }

        function isBinary(buf: Buffer): boolean {
          return buf.subarray(0, Math.min(buf.length, 8192)).includes(0);
        }

        // The glob comes from the model, so a pattern crafted with many "**" segments would backtrack
        // combinatorially against a deep path and wedge the synchronous event loop. Refused rather
        // than matched.
        function rejectComplexGlob(glob: string): void {
          if (glob.length > MAX_GLOB_LENGTH) throw new Error("glob pattern is too complex");
          let stars = 0;
          for (const seg of glob.split("/")) if (seg === "**" && ++stars > MAX_GLOB_DOUBLE_STARS)
            throw new Error("glob pattern is too complex");
        }

        // "*" within a path segment, "**" across segments, everything else literal. No regular
        // expression: nothing here may compile a pattern from text a model supplied. Memoised by
        // (glob-seg, path-seg) so a crafted "**" run cannot backtrack, and the search deadline is
        // carried in so a match aborts with the budget rather than after it.
        function globMatches(glob: string, relPath: string, deadline: number): boolean {
          const g = glob.split("/"), p = relPath.split("/");
          const width = p.length + 1;
          const memo = new Map<number, boolean>();

          function segment(pattern: string, name: string): boolean {
            const parts = pattern.split("*");
            if (parts.length === 1) return pattern === name;
            if (!name.startsWith(parts[0])) return false;
            let pos = parts[0].length;
            for (let i = 1; i < parts.length - 1; i++) {
              const at = name.indexOf(parts[i], pos);
              if (at < 0) return false;
              pos = at + parts[i].length;
            }
            const last = parts[parts.length - 1];
            return name.length - pos >= last.length && name.endsWith(last);
          }

          function match(gi: number, ni: number): boolean {
            const key = gi * width + ni;
            const seen = memo.get(key);
            if (seen !== undefined) return seen;
            // Bounded by the memo to (g.length+1)*(p.length+1) fresh states, so this runs a bounded
            // number of times even for a pattern built to backtrack.
            if (Date.now() > deadline) return false;

            let result: boolean;
            if (gi === g.length) result = ni === p.length;
            else if (g[gi] === "**") {
              result = false;
              for (let k = ni; k <= p.length; k++) if (match(gi + 1, k)) { result = true; break; }
            } else {
              result = ni < p.length && segment(g[gi], p[ni]) && match(gi + 1, ni + 1);
            }

            memo.set(key, result);
            return result;
          }

          return match(0, 0);
        }

        export default async function (pi: any) {
          const manifestPath = process.env.KCAP_PI_REVIEWER_MANIFEST;
          if (!manifestPath) fail("KCAP_PI_REVIEWER_MANIFEST is not set");

          let manifest: any;
          try {
            manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
          } catch (e: any) {
            fail("manifest unreadable: " + ((e && e.message) || String(e)));
          }

          const rootReal = realpathSync(manifest.root);

          // The read boundary. Compared by path component on fully resolved paths: a string prefix
          // would admit a sibling such as <root>-evil, and an unresolved path would admit a symlink.
          function contained(userPath: string): string {
            const target = realpathSync(resolve(rootReal, String(userPath ?? ".")));
            const rel = relative(rootReal, target);
            const inside = rel === "" || (rel !== ".." && !rel.startsWith(".." + sep) && !isAbsolute(rel));
            if (!inside) throw new Error("path is outside the repository under review");
            return target;
          }

          const fileTools: Record<string, any> = {
            read_file: {
              description: "Read a UTF-8 text file in the repository under review. Returns at most 2000 lines or 256 KB per call; use offset and limit to page.",
              parameters: {
                type: "object",
                properties: {
                  path: { type: "string" },
                  offset: { type: "integer", minimum: 0, description: "First line to return, zero-based." },
                  limit: { type: "integer", minimum: 1, maximum: MAX_READ_LINES },
                },
                required: ["path"],
              },
              run(params: any) {
                const target = contained(params.path);
                if (!statSync(target).isFile()) throw new Error("not a file");

                const offset = Math.max(0, Number(params.offset ?? 0) | 0);
                const limit = Math.min(MAX_READ_LINES, Math.max(1, Number(params.limit ?? MAX_READ_LINES) | 0));

                // Streamed with a fixed buffer, never the whole file: a hostile large file must not be
                // materialized to enforce the 2000-line / 256 KB bound, and the returned size is
                // measured in UTF-8 bytes (Buffer.byteLength), not UTF-16 string length.
                const fd = openSync(target, "r");
                try {
                  const chunk = Buffer.allocUnsafe(READ_CHUNK_BYTES);
                  const decoder = new StringDecoder("utf8");
                  let carry = "", out = "", line = 0, taken = 0, bytes = 0, more = false, stop = false, first = true;

                  function take(text: string, terminated: boolean): boolean {
                    if (line < offset) { line++; return true; }
                    if (taken >= limit) { more = true; return false; }
                    const add = Buffer.byteLength(text, "utf8") + (terminated ? 1 : 0);
                    if (bytes + add > MAX_READ_BYTES) { more = true; return false; }
                    out += terminated ? text + "\n" : text;
                    bytes += add; taken++; line++;
                    return true;
                  }

                  while (!stop) {
                    const n = readSync(fd, chunk, 0, chunk.length, null);
                    if (n === 0) break;
                    if (first) {
                      first = false;
                      if (chunk.subarray(0, Math.min(n, 8192)).includes(0)) throw new Error("binary file");
                    }
                    carry += decoder.write(chunk.subarray(0, n));

                    let nl: number;
                    while ((nl = carry.indexOf("\n")) >= 0) {
                      const one = carry.slice(0, nl);
                      carry = carry.slice(nl + 1);
                      if (!take(one, true)) { stop = true; break; }
                    }

                    // A single line longer than the returnable budget would otherwise grow `carry` to
                    // the whole file on a newline-free input.
                    if (!stop && carry.length > MAX_READ_BYTES) { more = true; stop = true; }
                  }

                  if (!stop) {
                    carry += decoder.end();
                    if (carry.length > 0) take(carry, false);
                  }

                  const hint = more ? "\n[more content; call again with offset " + (offset + taken) + "]" : "";
                  return textResult(out + hint);
                } finally {
                  closeSync(fd);
                }
              },
            },

            list_directory: {
              description: "List a directory in the repository under review. Symbolic links are listed, not followed.",
              parameters: { type: "object", properties: { path: { type: "string" } }, required: ["path"] },
              run(params: any) {
                const target = contained(params.path);
                const entries = readdirSync(target, { withFileTypes: true }).slice(0, MAX_DIR_ENTRIES);
                return textResult(entries.map((e: any) =>
                  (e.isSymbolicLink() ? "link  " : e.isDirectory() ? "dir   " : "file  ") + e.name).join("\n"));
              },
            },

            search_files: {
              description: "Search text files in the repository under review for a literal string. Not a regular expression. Returns at most 200 matches as path:line: text.",
              parameters: {
                type: "object",
                properties: {
                  text: { type: "string", description: "Literal text to find." },
                  path: { type: "string", description: "Directory to search; defaults to the repository root." },
                  glob: { type: "string", description: "Optional filter on the repository-relative path, e.g. **/*.cs" },
                  ignore_case: { type: "boolean" },
                },
                required: ["text"],
              },
              run(params: any) {
                const needleRaw = String(params.text ?? "");
                if (!needleRaw) throw new Error("text is required");
                const ignoreCase = params.ignore_case === true;
                const needle = ignoreCase ? needleRaw.toLowerCase() : needleRaw;
                const glob = params.glob ? String(params.glob) : null;
                if (glob !== null) rejectComplexGlob(glob);   // refuse once, before the walk
                const started = Date.now();
                const deadline = started + SEARCH_BUDGET_MS;
                const matches: string[] = [];
                let truncated = "";
                const stack = [contained(params.path ?? ".")];

                walk: while (stack.length) {
                  const dir = stack.pop()!;
                  for (const e of readdirSync(dir, { withFileTypes: true })) {
                    // A link is neither descended into nor searched: its target may lie outside the root.
                    if (e.isSymbolicLink()) continue;
                    const full = join(dir, e.name);
                    if (e.isDirectory()) { if (e.name !== ".git") stack.push(full); continue; }
                    if (!e.isFile()) continue;
                    if (Date.now() > deadline) { truncated = "\n[stopped: time budget reached]"; break walk; }
                    const rel = relative(rootReal, full).split(sep).join("/");
                    if (glob !== null && !globMatches(glob, rel, deadline)) continue;
                    if (statSync(full).size > MAX_SEARCH_FILE_BYTES) continue;
                    const buf = readFileSync(full);
                    if (isBinary(buf)) continue;
                    const lines = buf.toString("utf8").split("\n");
                    for (let n = 0; n < lines.length; n++) {
                      const hay = ignoreCase ? lines[n].toLowerCase() : lines[n];
                      if (!hay.includes(needle)) continue;
                      matches.push(rel + ":" + (n + 1) + ": " + lines[n].slice(0, 300));
                      if (matches.length >= MAX_MATCHES) { truncated = "\n[stopped: 200 matches]"; break walk; }
                    }
                  }
                }

                return textResult((matches.length ? matches.join("\n") : "no matches") + truncated);
              },
            },
          };

          for (const name of manifest.fileTools || []) {
            const tool = fileTools[name];
            if (!tool) fail("manifest names an unknown file tool: " + name);
            pi.registerTool({
              name,
              label: name,
              description: tool.description,
              parameters: tool.parameters,
              async execute(_toolCallId: string, params: any) { return tool.run(params || {}); },
            });
          }

          for (const server of manifest.servers || []) {
            const client = new McpStdioClient(server.id, server.command, server.args || [], server.env || {});
            await withTimeout(client.start(), HANDSHAKE_TIMEOUT_MS, server.id + " handshake");
            const served = await withTimeout(client.listTools(), HANDSHAKE_TIMEOUT_MS, server.id + " tools/list");
            const byName = new Map<string, any>(served.map((t: any) => [t.name, t]));

            for (const entry of server.tools || []) {
              const mcpTool = byName.get(entry.mcp);
              if (!mcpTool) fail(server.id + " does not serve " + entry.mcp);
              pi.registerTool({
                name: entry.pi,
                label: entry.pi,
                description: String(mcpTool.description || entry.mcp),
                parameters: mcpTool.inputSchema || { type: "object", properties: {} },
                async execute(_toolCallId: string, params: any) {
                  if (client.closed) throw new Error(entry.pi + " is unavailable: its server exited");
                  const result = await withTimeout(client.callTool(entry.mcp, params), TOOL_CALL_TIMEOUT_MS, entry.pi);
                  // A thrown execute is how an error reaches the model; a tool result has no error flag.
                  if (result && result.isError) {
                    const parts = Array.isArray(result.content) ? result.content : [];
                    throw new Error(parts.map((p: any) => p.text || "").join("\n") || entry.pi + " returned an error");
                  }
                  return result && Array.isArray(result.content) && result.content.length
                    ? { content: result.content }
                    : textResult(JSON.stringify(result || {}));
                },
              });
            }
          }

          // Pi drops an allowlisted name that matches nothing without saying so. The host compares this
          // report with the list it launched with before it sends the first prompt.
          pi.on("session_start", async () => {
            const readyPath = join(dirname(manifestPath), "ready.json");
            const tmp = readyPath + ".tmp";
            writeFileSync(tmp, JSON.stringify({ active: [...pi.getActiveTools()].sort() }));
            renameSync(tmp, readyPath);
          });
        }
        """;
}
