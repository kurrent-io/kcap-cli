using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpSessionIdTests {
    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    // Injected rather than set on the process: the suite itself runs inside a harness session that
    // exports these variables, so a real-environment test could pass or fail on the runner's own id.
    static Func<string, string?> Env(Dictionary<string, string?> values) =>
        key => values.TryGetValue(key, out var value) ? value : null;

    [Test]
    public async Task Prefers_the_explicit_argument() {
        await Assert.That(McpSessionId.Resolve(Args("""{"session_id":"explicit1"}"""), Env(new()))).IsEqualTo("explicit1");
    }

    [Test]
    public async Task Strips_dashes_from_an_explicit_guid() {
        var id = McpSessionId.Resolve(Args("""{"session_id":"1234abcd-56ef-78ab-90cd-1234567890ab"}"""), Env(new()));

        await Assert.That(id).IsEqualTo("1234abcd56ef78ab90cd1234567890ab");
    }

    [Test]
    public async Task Falls_back_to_kcap_session_id() {
        await Assert.That(McpSessionId.Resolve(new JsonObject(), Env(new() { ["KCAP_SESSION_ID"] = "envsess1" }))).IsEqualTo("envsess1");
    }

    [Test]
    public async Task Falls_back_to_codex_thread_id() {
        await Assert.That(McpSessionId.Resolve(new JsonObject(), Env(new() { ["CODEX_THREAD_ID"] = "thread-1" }))).IsEqualTo("thread-1");
    }

    [Test]
    public async Task Falls_back_to_the_running_harness_session() {
        // KCAP_SESSION_ID reaches only a Claude Code session's Bash tool calls; the MCP server process
        // sees CLAUDE_CODE_SESSION_ID and nothing else, so this is the one ambient signal it ever gets.
        var id = McpSessionId.Resolve(new JsonObject(), Env(new() { ["CLAUDE_CODE_SESSION_ID"] = "1234abcd-56ef-78ab-90cd-1234567890ab" }));

        await Assert.That(id).IsEqualTo("1234abcd56ef78ab90cd1234567890ab");
    }

    [Test]
    public async Task Prefers_the_running_harness_session_over_an_inherited_env_var() {
        // A session launched from another session's shell inherits the parent's KCAP_SESSION_ID;
        // attaching to it would file the work under the wrong session without any error.
        var id = McpSessionId.Resolve(new JsonObject(), Env(new() {
            ["KCAP_SESSION_ID"]        = "22222222222222222222222222222222",
            ["CLAUDE_CODE_SESSION_ID"] = "11111111-1111-1111-1111-111111111111"
        }));

        await Assert.That(id).IsEqualTo("11111111111111111111111111111111");
    }

    [Test]
    public async Task Rejects_a_dot_segment_from_either_source() {
        // "." survives escaping, so a dot segment in a session URL would be normalized out of the route.
        var ambient = Assert.Throws<ArgumentException>(() =>
            McpSessionId.Resolve(new JsonObject(), Env(new() { ["CLAUDE_CODE_SESSION_ID"] = ".." })));
        await Assert.That(ambient!.Message).IsEqualTo(McpSessionId.NoSessionIdMessage);

        var explicitId = Assert.Throws<ArgumentException>(() => McpSessionId.Resolve(Args("""{"session_id":"."}"""), Env(new())));
        await Assert.That(explicitId!.Message).IsEqualTo(McpSessionId.NoSessionIdMessage);
    }

    [Test]
    public async Task Rejects_a_non_string_argument_as_a_field_error() {
        await Assert.That(() => McpSessionId.Resolve(Args("""{"session_id":42}"""), Env(new())))
            .Throws<ArgumentException>().WithMessageContaining("session_id");
    }

    [Test]
    public async Task Throws_when_no_source_yields_an_id() {
        var ex = Assert.Throws<ArgumentException>(() => McpSessionId.Resolve(new JsonObject(), Env(new())));

        await Assert.That(ex!.Message).IsEqualTo(McpSessionId.NoSessionIdMessage);
    }
}

public class McpToolArgumentsTests {
    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task Optional_string_is_null_for_absent_null_or_blank() {
        await Assert.That(McpToolArguments.OptionalString(Args("{}"), "k")).IsNull();
        await Assert.That(McpToolArguments.OptionalString(Args("""{"k":null}"""), "k")).IsNull();
        await Assert.That(McpToolArguments.OptionalString(Args("""{"k":"  "}"""), "k")).IsNull();
        await Assert.That(McpToolArguments.OptionalString(null, "k")).IsNull();
    }

    [Test]
    public async Task Optional_string_trims_and_rejects_a_non_string() {
        await Assert.That(McpToolArguments.OptionalString(Args("""{"k":" v "}"""), "k")).IsEqualTo("v");
        await Assert.That(() => McpToolArguments.OptionalString(Args("""{"k":7}"""), "k"))
            .Throws<ArgumentException>().WithMessageContaining("'k' must be a string");
    }

    [Test]
    public async Task Require_string_rejects_missing_blank_and_wrong_type() {
        await Assert.That(() => McpToolArguments.RequireString(Args("{}"), "k")).Throws<ArgumentException>().WithMessageContaining("required");
        await Assert.That(() => McpToolArguments.RequireString(Args("""{"k":" "}"""), "k")).Throws<ArgumentException>().WithMessageContaining("blank");
        await Assert.That(() => McpToolArguments.RequireString(Args("""{"k":[]}"""), "k")).Throws<ArgumentException>().WithMessageContaining("string");
    }

    [Test]
    public async Task Try_read_int_reads_wire_integers_and_rejects_other_shapes() {
        await Assert.That(McpToolArguments.TryReadInt(Args("""{"n":3}"""), "n", out var n)).IsTrue();
        await Assert.That(n).IsEqualTo(3);
        await Assert.That(McpToolArguments.TryReadInt(Args("{}"), "n", out _)).IsFalse();
        await Assert.That(McpToolArguments.TryReadInt(Args("""{"n":null}"""), "n", out _)).IsFalse();
        await Assert.That(() => McpToolArguments.TryReadInt(Args("""{"n":"3"}"""), "n", out _)).Throws<ArgumentException>();
        await Assert.That(() => McpToolArguments.TryReadInt(Args("""{"n":1.5}"""), "n", out _)).Throws<ArgumentException>();
    }
}
