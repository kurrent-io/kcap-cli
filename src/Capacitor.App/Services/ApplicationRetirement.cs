using Capacitor.App.Platform.Mac;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// <summary>
/// Persists the bundle an installing copy ran from, so the installed copy removes it once nothing runs from it.
/// The record names the bundle's version, and a bundle that changed since is left alone. Removal renames the
/// bundle aside before deleting it, and records that name first, so a crash part-way leaves something the next
/// launch can finish rather than a half-deleted app under its real name.
/// </summary>
internal sealed class ApplicationRetirement(ConfigRoot config, TimeProvider time, Func<string, int[]?>? running = null) : IBundleRetirement {
    internal const string FileName = "app-retire-pending";
    static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(60);
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    sealed record Pending(string Root, string Version, string? Retired);

    readonly Func<string, int[]?> _running = running ?? InstalledAppProcesses.Find;

    public void Schedule(KcapBundle bundle) {
        Directory.CreateDirectory(config.Directory);
        using var guard = config.AcquireLock(FileName);
        Write(new Pending(Path.GetFullPath(bundle.Root), bundle.Version, null));
    }

    public void Cancel(string root) {
        using var guard = config.AcquireLock(FileName);
        if (Read() is { } pending && pending.Root == Path.GetFullPath(root)) File.Delete(config.Path(FileName));
    }

    /// <summary>Removes a bundle an earlier install handed over, waiting a bounded time for its process to exit.
    /// Returns what it removed, or null; a bundle still in use keeps its record for the next launch.</summary>
    public async Task<string?> RunPendingAsync(string? ownRoot, CancellationToken ct) {
        if (!File.Exists(config.Path(FileName))) return null;
        Pending? pending;
        using (config.AcquireLock(FileName)) pending = Read();
        if (pending is null) {
            Clear();
            return null;
        }

        // Only the name this class gives a bundle it sets aside: a half-deleted one no longer reads as kcap.
        if (pending.Retired is { } aside && Path.GetDirectoryName(aside) == Path.GetDirectoryName(pending.Root)
                && Path.GetFileName(aside).StartsWith(Path.GetFileName(pending.Root) + ".retired-", StringComparison.Ordinal)
                && Directory.Exists(aside) && new DirectoryInfo(aside).LinkTarget is null)
            Directory.Delete(aside, recursive: true);
        if (ownRoot is not null && Path.GetFullPath(ownRoot) == pending.Root
                || KcapBundle.Read(pending.Root) is not { } bundle || bundle.Version != pending.Version) {
            Clear();
            return null;
        }

        var started = time.GetTimestamp();
        while (_running(pending.Root) is not [] ) {
            if (time.GetElapsedTime(started) >= ExitWait) return null;
            await Task.Delay(Poll, time, ct).ConfigureAwait(false);
        }

        // The wait can outlast an install of another version at the same path, which is not this record's to remove.
        if (KcapBundle.Read(pending.Root)?.Version != pending.Version) {
            Clear();
            return null;
        }
        var retired = pending.Root + ".retired-" + Guid.NewGuid().ToString("N");
        using (config.AcquireLock(FileName)) {
            if (Read() != pending) return null;
            Write(pending with { Retired = retired });
        }
        Directory.Move(pending.Root, retired);
        if (KcapBundle.Read(retired)?.Version != pending.Version) {
            Directory.Move(retired, pending.Root);
            Clear();
            return null;
        }
        Directory.Delete(retired, recursive: true);
        Clear();
        return pending.Root;
    }

    void Clear() {
        using var guard = config.AcquireLock(FileName);
        File.Delete(config.Path(FileName));
    }

    Pending? Read() {
        try {
            var lines = File.ReadAllLines(config.Path(FileName));
            if (lines.Length is < 2 or > 3 || !Path.IsPathFullyQualified(lines[0]) || lines[1].Length == 0) return null;
            return new Pending(lines[0], lines[1], lines.Length == 3 && lines[2].Length > 0 ? lines[2] : null);
        } catch (FileNotFoundException) {
            return null;
        }
    }

    void Write(Pending pending) {
        var staging = config.Path(FileName + "." + Guid.NewGuid().ToString("N"));
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(staging, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(string.Join('\n', new[] { pending.Root, pending.Version, pending.Retired ?? "" }));
            File.Move(staging, config.Path(FileName), true);
        } finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
