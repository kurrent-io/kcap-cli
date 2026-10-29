using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>The child environment the PTY spawn hands a vendor: the daemon's own, minus what the launch
/// removes, plus its overlay.</summary>
[NotInParallel]
public class UnixPtyProcessEnvTests {
    const string Variable = "CLAUDE_CONFIG_DIR";

    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task Inherited_variable_is_removed_when_named_in_the_removal_set() {
        var inherited = Home.PathTo(".claude-work");
        using var env = EnvScope.Exclusive(Variable, inherited);

        var kept    = UnixPtyProcess.BuildChildEnv(null, null, 80, 24);
        var removed = UnixPtyProcess.BuildChildEnv(null, [Variable], 80, 24);

        await Assert.That(kept[Variable]).IsEqualTo(inherited);
        await Assert.That(removed.ContainsKey(Variable)).IsFalse();
        await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo(inherited);
    }

    [Test]
    public async Task Overlay_sets_the_variable_after_removal() {
        var account = Home.PathTo(".claude-other");
        using var env = EnvScope.Exclusive(Variable, Home.PathTo(".claude-work"));

        var child = UnixPtyProcess.BuildChildEnv(new Dictionary<string, string> { [Variable] = account }, [Variable], 80, 24);

        await Assert.That(child[Variable]).IsEqualTo(account);
    }
}
