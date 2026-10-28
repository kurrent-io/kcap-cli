using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Platform.Mac;

/// Removes this bundle's own traces: the `kcap` links into it, its support files under
/// ~/Library, and finally the bundle, which goes to the Trash so it stays restorable.
public sealed partial class MacAppRemoval : IAppRemoval {
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    // Refuses unless the path is still the link that was found, so the prompt can never remove
    // whatever replaced it in the meantime.
    const string AdminRemoveScript = "on run argv\nset p to quoted form of item 1 of argv\nset old to quoted form of item 2 of argv\ndo shell script \"test -L \" & p & \" && test \\\"$(/usr/bin/readlink \" & p & \")\\\" = \" & old & \" && /bin/rm \" & p with administrator privileges\nend run";

    readonly IProcessRunner        _runner;
    readonly UserHome              _home;
    readonly string                _bundleRoot;
    readonly IReadOnlyList<string> _linkFolders;
    readonly MacUpdaterFiles?      _updaterFiles;

    public MacAppRemoval(IProcessRunner runner, UserHome home, string bundleRoot)
        : this(runner, home, bundleRoot, KcapLinkFolders.For(home), MacUpdaterFiles.ForCurrentBundle(bundleRoot, home)) { }

    internal MacAppRemoval(IProcessRunner runner, UserHome home, string bundleRoot, IReadOnlyList<string> linkFolders,
            MacUpdaterFiles? updaterFiles = null) {
        _runner      = runner;
        _home        = home;
        _bundleRoot  = bundleRoot;
        _linkFolders = linkFolders;
        _updaterFiles = updaterFiles;
    }

    public async Task<IReadOnlyList<string>> RemoveLinksAsync(CancellationToken ct) {
        var remover = new KcapLinkRemover(_linkFolders, BundleBinary);
        await RemoveAsAdminAsync(remover.Remove(), ct).ConfigureAwait(false);
        return [.. remover.Find(), .. RemoveProfileLines()];
    }

    IEnumerable<string> RemoveProfileLines() {
        var left = new List<string>();
        foreach (var profile in ShellProfile.Removable(_home, [])) {
            try {
                ShellProfile.Remove(profile);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Console.Error.WriteLine($"kcap: could not remove the PATH line from {profile}: {ex.Message}");
                left.Add(profile);
            }
        }
        return left;
    }

    internal async Task RemoveAsAdminAsync(IReadOnlyList<KcapLinkFailure> failures, CancellationToken ct) {
        var bundleBinary = ResolveFully(BundleBinary);

        foreach (var failure in failures) {
            // Re-checked here because a link repointed since the scan (brew link, a reinstalled CLI)
            // is no longer ours, and the script only guards against a change after this read.
            if (new FileInfo(failure.Path).LinkTarget is not { } expected
             || bundleBinary is null || ResolveFully(failure.Path) != bundleBinary) continue;

            try {
                var result = await _runner.RunAsync("/usr/bin/osascript", ["-e", AdminRemoveScript, "--", failure.Path, expected],
                    new RunOptions(Timeout: TimeSpan.FromMinutes(2)), ct).ConfigureAwait(false);
                if (result.ExitCode != 0 || result.TimedOut)
                    Console.Error.WriteLine($"kcap: could not remove {failure.Path}: {failure.Reason}");
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                Console.Error.WriteLine($"kcap: could not remove {failure.Path}: {ex.Message}");
            }
        }
    }

    string BundleBinary => Path.Combine(_bundleRoot, "Contents", "MacOS", "kcap");

    static string? ResolveFully(string path) {
        try {
            var info = new FileInfo(path);
            return info.LinkTarget is null ? info.FullName : info.ResolveLinkTarget(true)?.FullName;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    public bool DeleteSupportFiles() {
        if (ReadBundleId(_bundleRoot) is not { } id) {
            Console.Error.WriteLine($"kcap: no bundle identifier in {_bundleRoot}; support files were left in place");
            return false;
        }

        var removed = true;

        var library = Path.Combine(_home.Path, "Library");
        foreach (var dir in new[] {
                     Path.Combine(library, "Caches", id),
                     Path.Combine(library, "HTTPStorages", id),
                     Path.Combine(library, "Saved Application State", id + ".savedState"),
                 }.Concat(_updaterFiles?.Directories ?? [])) {
            try {
                Directory.Delete(dir, recursive: true);
            } catch (DirectoryNotFoundException) {
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Console.Error.WriteLine($"kcap: could not remove {dir}: {ex.Message}");
                removed = false;
            }
        }

        foreach (var path in new[] { Path.Combine(library, "Preferences", id + ".plist") }.Concat(_updaterFiles?.Logs ?? [])) {
            try {
                File.Delete(path);
            } catch (DirectoryNotFoundException) {
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Console.Error.WriteLine($"kcap: could not remove {path}: {ex.Message}");
                removed = false;
            }
        }

        return removed;
    }

    public Task<bool> TrashBundleAsync(CancellationToken ct) {
        if (!OperatingSystem.IsMacOS()) return Task.FromResult(false);

        try {
            var manager = Send(GetClass("NSFileManager"), Selector("defaultManager"));
            var path    = Send(GetClass("NSString"), Selector("stringWithUTF8String:"), _bundleRoot);
            var url     = Send(GetClass("NSURL"), Selector("fileURLWithPath:"), path);
            if (manager == 0 || url == 0) return Task.FromResult(false);

            return Task.FromResult(SendBool(manager, Selector("trashItemAtURL:resultingItemURL:error:"), url, 0, 0));
        } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) {
            Console.Error.WriteLine($"kcap: could not move {_bundleRoot} to the Trash: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    public void RevealBundle() => _ = RevealAsync();

    async Task RevealAsync() {
        try {
            await _runner.RunAsync("/usr/bin/open", ["-R", _bundleRoot], new RunOptions(Timeout: TimeSpan.FromSeconds(10)),
                CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not reveal {_bundleRoot}: {ex.Message}");
        }
    }

    static string? ReadBundleId(string root) {
        try {
            using var reader = XmlReader.Create(Path.Combine(root, "Contents", "Info.plist"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var entries = XDocument.Load(reader).Root?.Element("dict")?.Elements().ToArray() ?? [];
            for (var i = 0; i + 1 < entries.Length; i++)
                if (entries[i].Name == "key" && entries[i].Value == "CFBundleIdentifier" && entries[i + 1].Name == "string")
                    return IsReverseDns(entries[i + 1].Value) ? entries[i + 1].Value : null;
            return null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) {
            return null;
        }
    }

    // The id names paths that are deleted recursively, so `..` or a separator must never get through.
    internal static bool IsReverseDns(string id) =>
        id.Split('.') is { Length: > 1 } segments
        && segments.All(segment => segment.Length > 0 && segment.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Send(nint receiver, nint selector, string argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool SendBool(nint receiver, nint selector, nint url, nint resultingUrl, nint error);
}
