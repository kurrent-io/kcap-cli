using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core;

/// <summary>
/// Gives each server its own spool directory under the shared root, so a drain only ever sees what
/// was spooled for the server it posts to. Files directly under the root were spooled before the
/// partition existed, and the first drain to run adopts them for its own server.
/// </summary>
static class SpoolPartition {
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public static string DirFor(string root, string? server) =>
        Path.Combine(root, server is null
            ? "none"
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ServerIdentity.Canonicalize(server) ?? server)))[..16]);

    /// <param name="onCollision">The name a root file takes when its own name is already taken in
    /// <paramref name="dir"/>, or null to leave it for the reap.</param>
    public static void AdoptUnpartitioned(string root, string dir, Func<string, string?> onCollision) {
        if (!Directory.Exists(root)) return;
        try {
            foreach (var file in Directory.EnumerateFiles(root)) {
                try {
                    Directory.CreateDirectory(dir);
                    var name   = Path.GetFileName(file);
                    var target = Path.Combine(dir, name);
                    if (File.Exists(target) && onCollision(name) is { } other) target = Path.Combine(dir, other);
                    File.Move(file, target);
                } catch { }
            }
        } catch { }
    }

    public static void Reap(string root, DateTime cutoff) {
        if (!Directory.Exists(root)) return;
        try {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); } catch { }
            }
        } catch { }
    }
}
