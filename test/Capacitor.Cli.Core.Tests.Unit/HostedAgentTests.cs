namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// What the daemon exports into an agent it hosts, as this process reads it back. Both variables
/// are inherited by every spawned child, so the cohort is the whole assembly.
/// </summary>
[NotInParallel]
public class HostedAgentTests {
    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task A_blank_id_names_no_agent(string? raw) {
        using var _ = EnvScope.Exclusive("KCAP_AGENT_ID", raw);

        await Assert.That(HostedAgent.FromEnvironment().AgentId).IsNull();
    }

    [Test]
    public async Task An_id_is_read_verbatim() {
        using var _ = EnvScope.Exclusive("KCAP_AGENT_ID", "6ba7b8109dad11d180b400c04fd430c8");

        await Assert.That(HostedAgent.FromEnvironment().AgentId).IsEqualTo("6ba7b8109dad11d180b400c04fd430c8");
    }

    /// The daemon exports the id unconditionally and the bridge only when it has one, so an id
    /// without a bridge is the ordinary shape rather than a broken environment.
    [Test]
    public async Task An_id_without_a_bridge_is_hosted() {
        using var id     = EnvScope.Exclusive("KCAP_AGENT_ID", "agent-1");
        using var bridge = EnvScope.Exclusive("KCAP_DAEMON_URL", null);

        var hosted = HostedAgent.FromEnvironment();

        await Assert.That(hosted.AgentId).IsEqualTo("agent-1");
        await Assert.That(hosted.Bridge).IsEqualTo(DaemonBridge.None);
    }

    [Test]
    public async Task A_bridge_is_parsed_alongside_the_id() {
        using var id     = EnvScope.Exclusive("KCAP_AGENT_ID", "agent-1");
        using var bridge = EnvScope.Exclusive("KCAP_DAEMON_URL", "http://127.0.0.1:9/b/");

        var hosted = HostedAgent.FromEnvironment();

        await Assert.That(hosted.AgentId).IsEqualTo("agent-1");
        await Assert.That(hosted.Bridge).IsEqualTo(new DaemonBridge.Loopback("http://127.0.0.1:9/b"));
    }
}
