namespace Capacitor.Cli.Core.Install;

/// <summary>
/// Where snapd mounts a snap: <c>/snap/&lt;name&gt;/&lt;revision&gt;</c>, with <c>/snap/&lt;name&gt;/current</c>
/// linking to the active revision. snapd removes old revisions after later refreshes, so a persisted path must
/// name <c>current</c>. Recognised from the binary's own path rather than <c>$SNAP</c>: agent hooks and systemd
/// run the binary directly, without the snap launcher that sets it.
/// </summary>
public sealed record SnapLayout(string Name, string Revision) {
    const string MountRoot = "/snap/";
    const string CurrentName = "current";

    public string Current => $"{MountRoot}{Name}/{CurrentName}";

    /// <summary>Installed from a file (`snap install --dangerous`, revisions x1, x2, …): the Store neither
    /// refreshes nor reinstalls it.</summary>
    public bool IsLocal => Revision.StartsWith('x');

    static readonly Lazy<SnapLayout?> Running = new(() => FromBinary(Environment.ProcessPath));

    /// <summary>The snap the running binary belongs to, or null when it is not running from one.</summary>
    public static SnapLayout? OfRunningBinary() => Running.Value;

    /// <summary>The snap a path under <c>/snap/&lt;name&gt;/&lt;revision&gt;/</c> belongs to; null for any other
    /// path, including one already under <c>current</c>.</summary>
    public static SnapLayout? FromBinary(string? path) {
        if (path is null || !path.StartsWith(MountRoot, StringComparison.Ordinal)) return null;

        var parts = path[MountRoot.Length..].Split('/');
        if (parts.Length < 3 || parts[0].Length == 0 || !IsRevision(parts[1])) return null;

        return new SnapLayout(parts[0], parts[1]);
    }

    // Store revisions are numbers; a local `snap install --dangerous` gets x1, x2, …
    static bool IsRevision(string s) {
        var digits = s.StartsWith('x') ? s[1..] : s;
        return digits.Length > 0 && digits.All(char.IsAsciiDigit);
    }

    /// <summary>The <c>current</c> form of a path inside a snap revision, when that form exists; otherwise
    /// <paramref name="path"/> unchanged.</summary>
    public static string Stabilize(string path, Func<string, bool> fileExists) {
        if (FromBinary(path) is not { } snap) return path;

        var stable = snap.Current + path[(MountRoot.Length + snap.Name.Length + 1 + snap.Revision.Length)..];

        return fileExists(stable) ? stable : path;
    }
}
