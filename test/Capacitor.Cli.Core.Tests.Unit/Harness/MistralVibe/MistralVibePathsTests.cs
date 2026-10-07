using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.MistralVibe;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.MistralVibe;

public class MistralVibePathsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Defaults_under_dot_vibe() {
        var home = Path.Combine("x", "home");
        var p    = new MistralVibePaths(new UserHome(home), null);

        await Assert.That(p.Home).IsEqualTo(Path.Combine(home, ".vibe"));
        await Assert.That(p.HooksToml).IsEqualTo(Path.Combine(p.Home, "hooks.toml"));
        await Assert.That(p.ConfigToml).IsEqualTo(Path.Combine(p.Home, "config.toml"));
        await Assert.That(p.SessionLogsDir).IsEqualTo(Path.Combine(p.Home, "logs", "session"));
        await Assert.That(p.UnifiedDir).IsEqualTo(Path.Combine(p.Home, "logs", "session", "unified"));
    }

    [Test]
    public async Task Vibe_home_override_replaces_the_root() {
        var custom = Path.Combine("custom", "v");
        var p      = new MistralVibePaths(new UserHome(Path.Combine("x", "home")), custom);

        await Assert.That(p.Home).IsEqualTo(custom);
        await Assert.That(p.ConfigToml).IsEqualTo(Path.Combine(custom, "config.toml"));
    }

    [Test]
    public async Task Has_user_data_needs_a_config_file_or_a_session_log_dir() {
        var p = new MistralVibePaths(new UserHome(Tmp.Path), null);
        await Assert.That(p.HasUserData()).IsFalse();         // bare root, and no root yet

        Tmp.CreateDir(".vibe");
        await Assert.That(p.HasUserData()).IsFalse();         // empty root does not count

        Tmp.CreateFile(Path.Combine(".vibe", "config.toml"), "");
        await Assert.That(p.HasUserData()).IsTrue();          // a Vibe-written config marks it
    }
}
