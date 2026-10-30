using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core;

/// <summary>
/// Records which server a session's spooled entries were meant for, so a drain posting to another
/// server leaves them alone. Every server's hooks and daemon share one spool directory. A session
/// with no stamp was spooled before stamping existed and goes to whichever server drains it.
/// </summary>
static class SpoolServerStamp {
    /// <summary>Best-effort: a concurrent hook or drain holding the stamp open on Windows must not
    /// fail the append it guards, and the stamp already there almost always names the same server.</summary>
    public static void Write(string dir, string key, string? server) {
        if (server is null) return;
        try { File.WriteAllText(PathOf(dir, key), Identity(server)); }
        catch { }
    }

    public static bool BelongsElsewhere(string dir, string key, string? server) {
        if (server is null) return false;

        var path = PathOf(dir, key);
        if (!File.Exists(path)) return false;

        try { return File.ReadAllText(path) != Identity(server); }
        catch { return true; }
    }

    static string Identity(string server) => ServerIdentity.Canonicalize(server) ?? server;

    static string PathOf(string dir, string key) => Path.Combine(dir, $".server-{key}");
}
