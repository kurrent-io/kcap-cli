using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Core.Recording;

/// <summary>The vendor store a session was recorded in, as a physical directory, or null when it cannot be
/// established. Null is unknown, never the default directory: a resume into the wrong store would not find
/// the session.</summary>
public static class RecordingStore {
    /// <summary>Claude and Codex keep one store per config directory, and their transcript path names it,
    /// so the environment is never consulted for them: a hook may see <c>CLAUDE_CONFIG_DIR</c> scrubbed.
    /// The ACP vendors have no accounts; their store is the root their own variable resolves to, unless a
    /// transcript they sent lies outside it.</summary>
    public static string? Of(HarnessId vendor, string? transcriptPath, HarnessRegistry harnesses) {
        try {
            return vendor switch {
                HarnessId.Claude   => ClaudeStore(transcriptPath),
                HarnessId.Codex    => CodexStore(transcriptPath),
                HarnessId.Cursor   => Rooted(harnesses.Of<CursorHarness>().Paths.CursorDir, transcriptPath),
                HarnessId.Copilot  => Rooted(harnesses.Of<CopilotHarness>().Paths.Root, transcriptPath),
                HarnessId.Kiro     => Rooted(harnesses.Of<KiroHarness>().Paths.ConfigRoot, transcriptPath),
                HarnessId.Gemini   => Rooted(harnesses.Of<GeminiHarness>().Paths.Root, transcriptPath),
                HarnessId.OpenCode => Rooted(harnesses.Of<OpenCodeHarness>().Paths.DataDir, transcriptPath),
                HarnessId.Grok     => Rooted(harnesses.Of<GrokHarness>().Paths.Home, transcriptPath),
                _                  => null,
            };
        } catch (Exception ex) when (IsNotAPath(ex) || ex is InvalidOperationException) {
            return null;
        }
    }

    /// <summary>The registry id of the account whose directory is <paramref name="store"/>, or null when
    /// none is registered.</summary>
    public static string? AccountFor(HarnessId vendor, string? store, AccountRegistry? registry) {
        if (string.IsNullOrEmpty(store) || registry is null) return null;

        return registry.Accounts.FirstOrDefault(a => a.Vendor == vendor && SameDirectory(a.Directory, store))?.Id;
    }

    // <store>/projects/<project>/<session>.jsonl
    static string? ClaudeStore(string? transcriptPath) {
        if (Full(transcriptPath) is not { } path || !IsJsonl(path)) return null;

        var projects = Path.GetDirectoryName(Path.GetDirectoryName(path));

        return Path.GetFileName(projects) == "projects" ? Physical(Path.GetDirectoryName(projects)) : null;
    }

    // <store>/sessions/YYYY/MM/DD/rollout-*.jsonl
    static string? CodexStore(string? rolloutPath) {
        if (Full(rolloutPath) is not { } path || !Path.GetFileName(path).StartsWith("rollout-", StringComparison.Ordinal) || !IsJsonl(path)) return null;

        var day   = Path.GetDirectoryName(path);
        var month = Path.GetDirectoryName(day);
        var year  = Path.GetDirectoryName(month);

        if (!Digits(Path.GetFileName(day), 2) || !Digits(Path.GetFileName(month), 2) || !Digits(Path.GetFileName(year), 4)) return null;

        var sessions = Path.GetDirectoryName(year);

        return Path.GetFileName(sessions) == "sessions" ? Physical(Path.GetDirectoryName(sessions)) : null;
    }

    static string? Rooted(string root, string? transcriptPath) {
        var store = Physical(root);

        if (store is null || Full(transcriptPath) is not { } path) return store;

        var parent = Physical(Path.GetDirectoryName(path));
        var cmp    = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return parent is not null && (parent + Path.DirectorySeparatorChar).StartsWith(store + Path.DirectorySeparatorChar, cmp) ? store : null;
    }

    static bool IsJsonl(string path) => path.EndsWith(".jsonl", StringComparison.Ordinal);

    static string? Full(string? path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    static string? Physical(string? directory) => string.IsNullOrEmpty(directory) ? null : AccountDirectory.Normalize(directory);

    static bool Digits(string? segment, int length) => segment is { } s && s.Length == length && s.All(char.IsAsciiDigit);

    static bool SameDirectory(string registered, string store) {
        try {
            return AccountDirectory.Same(registered, store);
        } catch (Exception ex) when (IsNotAPath(ex)) {
            return false;
        }
    }

    static bool IsNotAPath(Exception ex) => ex is ArgumentException or NotSupportedException or IOException;
}
