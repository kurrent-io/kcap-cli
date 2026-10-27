namespace Capacitor.Cli.Core.Tests.Unit;

public class EnvelopeJournalProjectionTests {
    static readonly IChatTranscriptProjection Sut = TranscriptChat.Journal;

    [Test]
    [Arguments(AcpEventKind.UserMessage)]
    [Arguments(AcpEventKind.AssistantText)]
    [Arguments(AcpEventKind.SystemNote)]
    [Arguments(AcpEventKind.ToolCall)]
    [Arguments(AcpEventKind.Usage)]
    [Arguments("future_kind")]
    public async Task Every_kind_passes_through_as_one_envelope(string kind) {
        var line = EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: kind, Text: "t", ToolCallId: "c1"));
        var context = Sut.CreateContext("s", "a1");
        var result = Sut.Project(line, 1, DateTimeOffset.UnixEpoch, context);
        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(result[0].Kind).IsEqualTo(kind);
    }

    [Test]
    public async Task Malformed_line_throws_so_the_tab_logs_it_once() {
        var context = Sut.CreateContext("s", null);
        await Assert.That(() => Sut.Project("{}", 1, DateTimeOffset.UnixEpoch, context)).Throws<FormatException>();
    }

    [Test]
    public async Task Context_is_stateless_and_reusable() {
        var context = Sut.CreateContext("s", null);
        context.BeginBatch();
        var line = EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "x"));
        await Assert.That(Sut.Project(line, 1, DateTimeOffset.UnixEpoch, context)).Count().IsEqualTo(1);
        await Assert.That(Sut.Project(line, 2, DateTimeOffset.UnixEpoch, context)).Count().IsEqualTo(1);
    }

    /// A hosted ACP agent's subagents reach the local lane only through these journal records.
    [Test]
    public async Task A_journaled_subagent_start_and_end_start_and_finish_its_row() {
        var context = Sut.CreateContext("s", null);
        var start = Sut.ProjectWithInputs(EnvelopeJournalFormat.Write(new AcpEventEnvelope(
            Kind: AcpEventKind.SubagentStarted, AgentId: "a1", AgentType: "explorer", TimestampIso: "2026-10-21T09:00:00Z")), 1, DateTimeOffset.UnixEpoch, context);
        var end = Sut.ProjectWithInputs(EnvelopeJournalFormat.Write(new AcpEventEnvelope(
            Kind: AcpEventKind.SubagentCompleted, AgentId: "a1", TimestampIso: "2026-10-21T09:05:00Z")), 2, DateTimeOffset.UnixEpoch, context);

        var started  = (RunSignal.Started)start.Runs[0];
        var finished = (RunSignal.Finished)end.Runs.Single();
        await Assert.That(started.Name).IsEqualTo("explorer");
        await Assert.That(started.At).IsEqualTo(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
        await Assert.That(((RunSignal.Identified)start.Runs[1]).AgentId).IsEqualTo("a1");
        await Assert.That(finished.AgentId).IsEqualTo("a1");
        await Assert.That(finished.At).IsEqualTo(new DateTimeOffset(2026, 10, 21, 9, 5, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Vendor_registry_is_unchanged() {
        await Assert.That(TranscriptChat.For("claude")).IsNotNull();
        await Assert.That(TranscriptChat.For("gemini")).IsNull();
        IChatTranscriptProjection asInterface = TranscriptChat.For("codex")!;
        await Assert.That(asInterface).IsNotNull();
    }
}
