using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>The registered account a vendor-supplied path belongs to. Hooks carry no account and
/// may run with <c>CLAUDE_CONFIG_DIR</c> scrubbed, so the transcript path is the evidence.</summary>
public static class AccountPaths {
    public static ClaudePaths? ClaudeForTranscript(string transcriptPath, AccountRegistry registry, UserHome home) =>
        string.IsNullOrWhiteSpace(transcriptPath) ? null : registry.Accounts
            .Where(a => a.Vendor == HarnessId.Claude)
            .Select(a => AccountLayouts.Claude(home, a.Directory))
            .FirstOrDefault(p => Contains(p.Projects, transcriptPath));

    public static CodexPaths? CodexForRollout(string rolloutPath, AccountRegistry registry, UserHome home) =>
        string.IsNullOrWhiteSpace(rolloutPath) ? null : registry.Accounts
            .Where(a => a.Vendor == HarnessId.Codex)
            .Select(a => AccountLayouts.Codex(home, a.Directory))
            .FirstOrDefault(p => Contains(p.Sessions, rolloutPath));

    // The trailing separator keeps /h/.claude from claiming /h/.claude-work. The second test covers
    // a path whose parent directory is reached through a symlink. A malformed payload path is simply
    // outside every account: a hook must never throw on it.
    static bool Contains(string root, string path) {
        try {
            var r   = AccountDirectory.Normalize(root) + Path.DirectorySeparatorChar;
            var p   = Path.GetFullPath(path);
            var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return p.StartsWith(r, cmp) || AccountDirectory.Normalize(Path.GetDirectoryName(p) ?? p).StartsWith(r, cmp);
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            return false;
        }
    }
}
