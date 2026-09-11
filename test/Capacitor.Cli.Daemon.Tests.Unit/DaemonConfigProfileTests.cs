using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// Verifies that <see cref="DaemonConfig"/> is correctly populated from
/// <see cref="DaemonSettings"/> profile values — mirrors the logic in
/// DaemonRunner.RunAsync so that changes to either stay in sync.
/// </summary>
public class DaemonConfigProfileTests {
    // Helper: simulate the DaemonRunner profile-wiring block in isolation.
    static DaemonConfig ApplyProfileSettings(DaemonConfig config, DaemonSettings? profileDaemon, bool maxAgentsFromArgs = false) {
        if (string.IsNullOrEmpty(config.Name) && !string.IsNullOrEmpty(profileDaemon?.Name))
            config.Name = profileDaemon.Name;

        DaemonRunner.ApplyProfileCapacity(config, profileDaemon, maxAgentsFromArgs);

        if (!string.IsNullOrEmpty(profileDaemon?.ClaudePath))
            config.ClaudePath = profileDaemon.ClaudePath;

        if (!string.IsNullOrEmpty(profileDaemon?.CodexPath))
            config.CodexPath = profileDaemon.CodexPath;

        return config;
    }

    // ── max_agents from profile ──────────────────────────────────────────────

    [Test]
    public async Task MaxAgents_FromProfile_Applies() {
        var config = ApplyProfileSettings(new DaemonConfig(), new DaemonSettings { MaxAgents = 3 });

        await Assert.That(config.MaxConcurrentAgents).IsEqualTo(3);
    }

    /// A profile value equal to the default must still be the profile's value, not "unset".
    [Test]
    public async Task MaxAgents_FromProfile_Applies_when_it_equals_the_default() {
        var config = ApplyProfileSettings(new DaemonConfig { MaxConcurrentAgents = 8 }, new DaemonSettings { MaxAgents = 5 });

        await Assert.That(config.MaxConcurrentAgents).IsEqualTo(5);
    }

    [Test]
    public async Task MaxAgents_FromArgs_WinsOverProfile() {
        var config = ApplyProfileSettings(new DaemonConfig { MaxConcurrentAgents = 7 }, new DaemonSettings { MaxAgents = 3 }, maxAgentsFromArgs: true);

        await Assert.That(config.MaxConcurrentAgents).IsEqualTo(7);
    }

    [Test]
    public async Task MaxAgents_NullProfile_KeepsDefault() {
        var config = ApplyProfileSettings(new DaemonConfig(), profileDaemon: null);

        await Assert.That(config.MaxConcurrentAgents).IsEqualTo(5);
    }

    // ── claude_path from profile ─────────────────────────────────────────────

    [Test]
    public async Task ClaudePath_FromProfile_OverridesDefault() {
        var daemon  = new DaemonSettings { ClaudePath = "/opt/claude/bin/claude" };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.ClaudePath).IsEqualTo("/opt/claude/bin/claude");
    }

    [Test]
    public async Task ClaudePath_NullInProfile_KeepsDefault() {
        var daemon  = new DaemonSettings { ClaudePath = null };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.ClaudePath).IsEqualTo("claude");
    }

    [Test]
    public async Task ClaudePath_EmptyInProfile_KeepsDefault() {
        var daemon  = new DaemonSettings { ClaudePath = "" };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.ClaudePath).IsEqualTo("claude");
    }

    [Test]
    public async Task ClaudePath_NullProfile_KeepsDefault() {
        var config = ApplyProfileSettings(new DaemonConfig(), profileDaemon: null);

        await Assert.That(config.ClaudePath).IsEqualTo("claude");
    }

    // ── codex_path from profile ──────────────────────────────────────────────

    [Test]
    public async Task CodexPath_FromProfile_OverridesDefault() {
        var daemon  = new DaemonSettings { CodexPath = "/opt/codex/bin/codex" };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.CodexPath).IsEqualTo("/opt/codex/bin/codex");
    }

    [Test]
    public async Task CodexPath_NullInProfile_KeepsDefault() {
        var daemon  = new DaemonSettings { CodexPath = null };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.CodexPath).IsEqualTo("codex");
    }

    [Test]
    public async Task CodexPath_EmptyInProfile_KeepsDefault() {
        var daemon  = new DaemonSettings { CodexPath = "" };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.CodexPath).IsEqualTo("codex");
    }

    [Test]
    public async Task CodexPath_NullProfile_KeepsDefault() {
        var config = ApplyProfileSettings(new DaemonConfig(), profileDaemon: null);

        await Assert.That(config.CodexPath).IsEqualTo("codex");
    }

    // ── both paths set simultaneously ────────────────────────────────────────

    [Test]
    public async Task BothPaths_FromProfile_OverrideBothDefaults() {
        var daemon  = new DaemonSettings { ClaudePath = "/a/claude", CodexPath = "/b/codex" };
        var config  = ApplyProfileSettings(new DaemonConfig(), daemon);

        await Assert.That(config.ClaudePath).IsEqualTo("/a/claude");
        await Assert.That(config.CodexPath).IsEqualTo("/b/codex");
    }
}
