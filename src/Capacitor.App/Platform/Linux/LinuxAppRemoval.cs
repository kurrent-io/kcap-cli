using System.Globalization;
using System.Runtime.InteropServices;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Platform.Linux;

/// Removes what an AppImage install leaves outside the image: the `kcap` links into the CLI it copied out of the
/// mount, that copy itself, and finally the image, which goes to the user's Trash so it stays restorable.
public sealed partial class LinuxAppRemoval : IAppRemoval {
    readonly IProcessRunner        _runner;
    readonly string                _image;
    readonly string                _stagedRoot;
    readonly IReadOnlyList<string> _linkFolders;
    readonly string                _trash;
    readonly TimeProvider          _time;
    readonly Func<string, string, int> _move;

    public LinuxAppRemoval(IProcessRunner runner, UserHome home, AppImageInstall install, Func<string, string?> getEnv, TimeProvider time)
        : this(runner, install.Image, AppImageInstall.StagedRoot(getEnv, home), KcapLinkFolders.For(home),
               Path.Combine(AppImageInstall.DataHome(getEnv, home), "Trash"), time) { }

    internal LinuxAppRemoval(IProcessRunner runner, string image, string stagedRoot, IReadOnlyList<string> linkFolders, string trash,
            TimeProvider time, Func<string, string, int>? move = null) {
        // libc is a Linux name; anywhere else the image stays put rather than the call failing to bind.
        _move        = move ?? (OperatingSystem.IsLinux() ? MoveWithoutReplacing : static (_, _) => NotSupported);
        _runner      = runner;
        _image       = image;
        _stagedRoot  = stagedRoot;
        _linkFolders = linkFolders;
        _trash       = trash;
        _time        = time;
    }

    public string FinishByHand => "Move the kcap AppImage to the Trash";

    /// A link in a folder only root can write stays: the CLI's own uninstall has already printed the command
    /// that removes it.
    public Task<IReadOnlyList<string>> RemoveLinksAsync(CancellationToken ct) {
        var remover = new KcapLinkRemover(_linkFolders, Path.Combine(AppImageInstall.StagedCliDirectory(_stagedRoot), "kcap"));
        foreach (var failure in remover.Remove()) Console.Error.WriteLine($"kcap: could not remove {failure.Path}: {failure.Reason}");

        return Task.FromResult(remover.Find());
    }

    public void DeleteSupportFiles() {
        try {
            if (Directory.Exists(_stagedRoot)) Directory.Delete(_stagedRoot, recursive: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"kcap: could not remove {_stagedRoot}: {ex.Message}");
        }
    }

    /// <summary>The freedesktop.org home Trash. A name is free only while the Trash holds neither its file nor its info;
    /// the info file is claimed first, so two removals never take one name, and the image is then linked in place, which
    /// never replaces a file already there. An image on another filesystem than the Trash stays where it is.</summary>
    public Task<bool> TrashBundleAsync(CancellationToken ct) {
        if (!File.Exists(_image)) return Task.FromResult(false);

        var files = Path.Combine(_trash, "files");
        var info  = Path.Combine(_trash, "info");
        string? claimed = null;

        try {
            OwnerOnlyDirectory(_trash);
            OwnerOnlyDirectory(files);
            OwnerOnlyDirectory(info);

            var name = Path.GetFileName(_image);
            for (var attempt = 1; claimed is null; attempt++) {
                var candidate = attempt == 1 ? name : $"{Path.GetFileNameWithoutExtension(name)}.{attempt}{Path.GetExtension(name)}";
                var infoPath  = Path.Combine(info, candidate + ".trashinfo");
                try {
                    using (var stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write))
                    using (var writer = new StreamWriter(stream))
                        writer.Write(TrashInfo(_image, _time.GetLocalNow()));
                } catch (IOException) when (File.Exists(infoPath)) {
                    continue;
                }

                if (File.Exists(Path.Combine(files, candidate)) || Directory.Exists(Path.Combine(files, candidate))) {
                    TryDelete(infoPath);
                    continue;
                }

                claimed = candidate;
            }

            if (_move(_image, Path.Combine(files, claimed)) is var errno and not 0) {
                TryDelete(Path.Combine(info, claimed + ".trashinfo"));
                Console.Error.WriteLine(errno == CrossDevice
                    ? $"kcap: {_image} is on another filesystem than the Trash; leaving it where it is"
                    : $"kcap: could not move {_image} to the Trash (errno {errno})");

                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            if (claimed is not null) TryDelete(Path.Combine(info, claimed + ".trashinfo"));
            Console.Error.WriteLine($"kcap: could not move {_image} to the Trash: {ex.Message}");

            return Task.FromResult(false);
        }
    }

    const int CrossDevice  = 18;
    const int NotSupported = 38;

    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Created owner-only, as the Trash spec asks; one that already exists keeps its mode.</summary>
    static void OwnerOnlyDirectory(string path) {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, OwnerOnly);
    }

    static void TryDelete(string path) {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>link(2) then unlink(2): unlike <see cref="File.Move(string,string)"/> and rename(2), a link never replaces
    /// what is at the destination, and never copies across filesystems, answering EXDEV instead.</summary>
    static int MoveWithoutReplacing(string from, string to) {
        if (LinkNative(from, to) != 0) return Marshal.GetLastPInvokeError();
        if (UnlinkNative(from) == 0) return 0;

        var errno = Marshal.GetLastPInvokeError();
        UnlinkNative(to);

        return errno;
    }

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LinkNative(string from, string to);

    [LibraryImport("libc", EntryPoint = "unlink", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnlinkNative(string path);

    internal static string TrashInfo(string path, DateTimeOffset deletedAt) =>
        "[Trash Info]\n"
      + $"Path={Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal)}\n"
      + $"DeletionDate={deletedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n";

    public void RevealBundle() => _ = RevealAsync();

    async Task RevealAsync() {
        var folder = Path.GetDirectoryName(_image) ?? _image;
        try {
            await _runner.RunAsync("xdg-open", [folder], new RunOptions(Timeout: TimeSpan.FromSeconds(10)), CancellationToken.None)
                .ConfigureAwait(false);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not open {folder}: {ex.Message}");
        }
    }
}
