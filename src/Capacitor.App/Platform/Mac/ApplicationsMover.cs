using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Platform.Mac;

public sealed record MoveOutcome(bool Moved, string? InstalledPath, string? Error, bool PermissionDenied = false);

/// <summary>
/// Installs the running copy as <c>kcap.app</c> in an Applications folder, replacing an older installed copy and
/// superseding the other kcap bundles there (the running one, a pre-rebrand "Kurrent Capacitor.app"). Copying, the
/// swap and reference repair roll back together. The running bundle itself is never moved: a single-file app loads
/// assemblies from its executable on first use, so its removal goes to <paramref name="retirement"/> instead.
/// </summary>
public sealed partial class ApplicationsMover(
        IProcessRunner runner, Func<string, string, bool> promote, string applicationsDir = "/Applications",
        Func<string, string, bool>? swap = null, Func<string, bool>? isRunning = null,
        Func<string, CancellationToken, Task<bool>>? quitRunning = null,
        Func<IApplicationReferences>? migrateReferences = null,
        Func<string, CancellationToken, Task<bool>>? activate = null, string? userApplicationsDir = null,
        IBundleRetirement? retirement = null, string? systemApplicationsDir = null) {
    const string BundleName = KcapBundle.BundleName;
    static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(2);

    public static IReadOnlyList<string> ApplicationDirectories(string bundleRoot, string userApplicationsDir,
            string systemApplicationsDir = "/Applications") {
        var source = Path.GetFullPath(bundleRoot.TrimEnd('/'));
        bool Existing(string directory) => KcapBundle.Read(Path.Combine(directory, BundleName)) is not null;
        var directories = new[] { systemApplicationsDir, userApplicationsDir };
        var existing = directories.Where(Existing).ToArray();
        bool Conflicts(string directory) {
            var target = Path.Combine(directory, BundleName);
            return Path.GetFullPath(target) != source && Exists(target) && KcapBundle.Read(target) is null;
        }
        if (Conflicts(systemApplicationsDir) || existing.Any(Conflicts))
            return directories;
        // The running copy's folder owns the install only while it is the one installed: with a copy in the other
        // folder too, the daemon and terminal command follow only one of them, so the user picks which to keep.
        var home = Path.GetDirectoryName(source);
        if (Path.GetFileName(source) == BundleName && home is not null
                && existing.SequenceEqual([home]) && KcapBundle.Read(source) is { Legacy: false })
            return [home];
        if (existing.Length > 0) return existing;
        return [Path.GetDirectoryName(source) == userApplicationsDir ? userApplicationsDir : systemApplicationsDir];
    }

    public ApplicationsInstallPlan Inspect(string bundleRoot) {
        var target = Path.Combine(applicationsDir, BundleName);
        if (KcapBundle.Read(bundleRoot) is not { Legacy: false } source)
            return new(ApplicationsInstallAction.Blocked, target, Error: "This copy of kcap is incomplete or cannot be verified. Download it again.");
        var installed = KcapBundle.Read(target);
        if (Exists(target) && installed is null)
            return new(ApplicationsInstallAction.Blocked, target, source.Version, Error: $"There is already a kcap.app in {Path.GetDirectoryName(target)}, but it looks damaged or is not kcap, so it is left alone. Move it to the Trash, then come back to install.");
        var newer = installed is null || installed.Legacy || PrereleaseSemver.IsNewer(source.Version, installed.Version);
        var action = installed is null ? ApplicationsInstallAction.Install
            : newer ? ApplicationsInstallAction.Update : ApplicationsInstallAction.OpenInstalled;
        var installing = newer ? source.Version : installed!.Version;
        return new(action, target, installing, installed?.Version,
            ExistingPath: installed is null ? null : target, PayloadPath: newer ? bundleRoot : target,
            Replaces: Predecessors(bundleRoot, target, installing));
    }

    /// Every folder holds a verified kcap.app: the choice is which installed copy to keep, not where to install.
    public static bool InstalledInEach(IEnumerable<string> directories) =>
        directories.All(directory => KcapBundle.Read(Path.Combine(directory, BundleName)) is not null);

    string[] ApplicationFolders =>
        new[] { systemApplicationsDir, userApplicationsDir, applicationsDir }.OfType<string>().Select(Full).Distinct().ToArray();

    /// The running bundle when it sits in an Applications folder under another name or in the other folder, a
    /// kcap.app in the other folder, and a pre-rebrand bundle in either folder; never the target, never anything that
    /// is not a verified kcap bundle, and never one newer than what is being installed: a pre-rebrand path keeps
    /// receiving in-place updates.
    IReadOnlyList<string> Predecessors(string bundleRoot, string target, string installing) {
        var source = Full(bundleRoot);
        var folders = ApplicationFolders;
        var candidates = folders.Select(folder => Path.Combine(folder, KcapBundle.LegacyBundleName))
            .Concat(folders.Select(folder => Path.Combine(folder, BundleName))).ToList();
        if (Path.GetDirectoryName(source) is { } parent && folders.Contains(parent)) candidates.Insert(0, source);
        return candidates.Distinct()
            .Where(root => root != Full(target) && KcapBundle.Read(root) is { } bundle && !PrereleaseSemver.IsNewer(bundle.Version, installing))
            .ToArray();
    }

    static string Full(string path) => Path.GetFullPath(path.TrimEnd('/'));

    static bool Exists(string path) => Directory.Exists(path) || File.Exists(path) || new FileInfo(path).LinkTarget is not null;

    public async Task<MoveOutcome> MoveAsync(string bundleRoot, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var plan = Inspect(bundleRoot);
        if (plan.Action == ApplicationsInstallAction.Blocked) return new(false, plan.Target, plan.Error);
        var copies = plan.Action != ApplicationsInstallAction.OpenInstalled;
        if (!copies && plan.Replaces.Count == 0) return new(true, plan.Target, null);
        var source = Full(bundleRoot);
        var deferred = plan.Replaces.Contains(source) ? KcapBundle.Read(source) : null;
        var retiring = plan.Replaces.Where(root => root != source).ToArray();
        var roots = new[] { plan.ExistingPath, plan.Target }.Concat(retiring).OfType<string>().Distinct().Where(Directory.Exists).ToArray();
        foreach (var root in roots) {
            if (isRunning?.Invoke(root) != true) continue;
            if (quitRunning is null || !await quitRunning(root, ct))
                return new(false, plan.Target, Path.GetFileName(root) == KcapBundle.LegacyBundleName
                    ? "Could not quit Kurrent Capacitor. Quit it and try again."
                    : "Could not quit the installed copy of kcap. Quit it and try again.");
        }
        var staging = Path.Combine(applicationsDir, $"kcap.app.staging-{Guid.NewGuid():N}");
        var displaced = new List<(string Original, string Backup)>();
        IApplicationReferences? references = null;
        var promoted = false;
        var swapped = false;
        var scheduled = false;
        var destinationPermissionDenied = false;
        void WriteDestination(Action write) {
            try { write(); }
            catch (UnauthorizedAccessException) {
                destinationPermissionDenied = true;
                throw;
            }
        }
        void CancelRetirement() {
            if (!scheduled) return;
            scheduled = false;
            retirement?.Cancel(source);
        }
        try {
            if (copies) {
                WriteDestination(() => {
                    Directory.CreateDirectory(applicationsDir);
                    Directory.CreateDirectory(staging);
                });
                var copy = await runner.RunAsync("ditto", [plan.PayloadPath!, staging], new RunOptions(Timeout: CopyTimeout), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (copy.TimedOut) return Fail(staging, "Copying took too long. Try again.");
                if (copy.ExitCode != 0) return Fail(staging, $"Copying failed: {copy.Stderr.Trim()}");
                if (KcapBundle.Read(staging)?.Version != plan.SourceVersion) return Fail(staging, "The copy is incomplete.");
                if (Inspect(bundleRoot) != plan) return Fail(staging, "The installed copy changed while copying. Try again.");
            }
            if (roots.Any(root => isRunning?.Invoke(root) == true)) return Fail(staging, "An installed copy opened while copying. Quit it and try again.");

            references = migrateReferences?.Invoke();
            if (references is not null) await references.PrepareAsync([.. plan.Replaces.Concat(roots).Distinct()], plan.Target, ct).ConfigureAwait(false);
            if (copies) {
                WriteDestination(() => {
                    if (Exists(plan.Target)) {
                        if (swap?.Invoke(staging, plan.Target) != true) throw new IOException("The update could not be installed. The existing copy has not been changed.");
                        swapped = true;
                    } else {
                        if (!promote(staging, plan.Target)) throw new IOException("kcap.app appeared while copying. Open that copy instead.");
                        promoted = true;
                    }
                });
            }
            if (references is not null) await references.ApplyAsync(ct).ConfigureAwait(false);
            foreach (var root in retiring) {
                var backup = root + ".migrating-" + Guid.NewGuid().ToString("N");
                Directory.Move(root, backup);
                displaced.Add((root, backup));
            }
            if (deferred is not null && retirement is not null) {
                retirement.Schedule(deferred);
                scheduled = true;
            }
            if (activate is not null && !await activate(plan.Target, ct)) {
                if (isRunning?.Invoke(plan.Target) == true && (quitRunning is null || !await quitRunning(plan.Target, CancellationToken.None))) {
                    CancelRetirement();
                    return new(false, plan.Target, "The replacement app could not be closed. The new installation and recovery backups have been retained; open the installed kcap app before retrying.");
                }
                throw new IOException("Could not confirm kcap opened. The installation will be restored; try again.");
            }
            foreach (var (_, backup) in displaced) RemoveStaging(backup);
            RemoveStaging(staging);
            return new(true, plan.Target, null);
        } catch (Exception ex) {
            var errors = new List<string> { ex.Message };
            void RestoreBundles() {
                foreach (var (original, backup) in displaced.AsEnumerable().Reverse()) Directory.Move(backup, original);
                if (swapped && swap?.Invoke(staging, plan.Target) != true)
                    throw new IOException($"Could not restore {plan.Target}; its backup is {staging}.");
                if (promoted) Directory.Move(plan.Target, staging);
            }
            try { CancelRetirement(); } catch (Exception cancel) { errors.Add(cancel.Message); }
            try {
                if (references is not null) await references.RollbackAsync(RestoreBundles).ConfigureAwait(false);
                else RestoreBundles();
            } catch (Exception restore) { errors.Add(restore.Message); }
            if (errors.Count == 1) RemoveStaging(staging);
            if (ex is OperationCanceledException && errors.Count == 1) throw;
            return new(false, plan.Target, string.Join("\n\n", errors),
                PermissionDenied: destinationPermissionDenied && errors.Count == 1);
        } finally {
            if (references is not null) await references.DisposeAsync().ConfigureAwait(false);
        }
    }

    static MoveOutcome Fail(string staging, string error) {
        RemoveStaging(staging);
        return new MoveOutcome(false, null, error);
    }

    static void RemoveStaging(string staging) {
        try {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not remove staging bundle: {ex.Message}");
        }
    }

    /// A plain rename replaces an EMPTY existing directory; RENAME_EXCL fails on any existing entry.
    [SupportedOSPlatform("macos")]
    public static bool PromoteExclusive(string from, string to) => Rename(from, to, RENAME_EXCL);

    [SupportedOSPlatform("macos")]
    public static bool SwapAtomic(string from, string to) => Rename(from, to, 0x2);

    [SupportedOSPlatform("macos")]
    static bool Rename(string from, string to, uint flags) {
        if (renamex_np(from, to, flags) == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error is EPERM or EACCES or EROFS)
            throw new UnauthorizedAccessException($"Cannot write to {to}.", new Win32Exception(error));
        return false;
    }

    const uint RENAME_EXCL = 0x4;
    const int EPERM = 1, EACCES = 13, EROFS = 30;

    [LibraryImport("libc", EntryPoint = "renamex_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int renamex_np(string from, string to, uint flags);
}
