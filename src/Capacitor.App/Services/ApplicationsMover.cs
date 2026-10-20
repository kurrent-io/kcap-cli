using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

public sealed record MoveOutcome(bool Moved, string? InstalledPath, string? Error);

public sealed partial class ApplicationsMover(
        IProcessRunner runner, Func<string, string, bool> promote, string applicationsDir = "/Applications",
        Func<string, string, bool>? swap = null, Func<string, bool>? isRunning = null,
        Func<string, CancellationToken, Task<bool>>? quitRunning = null,
        Func<IApplicationReferences>? migrateReferences = null,
        Func<string, CancellationToken, Task<bool>>? activate = null, string? userApplicationsDir = null) {
    static readonly string[] BundleNames = ["kcap.app", "Kurrent Capacitor.app"];
    static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(2);

    public static IReadOnlyList<string> ApplicationDirectories(string bundleRoot, string userApplicationsDir,
            string systemApplicationsDir = "/Applications") {
        var source = Path.GetFullPath(bundleRoot.TrimEnd('/'));
        bool Existing(string directory) => BundleNames
            .Select(name => Path.Combine(directory, name)).Any(path => ReadVersion(path) is not null);
        var directories = new[] { systemApplicationsDir, userApplicationsDir };
        var existing = directories.Where(Existing).ToArray();
        bool Conflicts(string directory) {
            var target = Path.Combine(directory, "kcap.app");
            return Path.GetFullPath(target) != source && Exists(target) && ReadVersion(target) is null;
        }
        if (Conflicts(systemApplicationsDir) || existing.Any(Conflicts))
            return directories;
        var home = Path.GetDirectoryName(source);
        if (Path.GetFileName(source) == "kcap.app" && home is not null
                && existing.Contains(home) && ReadVersion(source) is not null
                && ReadVersion(Path.Combine(home, "Kurrent Capacitor.app")) is null)
            return [home];
        if (existing.Length > 0) return existing;
        return [Path.GetDirectoryName(source) == userApplicationsDir ? userApplicationsDir : systemApplicationsDir];
    }

    public ApplicationsInstallPlan Inspect(string bundleRoot) {
        var target = Path.Combine(applicationsDir, "kcap.app");
        var legacy = Path.Combine(applicationsDir, "Kurrent Capacitor.app");
        var source = ReadVersion(bundleRoot);
        if (source is null)
            return new(ApplicationsInstallAction.Blocked, target, Error: "This copy of kcap is incomplete or cannot be verified. Download it again.");
        if (Exists(target) && ReadVersion(target) is null)
            return new(ApplicationsInstallAction.Blocked, target, source, Error: $"{target} already exists, but is not a verified kcap bundle. Choose another Applications folder or move it aside in Finder.");
        var existing = ReadVersion(legacy) is not null ? legacy : ReadVersion(target) is not null ? target : null;
        var installed = existing is null ? null : ReadVersion(existing);
        var payload = installed is not null && !PrereleaseSemver.IsNewer(source, installed) ? existing! : bundleRoot;
        var payloadVersion = ReadVersion(payload)!;
        // A dragged copy can be newer than both the disk image and the legacy install.
        if (ReadVersion(target) is { } canonical && PrereleaseSemver.IsNewer(canonical, payloadVersion)) {
            payload = target;
            payloadVersion = canonical;
        }
        var action = existing is null ? ApplicationsInstallAction.Install
            : existing != target || PrereleaseSemver.IsNewer(source, installed!)
                ? ApplicationsInstallAction.Update : ApplicationsInstallAction.OpenInstalled;
        return new(action, target, payloadVersion, installed, ExistingPath: existing, PayloadPath: payload);
    }

    static bool Exists(string path) => Directory.Exists(path) || File.Exists(path) || new FileInfo(path).LinkTarget is not null;

    public async Task<MoveOutcome> MoveAsync(string bundleRoot, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var plan = Inspect(bundleRoot);
        if (plan.Action == ApplicationsInstallAction.Blocked) return new(false, plan.Target, plan.Error);
        if (plan.Action == ApplicationsInstallAction.OpenInstalled) return new(true, plan.Target, null);
        var roots = new[] { plan.ExistingPath, plan.Target }.OfType<string>().Distinct().Where(Directory.Exists).ToArray();
        foreach (var root in roots) {
            if (isRunning?.Invoke(root) != true) continue;
            if (quitRunning is null || !await quitRunning(root, ct))
                return new(false, plan.Target, "Could not quit the installed copy of kcap. Quit it and try again.");
        }
        var staging = Path.Combine(applicationsDir, $"kcap.app.staging-{Guid.NewGuid():N}");
        var displaced = new List<(string Original, string Backup)>();
        IApplicationReferences? references = null;
        var promoted = false;
        var swapped = false;
        try {
            Directory.CreateDirectory(applicationsDir);
            var copy = await runner.RunAsync("ditto", [plan.PayloadPath!, staging], new RunOptions(Timeout: CopyTimeout), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (copy.TimedOut) return Fail(staging, "Copying took too long. Try again.");
            if (copy.ExitCode != 0) return Fail(staging, $"Copying failed: {copy.Stderr.Trim()}");
            if (ReadVersion(staging) != plan.SourceVersion) return Fail(staging, "The copy is incomplete.");
            if (Inspect(bundleRoot) != plan) return Fail(staging, "The installed copy changed while copying. Try again.");
            if (roots.Any(root => isRunning?.Invoke(root) == true)) return Fail(staging, "An installed copy opened while copying. Quit it and try again.");

            references = migrateReferences?.Invoke();
            var retiredRoots = new[] { plan.ExistingPath, bundleRoot }
                .OfType<string>().Where(root => root != plan.Target && ReadVersion(root) is not null
                    && (root == plan.ExistingPath || Path.GetDirectoryName(root) is "/Applications" || Path.GetDirectoryName(root) == userApplicationsDir))
                .Distinct().ToArray();
            if (references is not null) await references.PrepareAsync([.. retiredRoots.Concat(roots).Distinct()], plan.Target, ct).ConfigureAwait(false);
            if (Exists(plan.Target)) {
                if (swap?.Invoke(staging, plan.Target) != true) throw new IOException("The update could not be installed. The existing copy has not been changed.");
                swapped = true;
            } else {
                if (!promote(staging, plan.Target)) throw new IOException("kcap.app appeared while copying. Open that copy instead.");
                promoted = true;
            }
            if (references is not null) await references.ApplyAsync(ct).ConfigureAwait(false);
            foreach (var root in retiredRoots) {
                var backup = root + ".migrating-" + Guid.NewGuid().ToString("N");
                Directory.Move(root, backup);
                displaced.Add((root, backup));
            }
            if (activate is not null && !await activate(plan.Target, ct)) {
                if (isRunning?.Invoke(plan.Target) == true && (quitRunning is null || !await quitRunning(plan.Target, CancellationToken.None)))
                    return new(false, plan.Target, "The replacement app could not be closed. The new installation and recovery backups have been retained; open the installed kcap app before retrying.");
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
            try {
                if (references is not null) await references.RollbackAsync(RestoreBundles).ConfigureAwait(false);
                else RestoreBundles();
            } catch (Exception restore) { errors.Add(restore.Message); }
            if (errors.Count == 1) RemoveStaging(staging);
            if (ex is OperationCanceledException && errors.Count == 1) throw;
            return new(false, plan.Target, string.Join("\n\n", errors));
        } finally {
            if (references is not null) await references.DisposeAsync().ConfigureAwait(false);
        }
    }

    static string? ReadVersion(string root) {
        try {
            if (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null ||
                (!File.Exists(Path.Combine(root, "Contents", "MacOS", "kcap-desktop")) &&
                 !File.Exists(Path.Combine(root, "Contents", "MacOS", "Kurrent Capacitor")))) return null;
            using var reader = XmlReader.Create(Path.Combine(root, "Contents", "Info.plist"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var entries = XDocument.Load(reader).Root?.Element("dict")?.Elements().ToArray();
            if (entries is null) return null;
            string? Value(string key) {
                for (var i = 0; i + 1 < entries.Length; i += 2)
                    if (entries[i].Name == "key" && entries[i].Value == key && entries[i + 1].Name == "string")
                        return entries[i + 1].Value;
                return null;
            }
            return Value("CFBundleIdentifier") == "io.kurrent.capacitor"
                ? Value("CFBundleVersion") ?? Value("CFBundleShortVersionString") : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) {
            return null;
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
    public static bool PromoteExclusive(string from, string to) => renamex_np(from, to, RENAME_EXCL) == 0;

    [SupportedOSPlatform("macos")]
    public static bool SwapAtomic(string from, string to) => renamex_np(from, to, 0x2) == 0;

    const uint RENAME_EXCL = 0x4;

    [LibraryImport("libc", EntryPoint = "renamex_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int renamex_np(string from, string to, uint flags);
}
