namespace Capacitor.Cli.Core.OpenCode;

/// <summary>
/// Installs / removes kcap's live-ingest plugin for SST OpenCode (AI-919).
/// OpenCode has no shell hooks, so — like Pi — kcap ships a TypeScript plugin
/// file (<c>~/.config/opencode/plugins/kcap.ts</c>) that OpenCode auto-discovers
/// and loads in-process. The plugin bridges OpenCode's event bus to the kcap CLI:
/// on <c>session.created</c> it shells <c>kcap hook --opencode --event session-start</c>
/// (which POSTs lifecycle + spawns the transcript watcher), and on
/// <c>session.idle</c> it fetches the session's full messages via the injected SDK
/// <c>client</c> and appends them as native <c>{info,parts}</c> JSONL lines to the
/// file the watcher tails.
///
/// <para><see cref="ExtensionContent"/> is the single source of truth for the
/// installed file (embedding it as a const keeps NativeAOT happy — no manifest
/// resource reflection). A version marker beside it gates the upgrade-time
/// refresh, mirroring <see cref="Pi.PiExtensionInstaller"/>.</para>
/// </summary>
public static class OpenCodeExtensionInstaller {
    public const string MarkerFileName = ".kcap-extension-version";

    /// <summary>
    /// The kcap OpenCode plugin. Dependency-free (only <c>node:</c> builtins,
    /// available in Bun) so it works in airgapped installs with no npm registry,
    /// and untyped (<c>any</c>) so it carries no runtime dependency on
    /// <c>@opencode-ai/sdk</c> / <c>@opencode-ai/plugin</c> types. Fail-safe — every
    /// handler swallows errors so a kcap/server hiccup never disrupts the session.
    /// </summary>
    public const string ExtensionContent =
        """
        // kcap.ts — Kurrent Capacitor live-ingest plugin for SST OpenCode (AI-919).
        //
        // Installed by `kcap setup` / `kcap plugin install --opencode` into
        // ~/.config/opencode/plugins/kcap.ts, which OpenCode auto-loads in-process.
        // OpenCode has no shell hooks, so this plugin bridges OpenCode's event bus to
        // the kcap CLI:
        //   - session.created → `kcap hook --opencode --event session-start ...`, which
        //     POSTs /hooks/session-start/opencode and spawns the transcript watcher
        //     (vendor=opencode) tailing the JSONL file this plugin writes. The watcher
        //     synthesizes session-end when the opencode process exits (Kiro pattern).
        //   - session.idle → fetch the session's full messages via the injected SDK
        //     client and append any new {info,parts} as JSONL lines to that file; the
        //     watcher streams them. The server's OpenCodeTranscriptNormalizer dedupes by
        //     deterministic part id, so re-appending an updated message is safe.
        //
        // Dependency-free (only node: builtins) and fail-safe by default.

        import { appendFileSync, mkdirSync } from "node:fs"
        import { homedir } from "node:os"
        import { join, dirname } from "node:path"

        export const KcapPlugin = async ({ client, $, directory }: any) => {
          const dir = join(homedir(), ".cache", "kcap", "opencode")
          const file = (sid: string) => join(dir, sid + ".jsonl")
          // Subagents run as child sessions; their {info,parts} go in a nested dir beside
          // the parent file (<dir>/<parent>/<child>.jsonl) that the kcap watcher scans and
          // streams with the child's agent_id → AgentSubsession-* (AI-919 phase 2).
          const childFile = (parent: string, child: string) => join(dir, parent, child + ".jsonl")
          const started = new Set<string>()
          const children = new Set<string>()
          const written = new Map<string, Set<string>>()
          const unknownCounts = new Map<string, number>() // consecutive failed classifications per sid

          async function runKcap(args: string[]) {
            try {
              // kcap spawns a detached watcher and returns fast; bound it so a hung
              // kcap can never stall OpenCode.
              await Promise.race([
                $`kcap ${args}`.quiet().nothrow(),
                new Promise((res) => setTimeout(res, 10000)),
              ])
            } catch {
              // never disrupt the OpenCode session
            }
          }

          // Classify a session: "child" (a subagent — never ingest as top-level), "top"
          // (no parent), or "unknown" (a transient session.get failure left it ambiguous).
          // NEVER start a top-level session on "unknown" — that is exactly the case that would
          // misfile a child as BOTH a top-level session AND a subagent. Only a SUCCESSFUL
          // session.get with no parent proves top-level; otherwise defer to the next idle
          // (session.created carries parentID only for some children, and session.idle never
          // does, so the SDK is the authority).
          async function classify(sid: string, info?: any): Promise<"child" | "top" | "unknown"> {
            if (children.has(sid)) return "child"
            if (info?.parentID) { children.add(sid); return "child" }
            try {
              const s: any = await client.session.get({ path: { id: sid } })
              unknownCounts.delete(sid)
              const parentId = s?.parentID ?? s?.data?.parentID
              if (parentId) { children.add(sid); return "child" }
              return "top"
            } catch {
              // session.get failed — ambiguous, so DEFER (don't misfile a child as top-level).
              // But after a few consecutive failures fall back to top-level, so a persistent
              // endpoint-specific session.get failure doesn't permanently drop a real top-level
              // session (the rare wrong-guess duplicate is better than never capturing it).
              const n = (unknownCounts.get(sid) ?? 0) + 1
              unknownCounts.set(sid, n)
              return n >= 3 ? "top" : "unknown"
            }
          }

          // Per-message dedup key. parts.length catches new parts streaming in; the
          // terminal-tool count catches a tool part transitioning to completed/error WITHOUT
          // the part count changing — the server skips non-terminal tool snapshots and keeps
          // the FIRST append per prt_ id, so the completed snapshot MUST be re-appended under a
          // new key to be ingested. (Text/reasoning are final by session.idle = turn end.)
          function dedupeKey(m: any) {
            const parts: any[] = m?.parts ?? []
            let terminalTools = 0
            for (const p of parts) {
              const st = p?.state?.status
              if (p?.type === "tool" && (st === "completed" || st === "error")) terminalTools++
            }
            return (m?.info?.id ?? "") + ":" + parts.length + ":" + terminalTools
          }

          async function start(sid: string, info?: any) {
            if (started.has(sid)) return
            started.add(sid)
            try { mkdirSync(dir, { recursive: true }); appendFileSync(file(sid), "") } catch {}
            const args = ["hook", "--opencode", "--event", "session-start", "--session", sid, "--file", file(sid)]
            const cwd = info?.directory ?? directory
            if (cwd) args.push("--cwd", String(cwd))
            if (info?.version) args.push("--version", String(info.version))
            await runKcap(args)
          }

          // Fetch a session's full messages and append any not-yet-written {info,parts}
          // lines to targetFile. Re-emits when a message's part count grows (the assistant
          // streams parts in over a turn); deterministic prt_ ids dedupe server-side.
          async function flushTo(sid: string, targetFile: string) {
            try {
              const res: any = await client.session.messages({ path: { id: sid } })
              const msgs: any[] = Array.isArray(res) ? res : (res?.data ?? [])
              const seen = written.get(targetFile) ?? new Set<string>()
              written.set(targetFile, seen)
              const lines: string[] = []
              for (const m of msgs) {
                const id = m?.info?.id
                if (!id) continue
                const key = dedupeKey(m)
                if (seen.has(key)) continue
                seen.add(key)
                lines.push(JSON.stringify({ info: m.info, parts: m.parts ?? [] }))
              }
              if (lines.length > 0) {
                try { mkdirSync(dirname(targetFile), { recursive: true }) } catch {}
                appendFileSync(targetFile, lines.join("\n") + "\n")
              }
            } catch {
              // never disrupt the OpenCode session
            }
          }

          // Child ids whose spawning `task` tool part on the PARENT is terminal
          // (completed/error) — a DURABLE "subagent finished" signal, rather than assuming the
          // parent only idles once the task returned. The task part records the child id in
          // state.metadata.sessionId. We flush a child ONLY once its task is terminal, so a
          // still-streaming child's partial text/reasoning is never appended first (the server
          // keeps-first by prt_ id, so a partial text part would freeze — the dedup discriminator
          // only re-sends tool parts, not growing text).
          async function completedChildIds(parent: string) {
            const done = new Set<string>()
            try {
              const res: any = await client.session.messages({ path: { id: parent } })
              const msgs: any[] = Array.isArray(res) ? res : (res?.data ?? [])
              for (const m of msgs) {
                for (const p of (m?.parts ?? [])) {
                  if (p?.type !== "tool" || p?.tool !== "task") continue
                  const st = p?.state?.status
                  const cid = p?.state?.metadata?.sessionId
                  if (cid && (st === "completed" || st === "error")) done.add(cid)
                }
              }
            } catch {}
            return done
          }

          // On the parent's idle, stream COMPLETED child sessions (subagents) into the nested dir
          // the watcher scans. Every child id is recorded so its own session events skip the
          // top-level path; a child whose task isn't terminal yet is simply deferred to a later
          // parent idle (no partial transcript is written).
          async function flushSubagents(parent: string) {
            try {
              const done = await completedChildIds(parent)
              const res: any = await client.session.children({ path: { id: parent } })
              const kids: any[] = Array.isArray(res) ? res : (res?.data ?? [])
              for (const k of kids) {
                const cid = k?.id
                if (!cid) continue
                children.add(cid)
                if (!done.has(cid)) continue // task not terminal yet — defer to a later idle
                await flushTo(cid, childFile(parent, cid))
              }
            } catch {
              // never disrupt the OpenCode session
            }
          }

          return {
            event: async ({ event }: any) => {
              try {
                const type = event?.type
                const sid = event?.properties?.sessionID
                if (!sid) return
                if (children.has(sid)) return  // known subagent — its parent streams it
                if (type === "session.created") {
                  // START a top-level session only on a CONFIRMED classification — never on
                  // "unknown" (a session.get hiccup), which would misfile a child as both a
                  // top-level session and a subagent. "unknown" defers to the next idle.
                  if (await classify(sid, event.properties?.info) === "top") await start(sid, event.properties?.info)
                } else if (type === "session.idle") {
                  if (!started.has(sid)) {
                    if (await classify(sid) !== "top") return  // child → skip; unknown → retry next idle
                    await start(sid)
                  }
                  await flushTo(sid, file(sid))
                  await flushSubagents(sid)
                }
              } catch {
                // never disrupt the OpenCode session
              }
            },
          }
        }
        """;

    /// <summary>
    /// True when kcap.ts (or its marker) is present. Marker covers the case where
    /// a user deleted kcap.ts but kept the dir.
    /// </summary>
    public static bool IsInstalled(string pluginPath) {
        if (File.Exists(pluginPath)) return true;
        var dir = Path.GetDirectoryName(pluginPath);
        return dir is not null && File.Exists(Path.Combine(dir, MarkerFileName));
    }

    public static string? ReadMarker(string pluginPath) {
        var dir = Path.GetDirectoryName(pluginPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    public static void WriteMarker(string pluginPath) {
        var dir = Path.GetDirectoryName(pluginPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    public static void DeleteMarker(string pluginPath) {
        var dir = Path.GetDirectoryName(pluginPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
    }

    public static bool Install(string pluginPath) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
            File.WriteAllText(pluginPath, ExtensionContent);
            WriteMarker(pluginPath);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>Removes kcap.ts + marker. Returns true if kcap.ts existed.</summary>
    public static bool Remove(string pluginPath) {
        var existed = File.Exists(pluginPath);
        try {
            if (existed) File.Delete(pluginPath);
            DeleteMarker(pluginPath);
        } catch {
            return false;
        }
        return existed;
    }
}
