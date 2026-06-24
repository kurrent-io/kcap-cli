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

          // A child session (subagent) must NEVER be ingested as a top-level session.
          // session.created MAY carry info.parentID, but session.idle does not, and a
          // child's idle can fire before its parent is discovered — so resolve parentID
          // authoritatively via the SDK when it isn't already known. Fail-open to
          // top-level (a transient lookup failure must not drop a real session).
          async function isChild(sid: string, info?: any) {
            if (children.has(sid)) return true
            let parentId = info?.parentID
            if (parentId === undefined || parentId === null) {
              try {
                const s: any = await client.session.get({ path: { id: sid } })
                parentId = s?.parentID ?? s?.data?.parentID ?? null
              } catch { parentId = null }
            }
            if (parentId) { children.add(sid); return true }
            return false
          }

          // The server keys events by the part's stable prt_ id and keeps the FIRST
          // append, so a message must only be written once its content is final. A user
          // message is always final; an assistant message is final once it carries a
          // finish/completed marker. Gates subagent flushing so a still-streaming child
          // can't lock in partial content (the task tool blocks the parent until the
          // child completes, so by parent-idle this passes).
          function isFinal(m: any) {
            const info = m?.info
            if (info?.role !== "assistant") return true
            return info.finish != null || info.time?.completed != null
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
          async function flushTo(sid: string, targetFile: string, finalOnly = false) {
            try {
              const res: any = await client.session.messages({ path: { id: sid } })
              const msgs: any[] = Array.isArray(res) ? res : (res?.data ?? [])
              const seen = written.get(targetFile) ?? new Set<string>()
              written.set(targetFile, seen)
              const lines: string[] = []
              for (const m of msgs) {
                const id = m?.info?.id
                if (!id) continue
                // Subagents: skip a still-streaming message so its partial content isn't
                // the first (permanent) append for its part ids.
                if (finalOnly && !isFinal(m)) continue
                const key = id + ":" + (m?.parts?.length ?? 0)
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

          // On the parent's idle, stream any child sessions (subagents) into the nested dir
          // the watcher scans. The task tool blocks the parent until the child completes, so
          // by parent-idle the child transcript is whole.
          async function flushSubagents(parent: string) {
            try {
              const res: any = await client.session.children({ path: { id: parent } })
              const kids: any[] = Array.isArray(res) ? res : (res?.data ?? [])
              for (const k of kids) {
                const cid = k?.id
                if (!cid) continue
                children.add(cid)
                await flushTo(cid, childFile(parent, cid), true)
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
                if (type === "session.created") {
                  // Skip child sessions (subagents) on the top-level path — the parent's
                  // flushSubagents streams them. isChild resolves parentID via the SDK when
                  // the event doesn't carry it, so this can't misfile a child as top-level.
                  if (await isChild(sid, event.properties?.info)) return
                  await start(sid, event.properties?.info)
                } else if (type === "session.idle") {
                  if (await isChild(sid)) return
                  if (!started.has(sid)) await start(sid)
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
