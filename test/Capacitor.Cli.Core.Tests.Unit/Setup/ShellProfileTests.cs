using Capacitor.Cli.Core.Setup;
using TUnit.Assertions.Enums;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

// The installer edits POSIX login-shell profiles; Windows uses its user PATH instead.
[ExcludeOn(OS.Windows)]
public class ShellProfileTests {
    [TempDir] public required TempDir Tmp { get; init; }

    UserHome Home => new(Tmp.Path);

    string Block => $"{ShellProfile.Begin}\nexport PATH=\"$PATH:{ShellProfile.Folder(Home)}\"\n{ShellProfile.End}\n";

    [Test]
    public async Task Add_appends_the_marked_line_once_and_keeps_the_rest() {
        var profile = Tmp.CreateFile(".zprofile", "eval \"$(/opt/homebrew/bin/brew shellenv)\"\n");

        await Assert.That(new ShellProfile(Home, "/bin/zsh").Add()).IsTrue();
        await Assert.That(new ShellProfile(Home, "/bin/zsh").Add()).IsTrue();

        await Assert.That(File.ReadAllText(profile)).IsEqualTo("eval \"$(/opt/homebrew/bin/brew shellenv)\"\n" + Block);
    }

    /// The newline that ends the user's last line is kcap's too, so uninstall takes it back.
    [Test]
    public async Task A_newline_added_to_end_the_last_line_is_removed_with_the_block() {
        var profile = Tmp.CreateFile(".zprofile", "export A=1");

        new ShellProfile(Home, "/bin/zsh").Add();
        await Assert.That(File.ReadAllText(profile)).StartsWith($"export A=1\n{ShellProfile.BeginAfterNewline}\n");
        await Assert.That(new ShellProfile(Home, "/bin/zsh").Add()).IsTrue();

        ShellProfile.Remove(profile);

        await Assert.That(File.ReadAllText(profile)).IsEqualTo("export A=1");
    }

    [Test]
    public async Task The_added_newline_stays_once_the_user_has_written_after_the_block() {
        var profile = Tmp.CreateFile(".zprofile", "export A=1");
        new ShellProfile(Home, "/bin/zsh").Add();
        File.AppendAllText(profile, "export B=2\n");

        ShellProfile.Remove(profile);

        await Assert.That(File.ReadAllText(profile)).IsEqualTo("export A=1\nexport B=2\n");
    }

    [Test]
    public async Task Add_creates_a_missing_zprofile() {
        new ShellProfile(Home, null).Add();

        await Assert.That(File.ReadAllText(Tmp.PathTo(".zprofile"))).IsEqualTo(Block);
    }

    /// bash reads only the first login profile that exists, so creating .bash_profile would hide .profile.
    [Test]
    public async Task Bash_edits_the_profile_it_already_reads() {
        var profile = Tmp.CreateFile(".profile", "");

        new ShellProfile(Home, "/bin/bash").Add();

        await Assert.That(File.ReadAllText(profile)).IsEqualTo(Block);
        await Assert.That(File.Exists(Tmp.PathTo(".bash_profile"))).IsFalse();
    }

    [Test]
    public async Task A_shell_it_does_not_know_is_left_alone() {
        var shell = new ShellProfile(Home, "/opt/homebrew/bin/fish");

        await Assert.That(shell.Add()).IsFalse();
        await Assert.That(shell.Disclosure).IsNull();
        await Assert.That(Directory.EnumerateFileSystemEntries(Tmp.Path)).IsEmpty();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Add_writes_through_a_linked_profile() {
        var real = Tmp.CreateFile("dotfiles-zprofile", "export A=1\n");
        File.CreateSymbolicLink(Tmp.PathTo(".zprofile"), real);

        new ShellProfile(Home, "/bin/zsh").Add();

        await Assert.That(new FileInfo(Tmp.PathTo(".zprofile")).LinkTarget).IsEqualTo(real);
        await Assert.That(File.ReadAllText(real)).IsEqualTo("export A=1\n" + Block);
    }

    [Test]
    public async Task Remove_takes_out_exactly_the_block() {
        var profile = Tmp.CreateFile(".zprofile", "export A=1\n");
        new ShellProfile(Home, "/bin/zsh").Add();
        File.AppendAllText(profile, "export B=2\n");

        ShellProfile.Remove(profile);

        await Assert.That(File.ReadAllText(profile)).IsEqualTo("export A=1\nexport B=2\n");
    }

    [Test]
    [Arguments(new byte[] { (byte)'#', 0xE9, (byte)'\n' })]
    [Arguments(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'#', 0xC3, 0xA9, (byte)'\n' })]
    public async Task Remove_restores_the_original_bytes(byte[] original) {
        var profile = Tmp.PathTo(".zprofile");
        File.WriteAllBytes(profile, original);
        new ShellProfile(Home, "/bin/zsh").Add();

        ShellProfile.Remove(profile);

        await Assert.That(File.ReadAllBytes(profile)).IsEquivalentTo(original, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_non_ascii_home_is_written_as_utf8() {
        var home = new UserHome(Tmp.CreateDir("Zoë"));

        new ShellProfile(home, "/bin/zsh").Add();

        await Assert.That(File.ReadAllText(Path.Combine(home.Path, ".zprofile"))).Contains(ShellProfile.Folder(home));
    }

    /// Lines typed after a lost end marker may be the user's own.
    [Test]
    public async Task An_unterminated_block_is_left_alone() {
        var text = $"{ShellProfile.Begin}\nexport PATH=\"$PATH:/x\"\nexport MINE=1\n";

        await Assert.That(ShellProfile.Without(text, "export PATH=\"$PATH:/x\"")).IsNull();
    }

    [Test]
    public async Task A_marker_inside_a_line_is_not_a_block() {
        var text = $"echo '{ShellProfile.Begin}'\necho '{ShellProfile.End}'\n";

        await Assert.That(ShellProfile.Without(text, "export PATH=\"$PATH:/x\"")).IsNull();
    }

    [Test]
    public async Task A_folder_the_shell_would_expand_is_refused() {
        await Assert.That(ShellProfile.Line("/Users/a$b/.local/bin")).IsNull();
        await Assert.That(ShellProfile.Line("/Users/a b/.local/bin")).IsEqualTo("export PATH=\"$PATH:/Users/a b/.local/bin\"");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Removable_waits_while_another_installs_link_needs_the_line() {
        var profile = Tmp.CreateFile(".zprofile", "");
        new ShellProfile(Home, "/bin/zsh").Add();
        var link = Tmp.PathTo(".local", "bin", "kcap");
        Tmp.CreateDir(".local", "bin");
        File.CreateSymbolicLink(link, Tmp.CreateFile("other-kcap", ""));

        await Assert.That(ShellProfile.Removable(Home, [])).IsEmpty();
        await Assert.That(ShellProfile.Removable(Home, [link])).IsEquivalentTo([profile]);

        File.Delete(link);
        await Assert.That(ShellProfile.Removable(Home, [])).IsEquivalentTo([profile]);
    }
    [Test]
    public async Task History_expansion_in_a_home_path_is_refused() {
        var home = new UserHome(Tmp.CreateDir("a!b"));
        await Assert.That(new ShellProfile(home, "/bin/bash").Add()).IsFalse();
        await Assert.That(File.Exists(Path.Combine(home.Path, ".bash_profile"))).IsFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(8)]
    [Arguments(30)]
    public async Task Retry_repairs_an_interrupted_owned_append(int missing) {
        var partial = Block[..(Block.Length - missing - 1)];
        var profile = Tmp.CreateFile(".zprofile", "export A=1\n" + partial);
        new ShellProfile(Home, "/bin/zsh").Add();
        await Assert.That(File.ReadAllText(profile)).IsEqualTo("export A=1\n" + Block);
    }

    [Test]
    public async Task Modified_blocks_are_preserved_and_reported() {
        var text = Block.Replace(ShellProfile.End, "export MINE=1\n" + ShellProfile.End);
        var profile = Tmp.CreateFile(".zprofile", text);
        await Assert.That(() => ShellProfile.Remove(profile)).Throws<IOException>();
        await Assert.That(File.ReadAllText(profile)).IsEqualTo(text);
        await Assert.That(() => new ShellProfile(Home, "/bin/zsh").Add()).Throws<IOException>();
    }

    [Test]
    public async Task An_unreadable_profile_is_returned_for_cleanup_to_report() {
        var profile = Tmp.CreateFile(".zprofile", Block);
        using var held = new FileStream(profile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.That(ShellProfile.Removable(Home, [])).IsEquivalentTo([profile]);
    }

    [Test]
    public async Task Removal_refuses_a_profile_that_another_writer_holds() {
        var profile = Tmp.CreateFile(".zprofile", Block);
        using (var held = new FileStream(profile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) {
            await Assert.That(() => ShellProfile.Remove(profile)).Throws<IOException>();
        }
        await Assert.That(File.ReadAllText(profile)).IsEqualTo(Block);
    }

}
