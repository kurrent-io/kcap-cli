using Capacitor.Cli.Core.Harness.Grok;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Grok;

public class GrokSubagentsTests {
    const string Spawned =
        """{"timestamp":1,"method":"_x.ai/session/update","params":{"sessionId":"p","update":{"sessionUpdate":"subagent_spawned","subagent_id":"01a135a0-64af-7661-94e6-282ec3ef065c","parent_session_id":"p","child_session_id":"01a135a0-64af-7661-94e6-282ec3ef065c","subagent_type":"general-purpose"},"_meta":{"eventId":"p-1"}}}""";
    const string Finished =
        """{"timestamp":2,"method":"_x.ai/session/update","params":{"sessionId":"p","update":{"sessionUpdate":"subagent_finished","subagent_id":"01a135a0-64af-7661-94e6-282ec3ef065c","child_session_id":"01a135a0-64af-7661-94e6-282ec3ef065c","status":"completed"},"_meta":{"eventId":"p-2"}}}""";
    const string Chunk =
        """{"timestamp":1,"method":"session/update","params":{"sessionId":"p","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"subagent_spawned"}},"_meta":{"eventId":"p-3"}}}""";

    [Test]
    public async Task Spawns_and_finishes_are_read_from_the_parent_s_lines() {
        var events = GrokSubagents.Read([Spawned, Chunk, Finished, "not json"]).ToArray();

        await Assert.That(events.Length).IsEqualTo(2);
        await Assert.That(events[0]).IsEqualTo(new GrokSubagentEvent("01a135a0-64af-7661-94e6-282ec3ef065c", "general-purpose", Finished: false));
        await Assert.That(events[1].Finished).IsTrue();
    }

    /// <summary>The child id becomes a path segment, so only a session id Grok could have written is
    /// accepted; anything else would point the import at a file outside the sessions directory.</summary>
    [Test]
    [Arguments("../../../.ssh/id_rsa")]
    [Arguments("/etc/passwd")]
    [Arguments("child-id")]
    public async Task A_child_id_that_is_not_a_session_id_is_ignored(string child) {
        await Assert.That(GrokSubagents.Read([Spawned.Replace("01a135a0-64af-7661-94e6-282ec3ef065c", child)]).ToArray()).IsEmpty();
    }

    [Test]
    public async Task A_child_s_transcript_is_its_sibling_session_directory() {
        var parent = Path.Combine("home", ".grok", "sessions", "%2Frepo", "parent-id", "updates.jsonl");

        await Assert.That(GrokSubagents.ChildTranscript(parent, "child-id"))
            .IsEqualTo(Path.Combine("home", ".grok", "sessions", "%2Frepo", "child-id", "updates.jsonl"));
    }

    [Test]
    public async Task A_child_s_agent_id_is_its_session_id_without_dashes() {
        await Assert.That(GrokSubagents.AgentId("01a135a0-64af-7661-94e6-282ec3ef065c")).IsEqualTo("01a135a064af766194e6282ec3ef065c");
    }
}
