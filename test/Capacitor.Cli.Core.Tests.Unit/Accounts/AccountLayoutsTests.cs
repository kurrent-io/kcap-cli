using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountLayoutsTests {
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task Default_claude_directory_keeps_the_sibling_user_config() {
        var paths = AccountLayouts.Claude(Home, Home.PathTo(".claude"));

        await Assert.That(paths.UserConfigJson).IsEqualTo(new ClaudePaths(Home, null).UserConfigJson);
    }

    [Test]
    public async Task Other_claude_directory_relocates_everything() {
        var paths = AccountLayouts.Claude(Home, Home.PathTo(".claude-work"));

        await Assert.That(paths.UserSettings).IsEqualTo(Home.PathTo(".claude-work", "settings.json"));
        await Assert.That(paths.UserConfigJson).IsEqualTo(Home.PathTo(".claude-work", ".claude.json"));
    }

    [Test]
    public async Task Codex_layout_points_at_the_account_home() {
        var paths = AccountLayouts.Codex(Home, Home.PathTo(".codex-b"));

        await Assert.That(paths.Sessions).IsEqualTo(Home.PathTo(".codex-b", "sessions"));
    }
}
