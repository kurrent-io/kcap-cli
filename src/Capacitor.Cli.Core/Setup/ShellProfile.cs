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

        var text = File.Exists(profile) ? Read(profile) : "";
        if (IndexOfBlock(text) >= 0) return true;

        var block = text.Length > 0 && !text.EndsWith('\n') ? $"\n{BeginAfterNewline}" : Begin;
        using var stream = new FileStream(profile, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes($"{block}\n{line}\n{End}\n"));
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
        var text = Read(profile);
        var next = text;
        while (Without(next) is { } shorter) next = shorter;
        if (!ReferenceEquals(next, text)) File.WriteAllBytes(profile, Bytes.GetBytes(next));
    }

    internal static string? Without(string text) {
        var begin = IndexOfBlock(text);
        if (begin < 0) return null;
        // An unterminated block may have had the user's own lines typed into it; leave it alone.
        var end = IndexOfLine(text, End, begin);
        if (end < 0) return null;

        var after = text.IndexOf('\n', end);
        var rest  = after < 0 ? "" : text[(after + 1)..];
        // Once the user has added lines after the block, that newline ends their line too.
        if (rest.Length == 0 && begin > 0 && IndexOfLine(text, BeginAfterNewline, begin) == begin) begin--;
        return text[..begin] + rest;
    }

    // The path goes inside double quotes, so a character the shell expands there is refused.
    internal static string? Line(string folder) =>
        folder.IndexOfAny(['"', '$', '`', '\\', '\n', '\r']) >= 0 ? null : $"export PATH=\"$PATH:{folder}\"";

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
            return File.Exists(profile) && IndexOfBlock(Read(profile)) >= 0;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    static string[] BashProfiles(UserHome home) =>
        [Path.Combine(home.Path, ".bash_profile"), Path.Combine(home.Path, ".bash_login"), Path.Combine(home.Path, ".profile")];

    static IEnumerable<string> Candidates(UserHome home) => [Path.Combine(home.Path, ".zprofile"), .. BashProfiles(home)];

    // An unset shell is the macOS default.
    string ShellName() => string.IsNullOrEmpty(shell) ? "zsh" : Path.GetFileName(shell);

    string Tilde(string path) =>
        path.StartsWith(home.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + path[home.Path.Length..] : path;
}
