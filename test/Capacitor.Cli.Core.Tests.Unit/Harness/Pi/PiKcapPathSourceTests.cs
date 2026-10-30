using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Pi;

/// <summary>
/// Pins, as text, how kcap's Pi extensions behave when <c>kcap</c> is not on the terminal's PATH: one
/// notice for the whole Pi process instead of a line per MCP server, and no PATH scan unless running
/// kcap already failed. No runtime harness executes the embedded TypeScript, so these assert the source.
/// </summary>
public class PiKcapPathSourceTests {
    static readonly string Bridge = PiMcpExtensionInstaller.ExtensionContent;
    const string Ingest = PiExtensionInstaller.ExtensionContent;

    [Test]
    public async Task the_notice_is_one_line_naming_the_fix() {
        await Assert.That(PiKcapPathSource.Text)
            .Contains("console.error(\"[kcap] kcap is not on this terminal's PATH; open a new terminal. \"");
    }

    /// <summary>Both extensions read one flag on globalThis, so together they print the notice once.</summary>
    [Test]
    public async Task the_notice_is_shown_once_per_pi_process() {
        await Assert.That(PiKcapPathSource.Text).Contains("(globalThis as any).__kcapPiPathNotice ||= { shown: false }");
        await Assert.That(PiKcapPathSource.Text).Contains("if (!KCAP_PATH_NOTICE.shown) {");
        await Assert.That(PiKcapPathSource.Text).Contains("KCAP_PATH_NOTICE.shown = true;");
    }

    /// <summary>A hosted launch is recorded by the daemon, so its notice must not say recording is off;
    /// any other session with kcap missing is not recorded, and the notice must say so.</summary>
    [Test]
    public async Task the_notice_says_recording_is_off_only_where_it_is() {
        await Assert.That(PiKcapPathSource.Text).Contains("const hosted = process?.env?.KCAP_PI_PURE === \"1\";");
        await Assert.That(PiKcapPathSource.Text)
            .Contains("(hosted ? \"kcap tools are off in this Pi session.\" : \"This Pi session is not being recorded, and kcap tools are off.\")");
    }

    /// <summary>The scan resolves PATHEXT on Windows, where the binary is <c>kcap.exe</c> and npm's is <c>kcap.cmd</c>;
    /// a false "missing" there would tell a working install to open a new terminal.</summary>
    [Test]
    public async Task the_scan_honours_pathext_on_windows() {
        await Assert.That(PiKcapPathSource.Text).Contains("process.platform === \"win32\"");
        await Assert.That(PiKcapPathSource.Text).Contains("process.env.PATHEXT");
        await Assert.That(PiKcapPathSource.Text).Contains("statSync(join(dir, \"kcap\" + ext)).isFile()");
    }

    [Test]
    public async Task both_extensions_splice_the_notice_and_import_what_it_uses() {
        foreach (var ts in new[] { Bridge, Ingest }) {
            await Assert.That(ts).Contains(PiKcapPathSource.Text);
            await Assert.That(ts).Contains("import { statSync } from \"node:fs\";");
            await Assert.That(ts).Contains("import { delimiter, join } from \"node:path\";");
        }
    }

    /// <summary>A spawn ENOENT fails every server alike, so it is collected rather than logged per server,
    /// and the scan runs only when something was collected.</summary>
    [Test]
    public async Task the_bridge_reports_a_missing_kcap_once_not_per_server() {
        await Assert.That(Bridge).Contains("e.code === \"ENOENT\" && String(e.syscall || \"\").startsWith(\"spawn\")");
        await Assert.That(Bridge).Contains("if (isSpawnEnoent(e)) unspawnable.push(server);");
        await Assert.That(Bridge).Contains("else console.error(\"[kcap-mcp] \" + server + \" unavailable, skipping: \"");
        await Assert.That(Bridge).Contains("if (unspawnable.length && !noticeIfKcapMissing() && !spawnFailureShown) {");
    }

    /// <summary>A working setup must not change: the ingest extension consults the notice only after
    /// <c>kcap hook --pi</c> failed at session start, never on its success path or at shutdown.</summary>
    [Test]
    public async Task the_ingest_extension_checks_only_after_a_failed_session_start() {
        await Assert.That(Ingest)
            .Contains("if (event === \"session-start\" && typeof res?.code === \"number\" && res.code !== 0) noticeIfKcapMissing();");
        await Assert.That(Ingest).Contains("if (event === \"session-start\") noticeIfKcapMissing();");
        await Assert.That(Ingest.Split("noticeIfKcapMissing()").Length - 1).IsEqualTo(3);
    }
}
