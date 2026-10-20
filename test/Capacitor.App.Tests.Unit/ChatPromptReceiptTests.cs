using System.Text;
using System.Text.Json;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class ChatPromptReceiptTests {
    static readonly IReadOnlyList<ChatPromptCase> Corpus = ChatPromptCorpus.All();
    static readonly DateTimeOffset Received = DateTimeOffset.UnixEpoch;

    [Test]
    [Arguments("claude", "plain")]
    [Arguments("claude", "blocks")]
    [Arguments("claude", "midturn")]
    [Arguments("claude", "paste-lf")]
    [Arguments("claude", "paste-crlf")]
    [Arguments("claude", "terminal")]
    [Arguments("codex", "plain")]
    [Arguments("codex", "terminal")]
    public Task Literal_prompts_leave_the_queue_and_keep_their_body(string vendor, string shape) => RunOnUiAsync(async () => {
        await Assert.That(Corpus.Count).IsEqualTo(256);
        foreach (var prompt in Corpus) {
            await using var h = new ChatPromptHarness(vendor);
            await h.ReadyAsync();
            await h.SendAsync(prompt.Text);
            await Assert.That(h.Chat.QueuedMessages.Count).IsEqualTo(1).Because(prompt.Id);
            await h.DeliverAsync(Project(vendor, shape, prompt.Text));
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse().Because($"{prompt.Id}/{shape}");
            await Assert.That(h.Chat.Items.OfType<UserTurnItem>().Single().Text.Replace("\r\n", "\n").Trim())
                .IsEqualTo(prompt.Text.Replace("\r\n", "\n").Trim()).Because($"{prompt.Id}/{shape}");
            await Assert.That(h.Chat.Items.OfType<AssistantTextItem>().Single().Text).IsEqualTo("working");
            await Assert.That(h.Input.Sends).IsEqualTo(1);
        }
    });

    [Test]
    [Arguments("claude")]
    [Arguments("codex")]
    public Task Attached_literals_require_the_attachment_receipt_and_keep_their_body(string vendor) => RunOnUiAsync(async () => {
        foreach (var prompt in Corpus) {
            await using var h = new ChatPromptHarness(vendor);
            await h.ReadyAsync();
            h.Chat.Tray.AddAll([new StagedAttachment("example.png", "image/png", new byte[] { 1 })]);
            await h.SendAsync(prompt.Text);
            await Assert.That(h.Chat.QueuedMessages.Count).IsEqualTo(1).Because(prompt.Id);
            await h.DeliverAsync(Project(vendor, "plain", prompt.Text));
            await Assert.That(h.Chat.QueuedMessages.Count).IsEqualTo(1).Because($"bare echo: {prompt.Id}");
            var shape = vendor == "claude" ? "paste-crlf" : "plain";
            await h.DeliverAsync(Project(vendor, shape, prompt.Text + "\n\n[Attached files: .attached/example/a.png]"));
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse().Because(prompt.Id);
            await Assert.That(AttachmentTrailer.Strip(h.Chat.Items.OfType<UserTurnItem>().Last().Text).Replace("\r\n", "\n").Trim())
                .IsEqualTo(prompt.Text.Replace("\r\n", "\n").Trim()).Because(prompt.Id);
            await Assert.That(h.Input.Sends).IsEqualTo(1);
        }
    });

    [Test]
    [Arguments("claude", "<system-reminder>literal</system-reminder>")]
    [Arguments("claude", "Explain <system-reminder>literal</system-reminder>\nKeep it.")]
    [Arguments("codex", "# AGENTS.md instructions\nThis is a user example.")]
    public Task Replay_keeps_a_confirmed_literal_without_acknowledging_a_new_identical_send(string vendor, string text) => RunOnUiAsync(async () => {
        await using var h = new ChatPromptHarness(vendor);
        await h.ReadyAsync();
        var receipt = Project(vendor, "plain", text);
        await h.SendAsync(text);
        await h.DeliverAsync(receipt);
        await h.SendAsync(text);
        h.Feed.Reset(receipt, receipt);
        await h.TickAsync();
        await Assert.That(h.Chat.QueuedMessages.Count).IsEqualTo(1);
        await Assert.That(h.Chat.Items.OfType<UserTurnItem>().Count(i => i.Text == text)).IsEqualTo(1);
        await h.DeliverAsync(receipt);
        await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
        await Assert.That(h.Chat.Items.OfType<UserTurnItem>().Count(i => i.Text == text)).IsEqualTo(2);
    });

    [Test]
    public Task One_literal_receipt_does_not_acknowledge_its_cleaned_text_or_a_repeated_send() => RunOnUiAsync(async () => {
        await using var h = new ChatPromptHarness("claude");
        await h.ReadyAsync();
        const string text = "before <system-reminder>literal</system-reminder> after";
        await h.SendAsync("before  after");
        await h.SendAsync(text);
        await h.SendAsync(text);
        await h.DeliverAsync(Project("claude", "plain", text));
        await Assert.That(h.Chat.QueuedMessages.Select(q => q.Text)).IsEquivalentTo(new[] { "before  after", text });
        await Assert.That(h.Chat.Items.OfType<UserTurnItem>().Single().Text).IsEqualTo(text);
        await h.DeliverAsync(Project("claude", "plain", text));
        await Assert.That(h.Chat.QueuedMessages.Single().Text).IsEqualTo("before  after");
    });

    [Test]
    public Task An_old_literal_receipt_does_not_acknowledge_its_queued_resend() => RunOnUiAsync(async () => {
        await using var h = new ChatPromptHarness("claude");
        await h.ReadyAsync();
        h.SetIdle();
        const string text = "Explain <system-reminder>literal</system-reminder>\nKeep it.";
        var receipt = Project("claude", "plain", text);
        await h.SendAsync(text);
        var old = h.Chat.QueuedMessages.Single();
        // The original echo exists before the resend, but the pane has not drained it yet.
        h.Feed.Enqueue(receipt);
        await h.Chat.SendQueuedCommand.Execute(old);
        await Assert.That(h.Input.Sends).IsEqualTo(2);
        await h.TickAsync();
        await Assert.That(h.Chat.QueuedMessages.Count).IsEqualTo(1);
        await Assert.That(h.Chat.QueuedMessages.Single()).IsNotSameReferenceAs(old);
        await h.DeliverAsync(receipt);
        await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
    });

    [Test]
    [Arguments("claude", "plain", "Explain <system-reminder>literal</system-reminder>\nKeep it.")]
    [Arguments("claude", "paste-crlf", "line one\n/tmp/demo\necho hello")]
    [Arguments("codex", "plain", "# AGENTS.md instructions\nThis is a user example.")]
    public Task Old_history_cannot_restore_or_acknowledge_a_future_literal_send(string vendor, string shape, string text) => RunOnUiAsync(async () => {
        await using var h = new ChatPromptHarness(vendor);
        await h.ReadyAsync();
        var receipt = Project(vendor, shape, text);
        // This line existed before the send but had not yet been drained.
        h.Feed.Enqueue(receipt);
        await h.SendAsync(text);
        await h.TickAsync();
        await Assert.That(h.Chat.QueuedMessages.Count).IsEqualTo(1);
        await h.DeliverAsync(receipt);
        await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
    });

    [Test]
    [Arguments("isMeta", true)]
    [Arguments("isSidechain", true)]
    [Arguments("isCompactSummary", true)]
    [Arguments("origin", false)]
    public Task Trusted_injected_records_do_not_acknowledge_matching_literal_text(string field, bool flag) => RunOnUiAsync(async () => {
        await using var h = new ChatPromptHarness("claude");
        await h.ReadyAsync();
        const string text = "<agent-message from=\"peer\">\nLiteral example\n</agent-message>";
        await h.SendAsync(text);
        var fields = new Dictionary<string, object> { ["type"] = "user", ["message"] = new { content = text } };
        fields[field] = flag ? true : new { kind = "peer" };
        var chat = TranscriptChat.For("claude")!;
        var receipt = chat.ProjectWithInputs(JsonSerializer.Serialize(fields), 1, Received, chat.CreateContext("s", null));
        await h.DeliverAsync(receipt);
        await Assert.That(h.Chat.QueuedMessages.Count).IsEqualTo(1);
    });

    [Test]
    public Task A_literal_task_notification_does_not_finish_a_running_subagent() => RunOnUiAsync(async () => {
        await using var h = new ChatPromptHarness("claude");
        await h.ReadyAsync();
        var chat = TranscriptChat.For("claude")!;
        var start = chat.ProjectWithInputs("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"tool-1","name":"Agent","input":{"description":"running","subagent_type":"explore"}}]}}""", 1, Received, chat.CreateContext("s", null));
        await h.DeliverAsync(start);
        const string text = "<task-notification>\n<task-id>task-1</task-id><tool-use-id>tool-1</tool-use-id><status>completed</status>\n</task-notification>";
        await h.SendAsync(text);
        await h.DeliverAsync(Project("claude", "plain", text));
        await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
        await Assert.That(h.Runs.RunningCount).IsEqualTo(1);
    });

    static ChatProjectionResult Project(string vendor, string shape, string text) {
        var echo = shape == "terminal" ? Encoding.UTF8.GetString(TerminalInputEncoder.Paste(text))[6..^6] : text;
        if (shape.StartsWith("paste-", StringComparison.Ordinal)) {
            var newline = shape == "paste-lf" ? "\n" : "\r\n";
            echo = $"<pasted_content id=\"receipt\">{newline}{echo}{newline}</pasted_content id=\"receipt\">";
        }
        var line = vendor == "codex"
            ? JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = echo } } } })
            : shape == "midturn"
                ? JsonSerializer.Serialize(new { type = "attachment", attachment = new { type = "queued_command", commandMode = "prompt", prompt = echo, origin = new { kind = "human" } } })
            : shape == "blocks"
                ? JsonSerializer.Serialize(new { type = "user", message = new { content = new[] { new { type = "text", text = echo } } } })
                : JsonSerializer.Serialize(new { type = "user", message = new { content = echo } });
        var chat = TranscriptChat.For(vendor)!;
        return chat.ProjectWithInputs(line, 1, Received, chat.CreateContext("s", null));
    }

}
