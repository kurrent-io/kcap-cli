namespace Capacitor.Cli.Core;

/// <summary>
/// Whether this process likely runs in a container or VM that mounts the host's filesystem, so the
/// host may see a repository at a different path. A guess that decides only whether to ask the user:
/// nothing here can see the host's side of the mount.
///
/// <para>No single signal is reliable: a Docker sandbox showed a <c>/docker/</c> cgroup, a
/// <c>virtiofs</c> repository mount and <c>$SANDBOX_NAME</c>, but neither <c>/.dockerenv</c> nor
/// <c>/run/.containerenv</c>. Linux only.</para>
/// </summary>
public static class SandboxDetection {
    static readonly string[] ContainerCgroupMarkers = ["/docker/", "/docker-", "/containerd", "/kubepods", "/lxc/", "/libpod", "/podman"];

    static readonly string[] SharedFilesystemTypes = ["virtiofs", "9p", "fuse"];

    public static bool IsLikely(string path) =>
        OperatingSystem.IsLinux() && IsLikely(path, Environment.GetEnvironmentVariable, "/");

    /// <summary><paramref name="root"/> is the filesystem root the marker files and <c>/proc</c> are
    /// read under.</summary>
    public static bool IsLikely(string path, Func<string, string?> env, string root) {
        if (!string.IsNullOrEmpty(env("SANDBOX_NAME")) || !string.IsNullOrEmpty(env("container"))) return true;
        if (File.Exists(Path.Combine(root, ".dockerenv")) || File.Exists(Path.Combine(root, "run", ".containerenv"))) return true;
        if (CgroupNamesContainer(ReadOrNull(Path.Combine(root, "proc", "1", "cgroup")))) return true;

        return OnSharedMount(path, ReadOrNull(Path.Combine(root, "proc", "self", "mountinfo")));
    }

    public static bool CgroupNamesContainer(string? cgroup) =>
        cgroup is not null && ContainerCgroupMarkers.Any(m => cgroup.Contains(m, StringComparison.Ordinal));

    /// <summary>Whether the mount holding <paramref name="path"/> — the longest mount point that
    /// prefixes it in <c>/proc/self/mountinfo</c> — is a filesystem a VM or container shares from its
    /// host.</summary>
    public static bool OnSharedMount(string path, string? mountinfo) {
        if (mountinfo is null || !Path.IsPathRooted(path)) return false;

        string? bestPoint = null, bestType = null;
        foreach (var line in mountinfo.Split('\n')) {
            // <id> <parent> <major:minor> <root> <mount point> <options> [optional...] - <type> <source> <super options>
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (fields.Length < 5 || separator < 0 || separator + 1 >= fields.Length) continue;

            var point = Unescape(fields[4]);
            if (!Contains(point, path) || (bestPoint is not null && point.Length < bestPoint.Length)) continue;

            bestPoint = point;
            bestType = fields[separator + 1];
        }

        return bestType is not null && SharedFilesystemTypes.Any(t =>
            bestType == t || bestType.StartsWith(t + ".", StringComparison.Ordinal));
    }

    static bool Contains(string mountPoint, string path) =>
        mountPoint == "/" || path == mountPoint || path.StartsWith(mountPoint + "/", StringComparison.Ordinal);

    /// <summary>mountinfo writes space, tab, newline and backslash in a path as three-digit octal
    /// escapes.</summary>
    static string Unescape(string field) =>
        field.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

    static string? ReadOrNull(string path) {
        try {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return null;
        }
    }
}
