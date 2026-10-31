namespace Capacitor.Cli.Core.Harness.Pi;

/// <summary>
/// Embedded TypeScript shared by kcap.ts and kcap-mcp.ts: the one notice a Pi process prints when
/// <c>kcap</c> cannot be found. A terminal opened before kcap was installed keeps its old PATH, so
/// every spawn of <c>kcap</c> fails the same way; the notice state lives on <c>globalThis</c> so both
/// extensions together print it once. The splicing extension imports <c>statSync</c> from
/// <c>node:fs</c> and <c>delimiter</c>/<c>join</c> from <c>node:path</c>.
/// </summary>
public static class PiKcapPathSource {
    public const string Text =
        """

        const KCAP_PATH_NOTICE = ((globalThis as any).__kcapPiPathNotice ||= { shown: false });

        // Consulted only after running kcap failed, so a working setup never pays for the scan.
        function kcapOnPath(): boolean {
          const exts = process.platform === "win32"
            ? String(process.env.PATHEXT || ".COM;.EXE;.BAT;.CMD").split(";").filter(Boolean)
            : [""];
          for (const entry of String(process.env.PATH || "").split(delimiter)) {
            const dir = entry.trim().replace(/^"(.*)"$/, "$1");
            if (!dir) continue;
            for (const ext of exts) {
              try { if (statSync(join(dir, "kcap" + ext)).isFile()) return true; } catch { /* keep looking */ }
            }
          }
          return false;
        }

        // True when kcap is missing, so the caller drops its own per-attempt diagnostics.
        function noticeIfKcapMissing(): boolean {
          try {
            if (kcapOnPath()) return false;
            if (!KCAP_PATH_NOTICE.shown) {
              KCAP_PATH_NOTICE.shown = true;
              const hosted = process?.env?.KCAP_PI_PURE === "1";
              console.error("[kcap] kcap is not on this terminal's PATH; open a new terminal. "
                + (hosted ? "kcap tools are off in this Pi session." : "This Pi session is not being recorded, and kcap tools are off."));
            }
            return true;
          } catch {
            return false;
          }
        }

        """;
}
