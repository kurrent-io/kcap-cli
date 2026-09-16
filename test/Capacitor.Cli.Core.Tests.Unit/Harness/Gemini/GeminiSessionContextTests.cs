using Capacitor.Cli.Core.Harness.Gemini;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Gemini;

/// <summary>
/// Unit tests for <see cref="GeminiSessionContext"/>. The bootstrap block is the only place a
/// Gemini recording names its working directory, so every shape it arrives in has to resolve —
/// and every shape it does not has to read as "no workspace" rather than as a guess, since a
/// wrong directory would place a session an allow list meant to exclude.
/// </summary>
public class GeminiSessionContextTests {
    const string Bootstrap =
        "<session_context>\\nThis is the Gemini CLI. We are setting up the context for our chat.\\n"
      + "My operating system is: darwin\\n"
      + "The project's temporary directory is: /Users/tony/.gemini/tmp/proj\\n"
      + "- **Workspace Directories:**\\n  - /work/demo\\n- **Directory Structure:**\\n\\n/work/demo/\\n"
      + "\\u251c\\u2500\\u2500\\u2500app.py\\n\\n\\n</session_context>";

    [Test]
    public async Task The_seed_op_that_opens_a_recording_names_the_workspace() {
        var line = $$$"""
            {"$set":{"messages":[{"id":"d0","timestamp":"t","type":"user","content":[{"text":"{{{Bootstrap}}}"}]}],"lastUpdated":"t"}}
            """;

        await Assert.That(GeminiSessionContext.TryReadWorkspace(line)).IsEqualTo("/work/demo");
    }

    [Test]
    public async Task A_bare_user_message_names_the_workspace() {
        var line = $$"""
            {"id":"d0","timestamp":"t","type":"user","content":[{"text":"{{Bootstrap}}"}]}
            """;

        await Assert.That(GeminiSessionContext.TryReadWorkspace(line)).IsEqualTo("/work/demo");
    }

    [Test]
    public async Task The_first_of_several_workspace_directories_wins() {
        var bootstrap = "<session_context>\\n- **Workspace Directories:**\\n  - /work/demo\\n  - /work/shared\\n"
                      + "- **Directory Structure:**\\n</session_context>";
        var line      = $$"""{"id":"d0","type":"user","content":[{"text":"{{bootstrap}}"}]}""";

        await Assert.That(GeminiSessionContext.TryReadWorkspace(line)).IsEqualTo("/work/demo");
    }

    [Test]
    public async Task A_content_string_rather_than_a_part_array_still_resolves() {
        var line = $$"""{"id":"d0","type":"user","content":"{{Bootstrap}}"}""";

        await Assert.That(GeminiSessionContext.TryReadWorkspace(line)).IsEqualTo("/work/demo");
    }

    // A turn that merely quotes the tag must not be mined for a directory — only a message that
    // IS the bootstrap counts, which is the same rule the server normalizer applies.
    [Test]
    public async Task A_turn_that_only_quotes_the_tag_carries_no_workspace() {
        var line = """
            {"id":"m1","type":"gemini","content":"You asked about <session_context>\n- **Workspace Directories:**\n  - /work/demo\n"}
            """;

        await Assert.That(GeminiSessionContext.TryReadWorkspace(line)).IsNull();
    }

    [Test]
    public async Task The_header_record_carries_no_workspace() {
        await Assert.That(GeminiSessionContext.TryReadWorkspace(
            """{"sessionId":"s","projectHash":"h","kind":"main"}""")).IsNull();
    }

    [Test]
    public async Task An_empty_workspace_list_resolves_to_nothing_not_the_next_heading() {
        var bootstrap = "<session_context>\\n- **Workspace Directories:**\\n- **Directory Structure:**\\n  - /work/demo\\n</session_context>";
        var line      = $$"""{"id":"d0","type":"user","content":[{"text":"{{bootstrap}}"}]}""";

        await Assert.That(GeminiSessionContext.TryReadWorkspace(line)).IsNull();
    }

    [Test]
    public async Task A_bootstrap_with_no_workspace_heading_resolves_to_nothing() {
        var line = """{"id":"d0","type":"user","content":[{"text":"<session_context>\nThis is the Gemini CLI.\n</session_context>"}]}""";

        await Assert.That(GeminiSessionContext.TryReadWorkspace(line)).IsNull();
    }

    [Test]
    public async Task A_malformed_line_resolves_to_nothing_rather_than_throwing() {
        await Assert.That(GeminiSessionContext.TryReadWorkspace("{\"$set\": <session_context>")).IsNull();
    }
}
