using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ShellArgumentTests {
    [Test]
    public async Task A_plain_path_is_left_bare() {
        await Assert.That(ShellArgument.Quote("/home/u/.claude-work", windows: false)).IsEqualTo("/home/u/.claude-work");
    }

    [Test]
    public async Task A_posix_path_with_a_space_is_single_quoted() {
        await Assert.That(ShellArgument.Quote("/home/u/my claude", windows: false)).IsEqualTo("'/home/u/my claude'");
    }

    [Test]
    public async Task A_posix_single_quote_is_closed_escaped_and_reopened() {
        await Assert.That(ShellArgument.Quote("/home/u/it's", windows: false)).IsEqualTo("'/home/u/it'\\''s'");
    }

    [Test]
    public async Task A_windows_path_with_a_space_is_double_quoted() {
        await Assert.That(ShellArgument.Quote(@"C:\Users\u\my claude", windows: true)).IsEqualTo(@"""C:\Users\u\my claude""");
    }

    [Test]
    public async Task A_posix_backslash_is_quoted() {
        await Assert.That(ShellArgument.Quote(@"/home/u/a\b", windows: false)).IsEqualTo(@"'/home/u/a\b'");
    }
}
