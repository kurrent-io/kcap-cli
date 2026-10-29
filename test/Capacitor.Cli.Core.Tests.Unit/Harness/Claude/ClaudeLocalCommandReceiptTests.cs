using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudeLocalCommandReceiptTests {
    static ChatProjectionResult P(string line) {
        var chat = TranscriptChat.For("claude")!;
        return chat.ProjectWithInputs(line, 1, DateTimeOffset.UtcNow, chat.CreateContext("s", null));
    }

    [Test]
    [Arguments("rename", "Initial prompt via file issue")]
    [Arguments("compact", "")]
    [Arguments("plugin:command", "  a < b & c\nsecond line  ")]
    public async Task A_structured_local_command_result_is_a_hidden_receipt(string command, string args) {
        var result = P(JsonSerializer.Serialize(new {
            type = "system", subtype = "local_command",
            content = "<local-command-stdout>Done</local-command-stdout>",
            commandRun = new { command, args }
        }));
        var expected = "/" + command + (args.Trim().Length == 0 ? "" : " " + args.Trim());
        await Assert.That(result.SubmittedInputs).IsEquivalentTo(new[] { expected });
        await Assert.That(result.Envelopes).IsEmpty();
        await Assert.That(result.LiteralInputs).IsEmpty();
        await Assert.That(result.RestoreSubmittedInputs(_ => true).Envelopes).IsEmpty();
    }

    [Test]
    [Arguments("\"subtype\":\"other\",\"commandRun\":{\"command\":\"rename\",\"args\":\"x\"}")]
    [Arguments("\"subtype\":\"local_command\",\"content\":\"<command-name>/rename</command-name><command-args>x</command-args>\"")]
    [Arguments("\"subtype\":\"local_command\",\"content\":\"<local-command-stdout>Session renamed to: x</local-command-stdout>\"")]
    [Arguments("\"subtype\":\"local_command\",\"commandRun\":{\"command\":\"rename\"}")]
    [Arguments("\"subtype\":\"local_command\",\"commandRun\":{\"command\":\"rename\",\"args\":null}")]
    [Arguments("\"subtype\":\"local_command\",\"commandRun\":{\"command\":\"\",\"args\":\"x\"}")]
    [Arguments("\"subtype\":\"local_command\",\"commandRun\":{\"command\":\"/rename\",\"args\":\"x\"}")]
    [Arguments("\"subtype\":\"local_command\",\"commandRun\":{\"command\":\"rename x\",\"args\":\"x\"}")]
    public async Task Unstructured_or_malformed_system_records_are_not_receipts(string fields) {
        var result = P("{\"type\":\"system\"," + fields + "}");
        await Assert.That(result.SubmittedInputs).IsEmpty();
        await Assert.That(result.Envelopes).IsEmpty();
    }

    [Test]
    [Arguments("\"isMeta\":true")]
    [Arguments("\"isSidechain\":true")]
    [Arguments("\"origin\":{\"kind\":\"task-notification\"}")]
    [Arguments("\"origin\":{\"kind\":\"plugin\"}")]
    public async Task Non_user_local_commands_cannot_acknowledge_input(string flags) {
        var result = P("{\"type\":\"system\",\"subtype\":\"local_command\",\"commandRun\":{\"command\":\"rename\",\"args\":\"x\"}," + flags + "}");
        await Assert.That(result.SubmittedInputs).IsEmpty();
        await Assert.That(result.Envelopes).IsEmpty();
        await Assert.That(result.RestoreSubmittedInputs(_ => true).Envelopes).IsEmpty();
    }
}
