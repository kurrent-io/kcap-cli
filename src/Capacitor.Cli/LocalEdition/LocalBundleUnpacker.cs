using System.Formats.Tar;
using System.IO.Compression;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Unpacks the signed bundle into a directory that does not exist yet, writing nothing outside it.
/// Entries are refused rather than skipped, so a bundle that is not exactly what the publisher built
/// never becomes a version directory. Every file is flushed and every directory synced.
/// </summary>
public static class LocalBundleUnpacker {
    const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const string ServerName          = "kcap-server";

    public static async Task UnpackAsync(string archive, string destination, CancellationToken ct) {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));

        if (Directory.Exists(root) || File.Exists(root))
            throw new LocalBundleException(LocalInstallFailureCode.VerificationFailed, $"{destination}: already exists");

        try {
            await Extract(archive, root, ct);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) {
            throw new LocalBundleException(LocalInstallFailureCode.VerificationFailed, $"{archive}: {ex.Message}", ex);
        }
    }

    // Symlinks are created only after every directory and file exists, so nothing is written or
    // chmodded through a link, and case or Unicode aliasing on the volume can only alias paths
    // inside the tree.
    static async Task Extract(string archive, string root, CancellationToken ct) {
        MakeDir(root);

        var directories = new List<string> { root };
        var knownDirs   = new HashSet<string>(StringComparer.Ordinal) { root };
        var files       = new HashSet<string>(StringComparer.Ordinal);
        var symlinks    = new List<(string Name, string Full, string Target)>();

        await using (var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
        using (var reader = new TarReader(gzip)) {
            while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry) {
                try {
                    await Process(entry, root, directories, knownDirs, files, symlinks, ct);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    throw new LocalBundleException(LocalInstallFailureCode.VerificationFailed, $"{entry.Name}: {ex.Message}", ex);
                }
            }
        }

        foreach (var (name, full, target) in symlinks) {
            try {
                File.CreateSymbolicLink(full, target);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                throw new LocalBundleException(LocalInstallFailureCode.VerificationFailed, $"{name}: {ex.Message}", ex);
            }
        }

        if (symlinks.Count > 0) {
            if (!CanonicalPath.TryResolve(root, out var canonicalRoot))
                throw Refuse(root, "the destination cannot be resolved");

            foreach (var (name, full, _) in symlinks) {
                if (!CanonicalPath.TryResolve(full, out var resolved) || !IsWithin(resolved, canonicalRoot))
                    throw Refuse(name, "symbolic link leaves the tree");
            }
        }

        var server = Path.Combine(root, ServerName);

        if (!files.Contains(server)
         || (!OperatingSystem.IsWindows() && !File.GetUnixFileMode(server).HasFlag(UnixFileMode.UserExecute)))
            throw Refuse(ServerName, "the bundle has no executable kcap-server at its root");

        for (var i = directories.Count - 1; i >= 0; i--) DurableDirectory.Sync(directories[i]);

        DurableDirectory.Sync(Path.GetDirectoryName(root)!);
    }

    static async Task Process(TarEntry entry, string root, List<string> directories, HashSet<string> knownDirs, HashSet<string> files,
        List<(string Name, string Full, string Target)> symlinks, CancellationToken ct) {
        var relative = Normalize(entry.Name);

        if (relative.Length == 0) {
            if (entry.EntryType != TarEntryType.Directory) throw Refuse(entry.Name, "the tree root must be a directory");

            return;
        }

        var full = Path.GetFullPath(Path.Combine(root, relative));

        EnsureParents(root, full, knownDirs, directories);

        switch (entry.EntryType) {
            case TarEntryType.Directory:
                EnsureDir(full, knownDirs, directories);

                break;

            case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                await WriteFile(entry, full, ct);
                files.Add(full);

                break;

            case TarEntryType.SymbolicLink:
                var target = entry.LinkName;

                if (string.IsNullOrEmpty(target) || target.Contains('\0') || target.StartsWith('/') || Path.IsPathRooted(target))
                    throw Refuse(entry.Name, "symbolic link target is absolute or empty");

                symlinks.Add((entry.Name, full, target));

                break;

            case TarEntryType.HardLink:
                await CopyHardLink(entry, full, root, files, ct);
                files.Add(full);

                break;

            default:
                throw Refuse(entry.Name, $"entry type {entry.EntryType} is not allowed");
        }
    }

    static bool IsWithin(string path, string boundary) =>
        path == boundary || path.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    static string Normalize(string name) {
        if (name.Contains('\0')) throw Refuse(name, "invalid name");

        var rest = name;

        while (rest.StartsWith("./", StringComparison.Ordinal)) rest = rest[2..];

        if (rest.StartsWith('/') || rest.StartsWith('\\') || Path.IsPathRooted(rest)) throw Refuse(name, "absolute path");

        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();

        if (parts.Any(p => p == ".." || p.Contains('\\'))) throw Refuse(name, "path escapes the tree");

        return string.Join('/', parts);
    }

    static void EnsureParents(string root, string full, HashSet<string> knownDirs, List<string> directories) {
        var missing = new Stack<string>();

        for (var dir = Path.GetDirectoryName(full)!; dir != root; dir = Path.GetDirectoryName(dir)!)
            if (!knownDirs.Contains(dir)) missing.Push(dir);

        while (missing.Count > 0) EnsureDir(missing.Pop(), knownDirs, directories);
    }

    static void EnsureDir(string dir, HashSet<string> knownDirs, List<string> directories) {
        if (!knownDirs.Add(dir)) return;

        directories.Add(dir);

        if (!Directory.Exists(dir)) MakeDir(dir);
    }

    static async Task WriteFile(TarEntry entry, string full, CancellationToken ct) {
        var mode = FileMode(entry);

        await using (var stream = Create(full, mode)) {
            if (entry.DataStream is { } data) await data.CopyToAsync(stream, ct);

            FaultPoints.Hit($"flush:{full}");
            stream.Flush(true);
        }

        SetMode(full, mode);
    }

    static async Task CopyHardLink(TarEntry entry, string full, string root, HashSet<string> files, CancellationToken ct) {
        var target = Path.GetFullPath(Path.Combine(root, Normalize(entry.LinkName)));

        if (!files.Contains(target)) throw Refuse(entry.Name, "hard link target is not an earlier file");

        var mode = FileMode(entry);

        await using (var from = new FileStream(target, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var to = Create(full, mode)) {
            await from.CopyToAsync(to, ct);
            FaultPoints.Hit($"flush:{full}");
            to.Flush(true);
        }

        SetMode(full, mode);
    }

    static FileStream Create(string path, UnixFileMode mode) {
        var options = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };

        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = mode;

        return new FileStream(path, options);
    }

    static UnixFileMode FileMode(TarEntry entry) => entry.Mode & DirectoryMode | UnixFileMode.UserRead;

    static void MakeDir(string path) {
        if (OperatingSystem.IsWindows()) {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, DirectoryMode);
        File.SetUnixFileMode(path, DirectoryMode);
    }

    static void SetMode(string path, UnixFileMode mode) {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    static LocalBundleException Refuse(string entry, string reason) =>
        new(LocalInstallFailureCode.VerificationFailed, $"{entry}: {reason}");
}
