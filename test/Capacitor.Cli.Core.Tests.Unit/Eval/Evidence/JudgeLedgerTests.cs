using System.Text;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>The ledger and run file round-trip every field; the last footer wins; a torn final line is ignored; and the
/// derived delivery sets are what coverage and cite expansion read.</summary>
public class JudgeLedgerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly EvidenceRunBudgets Budgets = new(48, 600_000, 65_536);
    static readonly DateTimeOffset T0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    static JudgeLedgerPage Page(int seq, string handle, string tool = "read_events", string args = "{}", bool hasNext = false, string? next = null) {
        const string text = "{\"page\":\"p1\",\"entries\":[{\"cite\":\"p1.1\",\"ref\":\"AgentSession-r@3\",\"text\":\"hé\"}]}";
        var start = Encoding.UTF8.GetByteCount(text[..text.IndexOf("{\"cite\"", StringComparison.Ordinal)]);
        return new(seq, handle, tool, args, "AgentSession-r", text,
            Revisions: [("AgentSession-r", 3, 5)], Turns: [("AgentSession-r", 1)], Bodies: [("AgentSession-r@4", "output", null)],
            Detail: [("AgentSession-r", 3, start, Encoding.UTF8.GetByteCount(text) - 2 - start)],
            Cites: new Dictionary<string, string> { [$"{handle}.1"] = "AgentSession-r@3" }, hasNext, next);
    }

    [Test]
    public async Task Every_line_kind_round_trips_and_the_last_footer_wins() {
        var path = Tmp.PathTo("q1.ledger.jsonl");
        using (var w = JudgeLedgerWriter.Create(path, new JudgeLedgerHeader("run", "q", "v1", Budgets, T0.AddMinutes(8), T0))) {
            w.Append(Page(1, "p1", hasNext: true, next: "cursor-1"));
            w.Append(new JudgeLedgerCall(2, "read_events", "{\"ref\":\"p0.1\"}", JudgeLedgerOutcomes.Executed, null, null, 100));
            w.Append(new JudgeLedgerFooter(1, 100, null, [], T0.AddMinutes(1)));
        }
        using (var w = JudgeLedgerWriter.OpenAppend(path)) {
            w.Append(Page(3, "p2", args: "{\"page\":\"p1\",\"next\":true}"));
            w.Append(new JudgeLedgerCall(4, "list_turns", "{}", JudgeLedgerOutcomes.NotExecuted, "tool_call_budget", null, 0));
            w.Append(new JudgeLedgerFooter(48, 200, "tool_call_budget", ["AgentSession-x"], T0.AddMinutes(2)));
        }

        var ledger = JudgeLedgerReader.Read(path);

        await Assert.That(ledger.Header!.QuestionId).IsEqualTo("q");
        await Assert.That(ledger.Header.SoftDeadline).IsEqualTo(T0.AddMinutes(8));
        await Assert.That(ledger.Pages.Count).IsEqualTo(2);
        await Assert.That(ledger.Pages[0]).IsEquivalentTo(Page(1, "p1", hasNext: true, next: "cursor-1"));
        await Assert.That(ledger.Pages[0].Bytes).IsEqualTo(Encoding.UTF8.GetByteCount(ledger.Pages[0].Text));
        await Assert.That(ledger.Calls.Select(c => c.Outcome)).IsEquivalentTo([JudgeLedgerOutcomes.Executed, JudgeLedgerOutcomes.NotExecuted]);
        await Assert.That(ledger.ToolCalls).IsEqualTo(48);
        await Assert.That(ledger.DeliveredBytes).IsEqualTo(200);
        await Assert.That(ledger.StopReason).IsEqualTo("tool_call_budget");
        await Assert.That(ledger.SourcesRefused).IsEquivalentTo(["AgentSession-x"]);
        await Assert.That(ledger.FollowedHandles).IsEquivalentTo(["p1"]);
        await Assert.That(ledger.DeliveredEvents).IsEquivalentTo([("AgentSession-r", 3L), ("AgentSession-r", 4L), ("AgentSession-r", 5L)]);
        await Assert.That(ledger.DeliveredTurns).IsEquivalentTo([("AgentSession-r", 1)]);
        await Assert.That(ledger.SourcesWithPage).IsEquivalentTo(["AgentSession-r"]);
    }

    [Test]
    public async Task A_torn_final_line_is_ignored_and_a_ledger_without_a_footer_counts_its_calls() {
        var path = Tmp.PathTo("q1.ledger.jsonl");
        using (var w = JudgeLedgerWriter.Create(path, new JudgeLedgerHeader("run", "q", "v1", Budgets, null, T0))) {
            w.Append(Page(1, "p1"));
            w.Append(new JudgeLedgerCall(2, "read_events", "{}", JudgeLedgerOutcomes.Executed, null, null, 10));
            w.Append(new JudgeLedgerCall(3, "read_events", "{}", JudgeLedgerOutcomes.Error, null, "Error: invalid_ref — bad", 0));
        }
        await File.AppendAllTextAsync(path, "{\"kind\":\"call\",\"seq\":4,\"to");

        var ledger = JudgeLedgerReader.Read(path);

        await Assert.That(ledger.Calls.Count).IsEqualTo(2);
        await Assert.That(ledger.Footer).IsNull();
        await Assert.That(ledger.ToolCalls).IsEqualTo(2);
        await Assert.That(ledger.DeliveredBytes).IsEqualTo(ledger.Pages[0].Bytes);
        await Assert.That(ledger.StopReason).IsNull();
    }

    [Test]
    public async Task The_run_file_round_trips_with_its_seeded_pages_and_is_owner_only() {
        await using var ctx = EvidenceRunContext.Create("run", Tmp.Path);
        var file = new EvidenceRunFile("run", "q", "r", "v1", new string('t', 6_000), T0.AddMinutes(30),
            [new EvidenceRunSource("AgentSession-r", "root", true, 0, 9, 3), new EvidenceRunSource("AgentSubsession-r-a", "subagent", false, 0, 0, null)],
            Budgets, T0.AddMinutes(8), ctx.LedgerFilePath(1), [Page(0, "o0", tool: "list_sources")]);

        ctx.WriteRunFile(1, file);
        var back = EvidenceRunFile.Read(ctx.RunFilePath(1));

        await Assert.That(back with { Sources = file.Sources, SeededPages = file.SeededPages }).IsEqualTo(file);
        await Assert.That(back.Sources).IsEquivalentTo(file.Sources);
        await Assert.That(back.SeededPages.Single()).IsEquivalentTo(file.SeededPages.Single());
        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(ctx.RunFilePath(1))).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(() => ctx.WriteRunFile(1, file)).Throws<IOException>();
    }
}
