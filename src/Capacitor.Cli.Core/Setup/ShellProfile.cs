using System.Text;

namespace Capacitor.Cli.Core.Setup;

/// Puts `~/.local/bin` on a new terminal's PATH through the login shell's profile, for a user who
/// can neither write a folder already on PATH nor authorize an admin link. The line kcap adds sits
/// between markers, so a re-run finds it and uninstall removes exactly it; the rest of the file is
/// never rewritten.
public sealed class ShellProfile(UserHome home, string? shell) {
    internal const string Begin = "# >>> kcap PATH >>>";
    // Opens a block that kcap had to end the file's last line for, so removal takes that newline too.
    internal const string BeginAfterNewline = "# >>> kcap PATH (newline above added by kcap) >>>";
    internal const string End   = "# <<< kcap PATH <<<";

    // Latin-1 maps every byte to one char and back, so searching and cutting the file's bytes as
    // Latin-1 text keeps whatever encoding (and BOM) the rest of it is in, byte for byte.
    static readonly Encoding Bytes = Encoding.Latin1;

    /// The folder the profile line adds; its `kcap` link is what the line exists for.
    public static string Folder(UserHome home) => Path.Combine(home.Path, ".local", "bin");

    /// The file a new terminal's login shell reads, or null for a shell kcap does not edit.
    public string? ProfilePath => ShellName() switch {
        "zsh"  => Path.Combine(home.Path, ".zprofile"),
        // bash reads only the first of these that exists, so a new .bash_profile would hide a .profile.
        "bash" => BashProfiles(home).FirstOrDefault(File.Exists) ?? BashProfiles(home)[0],
        _      => null,
    };

    /// The profile line as it will read, shown before it is written; null where nothing is edited.
    public string? Disclosure => ProfilePath is { } profile && Line(Folder(home)) is { } line
        ? $"{line} (in {Tilde(profile)})"
        : null;

    /// Appends the marked line unless the profile already carries it. False when this shell has no
    /// profile kcap edits, or the folder cannot be written into one.
    public bool Add() {
        if (ProfilePath is not { } profile || Line(Folder(home)) is not { } line) return false;

        using var stream = new FileStream(profile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
        var text = Read(stream);
        var begin = IndexOfBlock(text);
        var marker = begin >= 0 && IndexOfLine(text, BeginAfterNewline, begin) == begin ? BeginAfterNewline : Begin;
        var expected = AsBytes($"{marker}\n{line}\n{End}\n");
        var position = text.Length;
        string append;
        if (begin >= 0) {
            if (Without(text, AsBytes(line)) is not null) return true;
            // Only a byte-for-byte prefix at EOF can be an interrupted append. User additions are
            // never interpreted as ours, even when they sit between our markers.
            var partial = text[begin..];
            if (!expected.StartsWith(partial, StringComparison.Ordinal)) throw Modified(profile);
            append = expected[partial.Length..];
        } else {
            var block = text.Length > 0 && !text.EndsWith('\n') ? $"\n{BeginAfterNewline}" : Begin;
            append = AsBytes($"{block}\n{line}\n{End}\n");
        }
        VerifyUnchanged(stream, text);
        try {
            stream.Position = position;
            stream.Write(Bytes.GetBytes(append));
            stream.Flush();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A short append is safe to roll back only while every byte still matches our write.
            var current = Read(stream);
            if (current.StartsWith(text, StringComparison.Ordinal)
                && append.StartsWith(current[text.Length..], StringComparison.Ordinal)) stream.SetLength(position);
            throw;
        }
        return true;
    }

    /// Profiles carrying kcap's line that uninstall may take it out of: none while a `kcap` entry
    /// stays in the folder (another install's link still needs the line), unless that entry is one
    /// of <paramref name="leaving"/>.
    public static IReadOnlyList<string> Removable(UserHome home, IReadOnlyCollection<string> leaving) {
        var link = Path.Combine(Folder(home), "kcap");
        var info = new FileInfo(link);
        if ((info.Exists || info.LinkTarget is not null) && !leaving.Contains(link)) return [];

        return Candidates(home).Where(Carries).ToArray();
    }

    /// Removes every kcap block from the file, leaving every other byte as it was.
    public static void Remove(string profile) {
        using var stream = new FileStream(profile, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
        var text = Read(stream);
        var next = text;
        var home = new UserHome(Path.GetDirectoryName(Path.GetFullPath(profile))!);
        var line = Line(Folder(home));
        if (line is null && IndexOfBlock(text) >= 0) throw Modified(profile);
        while (line is not null && Without(next, AsBytes(line)) is { } shorter) next = shorter;
        if (IndexOfBlock(next) >= 0) throw Modified(profile);
        if (next == text) return;
        VerifyUnchanged(stream, text);
        stream.Position = 0;
        stream.Write(Bytes.GetBytes(next));
        stream.SetLength(next.Length);
        stream.Flush();
    }

    internal static string? Without(string text, string line) {
        var begin = IndexOfBlock(text);
        if (begin < 0) return null;
        var marker = IndexOfLine(text, BeginAfterNewline, begin) == begin ? BeginAfterNewline : Begin;
        var block = $"{marker}\n{line}\n{End}\n";
        if (!text.AsSpan(begin).StartsWith(block, StringComparison.Ordinal)) return null;
        var rest = text[(begin + block.Length)..];
        if (rest.Length == 0 && begin > 0 && marker == BeginAfterNewline) begin--;
        return text[..begin] + rest;
    }

    static IOException Modified(string profile) => new($"{profile} contains an incomplete or modified kcap PATH block; review it manually before retrying.");

    static string AsBytes(string text) => Bytes.GetString(Encoding.UTF8.GetBytes(text));

    static string Read(Stream stream) {
        stream.Position = 0;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Bytes.GetString(buffer.ToArray());
    }

    static void VerifyUnchanged(Stream stream, string original) {
        if (Read(stream) != original)
            throw new IOException("The shell profile changed during PATH cleanup. Retry with other profile editors closed.");
    }

    // The path goes inside double quotes, so a character the shell expands there is refused.
    internal static string? Line(string folder) =>
        folder.IndexOfAny(['"', '$', '`', '!', '\\', '\n', '\r']) >= 0 ? null : $"export PATH=\"$PATH:{folder}\"";

    // File.ReadAllText would honour a BOM and decode the file, and the rewrite would then lose it.
    static string Read(string profile) => Bytes.GetString(File.ReadAllBytes(profile));

    static int IndexOfBlock(string text) {
        var plain  = IndexOfLine(text, Begin, 0);
        var joined = IndexOfLine(text, BeginAfterNewline, 0);
        return plain < 0 ? joined : joined < 0 ? plain : Math.Min(plain, joined);
    }

    static int IndexOfLine(string text, string line, int from) {
        for (var i = text.IndexOf(line, from, StringComparison.Ordinal); i >= 0; i = text.IndexOf(line, i + 1, StringComparison.Ordinal)) {
            var startsLine = i == 0 || text[i - 1] == '\n';
            var after      = i + line.Length;
            var endsLine   = after == text.Length || text[after] is '\n' or '\r';
            if (startsLine && endsLine) return i;
        }
        return -1;
    }

    static bool Carries(string profile) {
        try {
            return IndexOfBlock(Read(profile)) >= 0;
        } catch (FileNotFoundException) {
            return false;
        } catch (DirectoryNotFoundException) {
            return false;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Include an uninspectable candidate so Remove retries and its caller reports incomplete
            // cleanup, rather than treating a read failure as proof that no PATH block remains.
            return true;
        }
    }

    static string[] BashProfiles(UserHome home) =>
        [Path.Combine(home.Path, ".bash_profile"), Path.Combine(home.Path, ".bash_login"), Path.Combine(home.Path, ".profile")];

    static IEnumerable<string> Candidates(UserHome home) => [Path.Combine(home.Path, ".zprofile"), .. BashProfiles(home)];

    // An unset shell is the macOS default.
    string ShellName() => string.IsNullOrEmpty(shell) ? "zsh" : Path.GetFileName(shell);

    string Tilde(string path) => Tilde(home, path);

    internal static string Tilde(UserHome home, string path) =>
        path.StartsWith(home.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + path[home.Path.Length..] : path;
}
