using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Watches a spawned CLI's PTY output stream for a one-time, full-screen consent/trust dialog that
/// a hosted, UNATTENDED launch (a review-flow reviewer) can never dismiss — so the process would
/// otherwise sit on the prompt forever, no session would ever start, and the launch would die
/// silently at the server's 30 s "waiting for session id" timeout with nothing surfaced.
///
/// The motivating case is Claude Code's Bypass-Permissions consent dialog ("WARNING: Claude Code
/// running in Bypass Permissions mode … 2. Yes, I accept"), which blocks on any host where the
/// interactive CLI has never accepted bypass mode. The launcher now pre-accepts it in the user
/// settings, but this detector is the belt-and-braces fail-fast for when that write didn't take
/// (organization policy override, a relocated config dir, a read-only settings file, etc.).
///
/// The detector maintains a bounded, ANSI-stripped rolling window of recent output so a banner
/// delivered across several PTY reads (or torn mid-word at a chunk boundary) still matches. A
/// signature trips only when ALL of its markers are present in the window, which keeps ordinary
/// session output — even output that mentions "bypass permissions mode" in prose — from tripping
/// it. Once tripped it latches: the launch is already doomed, so every later frame reports the
/// same reason.
///
/// Text-only + source-generated regex — no reflection — so it is safe in the NativeAOT daemon.
/// </summary>
internal sealed partial class ConsentDialogDetector {
    /// <summary>A known blocking dialog: it trips when every marker is present in the window.</summary>
    /// <param name="Markers">Ordinal-ignore-case substrings that must ALL be present to trip.</param>
    /// <param name="Message">The actionable, human-readable failure reported to the server.</param>
    readonly record struct Signature(string[] Markers, string Message);

    static readonly Signature[] Signatures = [
        new(
            ["bypass permissions mode", "yes, i accept"],
            "Claude is blocked on the Bypass-Permissions consent dialog and cannot be accepted "
          + "unattended. Accept it once by launching Claude interactively on this host and choosing "
          + "\"Yes, I accept\" (or by setting \"skipDangerousModePermissionPrompt\": true in the Claude "
          + "user settings), then retry the launch."),
        new(
            ["do you trust the files in this"],
            "Claude is blocked on the workspace-trust dialog and cannot be accepted unattended. Trust "
          + "the folder once by launching Claude interactively in it, then retry the launch."),
    ];

    // The banner box is well under this; the whole dialog (headline + wrapped explanation + options)
    // fits, so both markers of a multi-line signature co-exist in the window at once. Cheap in memory.
    const int WindowChars = 16384;

    readonly StringBuilder _window = new();
    string?                _tripped;

    /// <summary>True once a blocking dialog has been detected (latched).</summary>
    public bool Tripped => _tripped is not null;

    /// <summary>
    /// Feeds one raw PTY chunk. Returns the actionable failure message once a blocking dialog is
    /// detected (and on every call thereafter — it latches), else <c>null</c>. Never throws:
    /// decoding is lenient and matching is pure string work.
    /// </summary>
    public string? Observe(ReadOnlySpan<byte> chunk) {
        if (_tripped is not null) return _tripped;
        if (chunk.IsEmpty) return null;

        // Markers are ASCII, so a multi-byte char torn at a chunk boundary (box-drawing glyphs) only
        // corrupts non-marker glyphs — never the ASCII markers themselves.
        var text = Encoding.UTF8.GetString(chunk);
        Append(StripAnsi(text));

        var window = _window.ToString();

        foreach (var sig in Signatures) {
            var all = true;
            foreach (var marker in sig.Markers) {
                if (!window.Contains(marker, StringComparison.OrdinalIgnoreCase)) { all = false; break; }
            }

            if (all) return _tripped = sig.Message;
        }

        return null;
    }

    void Append(string text) {
        _window.Append(text);

        if (_window.Length > WindowChars) {
            _window.Remove(0, _window.Length - WindowChars);
        }
    }

    static string StripAnsi(string s) => AnsiRegex().Replace(s, "");

    // CSI (colour/cursor/erase), OSC (…BEL), charset-select, and private-mode sequences — the same
    // families AgentOrchestrator strips for its terminal-text extraction.
    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]|\x1B\].*?\x07|\x1B[()][AB012]|\x1B[=>]")]
    private static partial Regex AnsiRegex();
}
