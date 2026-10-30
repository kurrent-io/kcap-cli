using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core;

/// <summary>
/// One server's spool directory under the shared root, so a drain only ever sees what was spooled
/// for the server it posts to. Files directly under the root were spooled before the partition
/// existed, and the first drain to run adopts them for its own server.
/// </summary>
public sealed class SpoolPartition {
    static TimeSpan Retention { get; } = TimeSpan.FromDays(30);

    public string Dir { get; }

    string? Root { get; }

    SpoolPartition(string dir, string? root) {
        Dir  = dir;
        Root = root;
    }

    public static SpoolPartition Partitioned(string root, string? server) => new(Path.Combine(root, Key(server)), root);

    /// <summary>A directory outside any root: nothing to adopt, and the reap stays inside it.</summary>
    public static SpoolPartition Standalone(string dir) => new(dir, null);

    /// <param name="collisionName">The name a root file takes when its own name is already taken, or
    /// null to leave it for the reap. It must be a temp the adopting drain reads before the live file.</param>
    public void AdoptUnpartitioned(Func<string, string?> collisionName) {
        if (Root is null || !Directory.Exists(Root)) return;
        try {
            foreach (var file in Directory.EnumerateFiles(Root)) {
                try {
                    Directory.CreateDirectory(Dir);
                    var name   = Path.GetFileName(file);
                    var target = Path.Combine(Dir, name);
                    if (File.Exists(target) && collisionName(name) is { } other) target = Path.Combine(Dir, other);
                    File.Move(file, target);
                } catch { }
            }
        } catch { }
    }

    /// <summary>Sweeps every server's directory, so one no longer configured is reaped too.</summary>
    public void DeleteExpired(TimeProvider time) {
        var top = Root ?? Dir;
        if (!Directory.Exists(top)) return;
        var cutoff = (time.GetUtcNow() - Retention).UtcDateTime;
        try {
            foreach (var file in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories)) {
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); } catch { }
            }
        } catch { }
    }

    static string Key(string? server) => server is null
        ? "none"
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ServerIdentity.Canonicalize(server) ?? server)))[..16];
}
