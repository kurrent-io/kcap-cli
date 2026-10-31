using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>
/// The marks a headless <c>claude -p</c> run started by kcap carries, so discovery, import and live
/// capture can tell kcap's own runs from the user's sessions. Each mark is something only
/// <see cref="ClaudeCliRunner"/> produces: a session id under kcap's tag, and a working directory
/// named <c>kcap-claude-</c> plus 32 hex digits that it creates for one run and deletes after it.
/// </summary>
static partial class ClaudeHelperRun {
    // "kcap" in ASCII, then a version-4 group: 60 fixed bits, which a random v4 id matches by chance
    // once in 2^60. A v4 shape, not a dedicated version, so every Claude build accepts it.
    const string Tag  = "6b636170-6b63-4170-";
    const string TagN = "6b6361706b634170";

    const string WorkingDirPrefix = "kcap-claude-";

    static readonly char[] Separators = ['/', '\\'];

    /// <summary>A fresh <c>--session-id</c> carrying kcap's tag.</summary>
    internal static string NewSessionId() {
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        random[0] = (byte)(0x80 | (random[0] & 0x3F));
        var hex = Convert.ToHexStringLower(random);

        return $"{Tag}{hex[..4]}-{hex[4..]}";
    }

    /// <summary>The working directory name for the run that uses <paramref name="sessionId"/>.</summary>
    internal static string WorkingDirName(string sessionId) =>
        WorkingDirPrefix + Guid.Parse(sessionId).ToString("N");

    /// <summary>Whether a session id, dashed or not, is one kcap assigned to a helper run.</summary>
    internal static bool IsHelperSessionId(string? sessionId) =>
        sessionId is not null
     && Guid.TryParse(sessionId, out var id)
     && id.ToString("N").StartsWith(TagN, StringComparison.Ordinal);

    /// <summary>Whether a recorded working directory is one <see cref="ClaudeCliRunner"/> created. Both
    /// separators are accepted, so a transcript written on another platform is read the same way.</summary>
    internal static bool IsHelperWorkingDir(string? cwd) {
        if (string.IsNullOrEmpty(cwd)) return false;

        var trimmed = cwd.TrimEnd('/', '\\');
        var name    = trimmed[(trimmed.LastIndexOfAny(Separators) + 1)..];

        return WorkingDirRx().IsMatch(name);
    }

    /// <summary>Either mark, as a hook payload or a transcript line presents them.</summary>
    internal static bool IsHelperRun(string? sessionId, string? cwd) =>
        IsHelperSessionId(sessionId) || IsHelperWorkingDir(cwd);

    [GeneratedRegex("^kcap-claude-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkingDirRx();
}
