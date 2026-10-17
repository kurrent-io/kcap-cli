using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

/// <summary>Builds <c>.tar.gz</c> bundles for tests, naming entries the way bsdtar does (<c>./x</c>).</summary>
public static class TestBundles {
    /// <summary>Directories and files get mode 0755; a hard or symbolic link carries <paramref name="entries"/>' LinkName.</summary>
    public static void Write(string path, params (string Name, TarEntryType Type, string? Content, string? LinkName)[] entries) =>
        Write(path, entries.Select(e => (e.Name, e.Type, e.Content, e.LinkName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                                                                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                                                                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute)).ToArray());

    public static void Write(string path, params (string Name, TarEntryType Type, string? Content, string? LinkName, UnixFileMode Mode)[] entries) {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var tar  = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);

        foreach (var (name, type, content, linkName, mode) in entries) {
            var entry = new PaxTarEntry(type, name) { Mode = mode };

            if (linkName is not null) entry.LinkName = linkName;
            if (content is not null) entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));

            tar.WriteEntry(entry);
        }
    }

    public static (string Name, TarEntryType Type, string? Content, string? LinkName) Dir(string name) => (name, TarEntryType.Directory, null, null);

    public static (string Name, TarEntryType Type, string? Content, string? LinkName) RegularFile(string name, string content) =>
        (name, TarEntryType.RegularFile, content, null);

    public static (string Name, TarEntryType Type, string? Content, string? LinkName) Symlink(string name, string target) =>
        (name, TarEntryType.SymbolicLink, null, target);

    public static (string Name, TarEntryType Type, string? Content, string? LinkName) Hardlink(string name, string target) =>
        (name, TarEntryType.HardLink, null, target);
}
