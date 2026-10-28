using System.Runtime.Versioning;
using Capacitor.App.Platform.Linux;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

/// <summary>What an AppImage install removes of itself once the CLI uninstall has run: the links into the CLI it
/// copied out of the mount, that copy, and the image, which goes to the freedesktop.org home Trash.</summary>
public class LinuxAppRemovalTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Runner : IProcessRunner {
        public List<(string File, string[] Args)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls.Add((fileName, args));
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
                Action<StreamedLine> onLine, CancellationToken ct) => Task.FromResult(new StreamingResult(0, false, []));
    }

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 9, 30, 15, TimeSpan.Zero));

    string Staged => Tmp.PathTo("data", "kcap", "desktop");

    /// The trash rules on every platform; the libc move itself only where it exists, in its own test.
    LinuxAppRemoval Removal(Runner? runner = null, string? image = null, Func<string, string, int>? move = null, bool libcMove = false,
            Func<string, Stream>? createInfo = null) =>
        new(runner ?? new Runner(), image ?? Tmp.PathTo("apps", "kcap-x86_64.AppImage"), Staged,
            [Tmp.PathTo("links-a"), Tmp.PathTo("links-b")], Tmp.PathTo("data", "Trash"), _time,
            libcMove ? null : move ?? MoveWithoutCopying, createInfo);

    /// A stream whose every write fails, as one on a full disk does.
    sealed class FullDisk : Stream {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("No space left on device");
    }

    /// A move within one filesystem that never replaces what is there, as link(2) and unlink(2) do, without libc.
    static int MoveWithoutCopying(string from, string to) {
        if (File.Exists(to)) return 17;
        File.Move(from, to);
        return 0;
    }

    const int CrossDevice = 18;

    [Test, ExcludeOn(OS.Windows)]
    public async Task Only_links_into_the_staged_cli_are_removed() {
        var stagedCli = Tmp.CreateFile("data/kcap/desktop/bin/kcap", "cli");
        var other     = Tmp.CreateFile("other/kcap", "npm cli");
        Tmp.CreateDir("links-a");
        Tmp.CreateDir("links-b");
        File.CreateSymbolicLink(Tmp.PathTo("links-a", "kcap"), stagedCli);
        File.CreateSymbolicLink(Tmp.PathTo("links-b", "kcap"), other);

        var left = await Removal().RemoveLinksAsync(CancellationToken.None);

        await Assert.That(left).IsEmpty();
        await Assert.That(File.Exists(Tmp.PathTo("links-a", "kcap")) || new FileInfo(Tmp.PathTo("links-a", "kcap")).LinkTarget is not null).IsFalse();
        await Assert.That(new FileInfo(Tmp.PathTo("links-b", "kcap")).LinkTarget).IsEqualTo(other);
    }

    [Test]
    public async Task The_staged_cli_and_plugin_are_deleted_and_the_rest_of_the_data_directory_is_kept() {
        Tmp.CreateFile("data/kcap/desktop/bin/kcap", "cli");
        Tmp.CreateFile("data/kcap/desktop/kcap/plugin.json", "{}");
        Tmp.CreateFile("data/kcap/local/registry.json", "{}");

        await Assert.That(Removal().DeleteSupportFiles()).IsTrue();

        await Assert.That(Directory.Exists(Staged)).IsFalse();
        await Assert.That(File.Exists(Tmp.PathTo("data", "kcap", "local", "registry.json"))).IsTrue();
    }

    /// An unreadable parent makes `Directory.Exists` read the staged CLI as absent, so only the delete can tell.
    /// A runner that bypasses permissions (root, CAP_DAC_OVERRIDE) can delete it anyway, so the test skips there.
    [Test, ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task A_staged_cli_behind_an_unreadable_folder_is_reported_as_left() {
        Tmp.CreateFile("data/kcap/desktop/bin/kcap", "cli");
        var parent = Tmp.PathTo("data", "kcap");
        var mode = File.GetUnixFileMode(parent);
        try {
            File.SetUnixFileMode(parent, UnixFileMode.None);
            Skip.When(Directory.Exists(Staged), "this runner bypasses folder permissions");

            await Assert.That(Removal().DeleteSupportFiles()).IsFalse();
        } finally {
            File.SetUnixFileMode(parent, mode);
        }
        await Assert.That(File.Exists(Tmp.PathTo("data", "kcap", "desktop", "bin", "kcap"))).IsTrue();
    }

    [Test]
    public async Task A_missing_staged_cli_counts_as_removed() =>
        await Assert.That(Removal().DeleteSupportFiles()).IsTrue();

    [Test]
    public async Task The_image_goes_to_the_home_trash_with_its_info_and_a_second_one_takes_a_free_name() {
        var first = Tmp.CreateFile("apps/kcap-x86_64.AppImage", "one");

        await Assert.That(await Removal(image: first).TrashBundleAsync(CancellationToken.None)).IsTrue();
        Tmp.CreateFile("apps/kcap-x86_64.AppImage", "two");
        await Assert.That(await Removal(image: first).TrashBundleAsync(CancellationToken.None)).IsTrue();

        await Assert.That(File.Exists(first)).IsFalse();
        await Assert.That(File.ReadAllText(Tmp.PathTo("data", "Trash", "files", "kcap-x86_64.AppImage"))).IsEqualTo("one");
        await Assert.That(File.ReadAllText(Tmp.PathTo("data", "Trash", "files", "kcap-x86_64.2.AppImage"))).IsEqualTo("two");
        await Assert.That(File.ReadAllText(Tmp.PathTo("data", "Trash", "info", "kcap-x86_64.AppImage.trashinfo")))
            .IsEqualTo(LinuxAppRemoval.TrashInfo(first, _time.GetLocalNow()));
        await Assert.That(File.Exists(Tmp.PathTo("data", "Trash", "info", "kcap-x86_64.2.AppImage.trashinfo"))).IsTrue();
    }

    /// <summary>An image on another filesystem than the Trash cannot be moved into it. It stays where it is, with
    /// nothing copied into the Trash and no info file left to name a file that is not there.</summary>
    [Test]
    public async Task An_image_on_another_filesystem_stays_where_it_is() {
        var image = Tmp.CreateFile("apps/kcap-x86_64.AppImage", "image");

        var trashed = await Removal(image: image, move: (_, _) => CrossDevice).TrashBundleAsync(CancellationToken.None);

        await Assert.That(trashed).IsFalse();
        await Assert.That(File.ReadAllText(image)).IsEqualTo("image");
        await Assert.That(Directory.EnumerateFileSystemEntries(Tmp.PathTo("data", "Trash", "files"))).IsEmpty();
        await Assert.That(Directory.EnumerateFileSystemEntries(Tmp.PathTo("data", "Trash", "info"))).IsEmpty();
    }

    /// <summary>A name is free only when the Trash holds neither its file nor its info. A file another tool left
    /// with no info entry keeps its place, and the image takes the next name.</summary>
    [Test]
    public async Task A_file_already_in_the_trash_without_its_info_keeps_its_name() {
        var image = Tmp.CreateFile("apps/kcap-x86_64.AppImage", "image");
        Tmp.CreateFile("data/Trash/files/kcap-x86_64.AppImage", "someone else's");

        await Assert.That(await Removal(image: image).TrashBundleAsync(CancellationToken.None)).IsTrue();

        await Assert.That(File.ReadAllText(Tmp.PathTo("data", "Trash", "files", "kcap-x86_64.AppImage"))).IsEqualTo("someone else's");
        await Assert.That(File.ReadAllText(Tmp.PathTo("data", "Trash", "files", "kcap-x86_64.2.AppImage"))).IsEqualTo("image");
        await Assert.That(File.Exists(Tmp.PathTo("data", "Trash", "info", "kcap-x86_64.AppImage.trashinfo"))).IsFalse();
        await Assert.That(File.Exists(Tmp.PathTo("data", "Trash", "info", "kcap-x86_64.2.AppImage.trashinfo"))).IsTrue();
    }

    /// <summary>An info file that cannot be written is no name collision: the removal stops at once, leaves no
    /// partial info file, and leaves the image where it is. The cap on claims only bounds a loop that retries</summary>
    [Test]
    public async Task An_info_file_that_cannot_be_written_stops_the_removal_and_is_not_left_behind() {
        var image  = Tmp.CreateFile("apps/kcap-x86_64.AppImage", "image");
        var claims = 0;

        var trashed = await Removal(image: image, createInfo: path => {
            if (++claims > 20) throw new UnauthorizedAccessException("too many claims");
            File.Create(path).Dispose();
            return new FullDisk();
        }).TrashBundleAsync(CancellationToken.None);

        await Assert.That(trashed).IsFalse();
        await Assert.That(claims).IsEqualTo(1);
        await Assert.That(Directory.EnumerateFileSystemEntries(Tmp.PathTo("data", "Trash", "info"))).IsEmpty();
        await Assert.That(File.ReadAllText(image)).IsEqualTo("image");
    }

    /// <summary>A Trash the removal creates is the user's alone, as the freedesktop.org spec asks.</summary>
    [Test, ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task A_trash_the_removal_creates_is_owner_only() {
        var image = Tmp.CreateFile("apps/kcap-x86_64.AppImage", "image");

        await Removal(image: image).TrashBundleAsync(CancellationToken.None);

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        await Assert.That(File.GetUnixFileMode(Tmp.PathTo("data", "Trash"))).IsEqualTo(ownerOnly);
        await Assert.That(File.GetUnixFileMode(Tmp.PathTo("data", "Trash", "files"))).IsEqualTo(ownerOnly);
        await Assert.That(File.GetUnixFileMode(Tmp.PathTo("data", "Trash", "info"))).IsEqualTo(ownerOnly);
    }

    /// <summary>The production move puts the image in the Trash on the platform it is built for.</summary>
    [Test, RunOn(OS.Linux)]
    public async Task On_linux_the_image_is_moved_into_the_trash() {
        var image = Tmp.CreateFile("apps/kcap-x86_64.AppImage", "image");

        await Assert.That(await Removal(image: image, libcMove: true).TrashBundleAsync(CancellationToken.None)).IsTrue();

        await Assert.That(File.Exists(image)).IsFalse();
        await Assert.That(File.ReadAllText(Tmp.PathTo("data", "Trash", "files", "kcap-x86_64.AppImage"))).IsEqualTo("image");
    }

    /// <summary>Off Linux there is no libc to bind, so the image stays where it is instead of the call throwing.</summary>
    [Test, ExcludeOn(OS.Linux)]
    public async Task Off_linux_the_image_stays_where_it_is() {
        var image = Tmp.CreateFile("apps/kcap-x86_64.AppImage", "image");

        await Assert.That(await Removal(image: image, libcMove: true).TrashBundleAsync(CancellationToken.None)).IsFalse();

        await Assert.That(File.ReadAllText(image)).IsEqualTo("image");
        await Assert.That(Directory.EnumerateFileSystemEntries(Tmp.PathTo("data", "Trash", "info"))).IsEmpty();
    }

    [Test]
    public async Task An_image_that_is_gone_is_not_reported_trashed() {
        await Assert.That(await Removal(image: Tmp.PathTo("apps", "gone.AppImage")).TrashBundleAsync(CancellationToken.None)).IsFalse();
        await Assert.That(Directory.Exists(Tmp.PathTo("data", "Trash"))).IsFalse();
    }

    [Test]
    public async Task A_path_with_reserved_characters_is_escaped_in_the_trash_info() {
        var info = LinuxAppRemoval.TrashInfo("/home/me/My Apps/kcap%.AppImage", _time.GetLocalNow());

        await Assert.That(info).Contains("\nPath=/home/me/My%20Apps/kcap%25.AppImage\n");
    }

    [Test]
    public async Task Revealing_opens_the_folder_holding_the_image() {
        var runner = new Runner();

        Removal(runner).RevealBundle();

        var call = runner.Calls.Single();
        await Assert.That(call.File).IsEqualTo("xdg-open");
        await Assert.That(call.Args).IsEquivalentTo([Tmp.PathTo("apps")]);
    }
}
