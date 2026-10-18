using System.Text.Json;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Harness.Grok;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Grok;

public class GrokSubagentSignalsTests {
    const string Child = "01a14f26-a703-7de1-aa37-7eda7334fe82";

    static AcpNotification Notification(string method, string sessionUpdate) =>
        new(method, JsonDocument.Parse($$$"""
            {"sessionId":"parent","update":{"sessionUpdate":"{{{sessionUpdate}}}","child_session_id":"{{{Child}}}","subagent_type":"general-purpose"}}
            """).RootElement.Clone());

    [Test]
    public async Task A_spawned_subagent_starts_with_its_child_session_and_type() {
        var signal = GrokSubagentSignals.Read(Notification("_x.ai/session_notification", "subagent_spawned"));

        await Assert.That(signal).IsEqualTo(new Daemon.Acp.AcpSubagentSignal(Started: true, Child, "01a14f26a7037de1aa377eda7334fe82", "general-purpose"));
    }

    [Test]
    public async Task A_finished_subagent_ends() {
        var signal = GrokSubagentSignals.Read(Notification("_x.ai/session_notification", "subagent_finished"));

        await Assert.That(signal).IsEqualTo(new Daemon.Acp.AcpSubagentSignal(Started: false, Child, "01a14f26a7037de1aa377eda7334fe82", AgentType: null));
    }

    /// <summary>session/load replays a finished turn's frames under another method; the subagent in
    /// them already ran.</summary>
    [Test]
    public async Task A_replayed_subagent_is_not_a_new_one() {
        await Assert.That(GrokSubagentSignals.Read(Notification("_x.ai/session/update", "subagent_spawned"))).IsNull();
    }
}
