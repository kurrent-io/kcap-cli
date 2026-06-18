namespace Capacitor.Cli.Core.Pi;

/// <summary>
/// Installs / removes kcap's live-ingest extension for Pi. Pi has no shell
/// hooks, so instead of writing a hooks.json (Copilot/Cursor) kcap ships a
/// TypeScript extension file (<c>~/.pi/agent/extensions/kcap.ts</c>) that Pi
/// auto-discovers and loads in-process. The extension shells out to
/// <c>kcap hook --pi</c> on <c>session_start</c>/<c>session_shutdown</c>.
///
/// <para><see cref="ExtensionContent"/> is the single source of truth for the
/// installed file (embedding it as a const keeps NativeAOT happy — no manifest
/// resource reflection). A version marker beside it gates the upgrade-time
/// refresh, mirroring <c>CopilotHooksInstaller</c>.</para>
/// </summary>
public static class PiExtensionInstaller {
    public const string MarkerFileName = ".kcap-extension-version";

    /// <summary>
    /// The kcap Pi extension. Untyped (<c>pi: any</c>) so it carries no runtime
    /// dependency on the <c>@earendil-works/pi-coding-agent</c> types, and
    /// fail-safe so a kcap/server hiccup never disrupts the pi session.
    /// </summary>
    public const string ExtensionContent =
        """
        // kcap.ts — Kurrent Capacitor live-ingest extension for Pi (badlogic/pi-mono).
        //
        // Installed by `kcap plugin install --pi` into ~/.pi/agent/extensions/kcap.ts.
        // Pi has no shell hooks, so this extension bridges Pi's in-process lifecycle
        // events to the kcap CLI: on session start/shutdown it invokes
        // `kcap hook --pi`, which POSTs /hooks/session-{start,end}/pi and runs the
        // transcript watcher (vendor=pi). Safe-by-default — every handler swallows
        // errors so a kcap or server hiccup never disrupts the pi session.

        export default function (pi: any) {
          async function notify(event: string, ctx: any, reason?: string) {
            try {
              const file = ctx?.sessionManager?.getSessionFile?.();
              if (!file) return; // ephemeral (--no-session): nothing to record
              const args = ["hook", "--pi", "--event", event, "--file", String(file)];
              if (ctx?.cwd) args.push("--cwd", String(ctx.cwd));
              if (reason) args.push("--reason", String(reason));
              // kcap spawns a detached watcher and returns fast; bound it so a hung
              // kcap can never stall pi's startup or shutdown.
              await pi.exec("kcap", args, { timeout: 10000 });
            } catch {
              // never disrupt the pi session
            }
          }

          pi.on("session_start", async (event: any, ctx: any) => {
            await notify("session-start", ctx, event?.reason);
          });

          pi.on("session_shutdown", async (event: any, ctx: any) => {
            await notify("session-end", ctx, event?.reason);
          });
        }
        """;

    /// <summary>
    /// True when kcap.ts (or its marker) is present. Marker covers the case
    /// where a user deleted kcap.ts but kept the dir.
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

    /// <summary>Removes kcap.ts + marker. Returns true if kcap.ts existed.</summary>
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
