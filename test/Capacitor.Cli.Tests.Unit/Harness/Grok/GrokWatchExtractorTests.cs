using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Harness.Grok;

/// <summary>
/// The watcher reads Grok's <c>updates.jsonl</c> (ACP <c>session/update</c> lines) for its title and
/// event counts, rather than falling through to Claude's <c>type:"user"|"assistant"</c> shape.
/// </summary>
public class GrokWatchExtractorTests {
    static string Update(string kind, string text, string method = "session/update") => new JsonObject {
        ["timestamp"] = 1,
        ["method"]    = method,
        ["params"]    = new JsonObject {
            ["sessionId"] = "s",
            ["update"]    = new JsonObject {
                ["sessionUpdate"] = kind,
                ["content"]       = new JsonObject { ["type"] = "text", ["text"] = text },
            },
            ["_meta"] = new JsonObject { ["eventId"] = "s-1" },
        },
    }.ToJsonString();

    const string ToolCall =
        """{"timestamp":1,"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"tool_call","toolCallId":"c1","title":"list_dir","rawInput":{}},"_meta":{"eventId":"s-2"}}}""";

    [Test]
    public async Task The_user_s_prompt_is_title_text() {
        await Assert.That(WatchCommand.TryExtractUserText(Update("user_message_chunk", "summarize the repo"), TimeProvider.System, "grok"))
            .IsEqualTo("summarize the repo");
        await Assert.That(WatchCommand.TryExtractUserText(Update("agent_message_chunk", "here it is"), TimeProvider.System, "grok")).IsNull();
    }

    [Test]
    public async Task The_agent_s_message_is_assistant_text_but_its_thinking_is_not() {
        await Assert.That(WatchCommand.TryExtractAssistantText(Update("agent_message_chunk", "here it is"), "grok")).IsEqualTo("here it is");
        await Assert.That(WatchCommand.TryExtractAssistantText(Update("agent_thought_chunk", "hmm"), "grok")).IsNull();
    }

    [Test]
    public async Task Only_conversational_lines_count_as_events() {
        await Assert.That(WatchCommand.IsEvent(Update("user_message_chunk", "go"), "grok")).IsTrue();
        await Assert.That(WatchCommand.IsEvent(Update("agent_message_chunk", "ok"), "grok")).IsTrue();
        await Assert.That(WatchCommand.IsEvent(ToolCall, "grok")).IsFalse();
        await Assert.That(WatchCommand.IsEvent(Update("agent_message_chunk", "ok", "_x.ai/session/update"), "grok")).IsFalse();
    }
}
