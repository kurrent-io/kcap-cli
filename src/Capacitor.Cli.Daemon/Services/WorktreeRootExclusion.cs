using System.Text;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The <c>info/exclude</c> line that keeps <c>.capacitor/worktrees/</c> out of a checkout's status. The
/// file is the user's, so the line is only ever appended, once; nothing already in it is rewritten.</summary>
internal static class WorktreeRootExclusion {
    const string Root = ".capacitor/worktrees/";

    /// <summary>The anchored pattern for the worktree root under a launch directory; null for a prefix no
    /// exclude line can spell.</summary>
    /// <param name="prefix">The launch directory relative to its work-tree top, as <c>rev-parse --show-prefix</c>
    /// prints it: empty at the top, otherwise ending in a slash.</param>
    public static string? Pattern(string prefix) {
        if (prefix.AsSpan().ContainsAny('\n', '\r')) return null;

        var escaped = new StringBuilder("/", prefix.Length + Root.Length + 1);
        foreach (var c in prefix) {
            if (c is '\\' or '*' or '?' or '[') escaped.Append('\\');
            escaped.Append(c);
        }

        return escaped.Append(Root).ToString();
    }

    /// <summary>Appends <paramref name="pattern"/> to the exclude file unless a line already reads exactly that,
    /// in the file's own line ending. False when it was already there.</summary>
    public static bool Apply(string excludePath, string pattern) {
        var text = File.Exists(excludePath) ? Encoding.UTF8.GetString(File.ReadAllBytes(excludePath)) : "";
        if (text.Split('\n').Any(line => line.TrimEnd('\r') == pattern)) return false;

        var eol  = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lead = text.Length == 0 || text.EndsWith('\n') ? "" : eol;

        Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
        using var stream = new FileStream(excludePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes(lead + pattern + eol));

        return true;
    }
}
