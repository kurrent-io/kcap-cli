using System.Text.Json;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Import;

public class CursorSubagentCorrelatorTests {
    // Cursor runs a subagent as its OWN session/transcript. The only in-data link is that
    // the child's first user_query (minus the <user_query> wrapper) is byte-identical to the
    // parent's `Task`/`Agent` tool_use `prompt`. The correlator recovers that link (AI-1153).

    static string Line(object o) => JsonSerializer.Serialize(o);

    static string ParentTranscript(string prompt, string subagentType = "generalPurpose") => string.Join("\n",
        Line(new { role = "user", message = new { content = new object[] { new { type = "text", text = "do the thing" } } } }),
        Line(new {
            role    = "assistant",
            message = new {
                content = new object[] {
                    new { type = "tool_use", name = "Task", input = new { description = "explore", prompt, subagent_type = subagentType } }
                }
            }
        })
    );

    static string ChildTranscript(string prompt) => string.Join("\n",
        Line(new { role = "user", message = new { content = new object[] { new { type = "text", text = "<user_query>\n" + prompt + "\n</user_query>" } } } }),
        Line(new { role = "assistant", message = new { content = new object[] { new { type = "text", text = "exploring" } } } })
    );

    static string TaskLine(string prompt, string subagentType) => Line(new {
        role    = "assistant",
        message = new { content = new object[] { new { type = "tool_use", name = "Task", input = new { prompt, subagent_type = subagentType } } } }
    });

    [Test]
    public async Task links_child_to_parent_when_first_user_query_matches_task_prompt() {
        using var fx = new CorrelatorFixture();
        const string prompt = "You are exploring the LoanApplicationDemo project. Return an overview.";
        var parent = fx.Add("11111111111111111111111111111111", ParentTranscript(prompt));
        var child  = fx.Add("22222222222222222222222222222222", ChildTranscript(prompt));

        var links = CursorSubagentCorrelator.Correlate([
            ("11111111111111111111111111111111", parent),
            ("22222222222222222222222222222222", child),
        ]);

        await Assert.That(links.ContainsKey("22222222222222222222222222222222")).IsTrue();
        await Assert.That(links["22222222222222222222222222222222"].ParentSessionId).IsEqualTo("11111111111111111111111111111111");
        await Assert.That(links["22222222222222222222222222222222"].SubagentType).IsEqualTo("generalPurpose");
        // The parent itself is not a child of anyone.
        await Assert.That(links.ContainsKey("11111111111111111111111111111111")).IsFalse();
    }

    [Test]
    public async Task no_link_when_no_prompt_matches() {
        using var fx = new CorrelatorFixture();
        var a = fx.Add("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ParentTranscript("prompt A"));
        var b = fx.Add("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", ChildTranscript("a totally different prompt"));

        var links = CursorSubagentCorrelator.Correlate([
            ("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", a),
            ("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", b),
        ]);

        await Assert.That(links.Count).IsEqualTo(0);
    }

    [Test]
    public async Task does_not_self_link_when_a_session_tasks_with_its_own_first_prompt() {
        // Degenerate guard: a session must never be classified as its own subagent.
        using var fx = new CorrelatorFixture();
        const string prompt = "explore repo";
        var self = fx.Add("cccccccccccccccccccccccccccccccc", ChildTranscript(prompt) + "\n" + TaskLine(prompt, "x"));

        var links = CursorSubagentCorrelator.Correlate([("cccccccccccccccccccccccccccccccc", self)]);

        await Assert.That(links.ContainsKey("cccccccccccccccccccccccccccccccc")).IsFalse();
    }

    sealed class CorrelatorFixture : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"kcap-correlator-{Guid.NewGuid():N}");

        public CorrelatorFixture() => Directory.CreateDirectory(Root);

        public string Add(string sessionId, string jsonl) {
            var path = Path.Combine(Root, sessionId + ".jsonl");
            File.WriteAllText(path, jsonl.ReplaceLineEndings("\n") + "\n");
            return path;
        }

        public void Dispose() {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
