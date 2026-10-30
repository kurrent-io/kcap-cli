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

    public string DirectoryPath { get; }

    string? Root { get; }

    SpoolPartition(string dir, string? root) {
        DirectoryPath = dir;
        Root          = root;
    }

    public static SpoolPartition Server(string root, string? server) => new(Path.Combine(root, DirectoryName(server)), root);

    /// <summary>A directory outside any root: nothing to adopt, and the reap stays inside it.</summary>
    public static SpoolPartition Standalone(string dir) => new(dir, null);

    /// <param name="collisionName">The name a root file takes when its own name is already taken, or
    /// null to leave it for the reap. It must be a temp the adopting drain reads before the live file.</param>
    public void AdoptLegacyFiles(Func<string, string?> collisionName) {
        if (Root is null || !Directory.Exists(Root)) return;
        try {
            foreach (var file in Directory.EnumerateFiles(Root)) {
                try {
                    Directory.CreateDirectory(DirectoryPath);
                    var name   = Path.GetFileName(file);
                    var target = Path.Combine(DirectoryPath, name);
                    if (File.Exists(target) && collisionName(name) is { } other) target = Path.Combine(DirectoryPath, other);
                    File.Move(file, target);
                } catch {
                    // Ignore: left in the root for the next drain or the reap
                }
            }
        } catch {
            // Ignore: the next drain adopts what is left
        }
    }

    /// <summary>Counts root files the next drain will adopt, so a backlog spooled before the partition
    /// existed still holds back a later entry of the same session.</summary>
    public bool AnyFileMatches(string pattern) => Matches(DirectoryPath, pattern) || (Root is not null && Matches(Root, pattern));

    static bool Matches(string dir, string pattern) => Directory.Exists(dir) && Directory.EnumerateFiles(dir, pattern).Any();

    /// <summary>Sweeps every server's directory, so one no longer configured is reaped too.</summary>
    public void DeleteExpired(TimeProvider time) {
        var top = Root ?? DirectoryPath;
        if (!Directory.Exists(top)) return;
        var cutoff = (time.GetUtcNow() - Retention).UtcDateTime;
        try {
            foreach (var file in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories)) {
                try {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                } catch {
                    // Ignore: the next sweep retries it
                }
            }
        } catch {
            // Ignore: the next sweep retries
        }
    }

    static string DirectoryName(string? server) => server is null
        ? "none"
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ServerIdentity.Canonicalize(server) ?? server)))[..16];
}
