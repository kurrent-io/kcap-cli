using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

public sealed record MoveOutcome(bool Moved, string? InstalledPath, string? Error);

public sealed partial class ApplicationsMover(
        IProcessRunner runner, Func<string, string, bool> promote, string applicationsDir = "/Applications",
        Func<string, string, bool>? swap = null, Func<string, bool>? isRunning = null) {
    static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(2);

    public ApplicationsInstallPlan Inspect(string bundleRoot) {
        var name = Path.GetFileName(bundleRoot.TrimEnd('/'));
        var target = Path.Combine(applicationsDir, name);
        var source = ReadVersion(bundleRoot);
        if (source is null)
            return new(ApplicationsInstallAction.Blocked, target, Error: "This copy of Capacitor is incomplete or cannot be verified. Download it again.");
        if (!Directory.Exists(target) && !File.Exists(target))
            return new(ApplicationsInstallAction.Install, target, source);
        var installed = ReadVersion(target);
        if (installed is null)
            return new(ApplicationsInstallAction.Blocked, target, source, Error: $"{name} already exists in {applicationsDir}, but is not a verified Capacitor bundle. Move it aside in Finder and try again.");
        return new(PrereleaseSemver.IsNewer(source, installed)
            ? ApplicationsInstallAction.Update : ApplicationsInstallAction.OpenInstalled, target, source, installed);
    }

    public async Task<MoveOutcome> MoveAsync(string bundleRoot, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var plan = Inspect(bundleRoot);
        if (plan.Action == ApplicationsInstallAction.Blocked) return new(false, plan.Target, plan.Error);
        if (plan.Action == ApplicationsInstallAction.OpenInstalled) return new(true, plan.Target, null);
        if (plan.Action == ApplicationsInstallAction.Update && isRunning?.Invoke(plan.Target) == true)
            return new(false, plan.Target, "Quit the installed copy of Capacitor, then try updating again.");

        var name = Path.GetFileName(plan.Target);
        var staging = Path.Combine(applicationsDir, $"{name}.staging-{Guid.NewGuid():N}");
        try {
            var copy = await runner.RunAsync("ditto", [bundleRoot, staging], new RunOptions(Timeout: CopyTimeout), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (copy.TimedOut) return Fail(staging, "Copying took too long. Try again.");
            if (copy.ExitCode != 0) return Fail(staging, $"Copying failed: {copy.Stderr.Trim()}");

            if (ReadVersion(staging) != plan.SourceVersion)
                return Fail(staging, "The copy is incomplete.");

            if (Inspect(bundleRoot) != plan)
                return Fail(staging, "The installed copy changed while copying. Try again.");
            if (plan.Action == ApplicationsInstallAction.Update) {
                if (isRunning?.Invoke(plan.Target) == true)
                    return Fail(staging, "Quit the installed copy of Capacitor, then try updating again.");
                if (swap?.Invoke(staging, plan.Target) != true)
                    return Fail(staging, "The update could not be installed. The existing copy has not been changed.");
                // After the atomic swap, staging contains only the previous bundle.
                RemoveStaging(staging);
            } else if (!promote(staging, plan.Target))
                return Fail(staging, $"{name} appeared in {applicationsDir} while copying. Open that copy instead.");

            return new MoveOutcome(true, plan.Target, null);
        } catch (OperationCanceledException) {
            RemoveStaging(staging);
            throw;
        } catch (Exception ex) {
            return Fail(staging, ex.Message);
        }
    }

    static string? ReadVersion(string root) {
        try {
            if (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null ||
                !File.Exists(Path.Combine(root, "Contents", "MacOS", "Kurrent Capacitor"))) return null;
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
