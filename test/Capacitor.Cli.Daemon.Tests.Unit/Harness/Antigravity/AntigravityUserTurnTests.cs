using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;

/// <summary>
/// The daemon-synthesized <c>user_message</c> the single turn worker emits ahead of a turn's own
/// output (ACP's <c>session/prompt</c> never round-trips through <c>session/update</c>, so there is
/// no natural agent-sourced envelope for the prompt itself) — its ordering, its clock reading, and
/// its journal fidelity.
/// </summary>
public class AntigravityUserTurnTests {
    static readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.Zero));

    static async Task<List<AcpEventEnvelope>> Drain(AntigravityHostedAgentRuntime rt, int count) {
        var list = new List<AcpEventEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (list.Count < count) list.Add(await rt.Envelopes.ReadAsync(cts.Token));
        return list;
    }

    [Test]
    public async Task One_user_message_per_admitted_turn_ordered_before_that_turns_output_with_the_injected_clock() {
        await using var rt = AntigravityRuntimeFakes.FakeRuntime(time: Time);
        await rt.SendUserInputAsync("first");
        await rt.SendUserInputAsync("second");

        // Turn 1: user("first"), session_started (init's own envelope, emitted once ever). Turn 2's
        // init never re-emits session_started, so it contributes only its own user_message.
        var envelopes = await Drain(rt, 3);
        var users = envelopes.Where(e => e.Kind == AcpEventKind.UserMessage).ToList();
        await Assert.That(users.Select(u => u.Text!)).IsEquivalentTo(new[] { "first", "second" }, CollectionOrdering.Matching);
        await Assert.That(users.All(u => u.TimestampIso == "2026-09-09T10:00:00.0000000+00:00")).IsTrue();
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        var secondIndex = envelopes.FindIndex(e => e.Kind == AcpEventKind.UserMessage && e.Text == "second");
        await Assert.That(envelopes.Take(secondIndex).Count(e => e.Kind != AcpEventKind.UserMessage)).IsGreaterThan(0);
    }

    [Test]
    public async Task A_refused_turn_emits_only_the_not_delivered_note() {
        await using var rt = AntigravityRuntimeFakes.FakeRuntime(FakeTurn.NeverEnds, queueCap: 1, time: Time);
        await rt.SendUserInputAsync("first");

        // Turn 1 is genuinely executing (its init has been read, freeing the capacity-1 pending-turns
        // slot) before "queued" is sent — otherwise "queued" races the worker's own dequeue and can be
        // rejected instead of queued.
        await rt.WaitForConversationIdAsync(CancellationToken.None);

        await rt.SendUserInputAsync("queued");
        await Assert.That(async () => await rt.SendUserInputAsync("refused")).Throws<Exception>();
        await Task.Delay(100);
        var seen = new List<AcpEventEnvelope>();
        while (rt.Envelopes.TryRead(out var e)) seen.Add(e);
        await Assert.That(seen.Count(e => e.Kind == AcpEventKind.UserMessage && e.Text == "refused")).IsEqualTo(0);
        await Assert.That(seen.Any(e => e.Kind == AcpEventKind.SystemNote && e.Text!.Contains("not delivered"))).IsTrue();
    }

    [Test]
    public async Task Journal_matches_channel_order_under_worker_and_queue_full_notice_writers() {
        using var tmp = new TempDir();
        // The stock completion grace bounds the writer against a hung disk, which turns an assertion
        // over fifty fsynced appends into a throughput race the suite's own load can lose.
        var journal = new TranscriptJournal(
            tmp.PathTo("journal.jsonl"), NullLogger.Instance, completeGrace: TimeSpan.FromMinutes(1));
        journal.Open("/w", null);
        await using var rt = AntigravityRuntimeFakes.FakeRuntime(FakeTurn.NeverEnds, queueCap: 1, time: Time, journal: journal);
        await rt.SendUserInputAsync("first");
        await rt.WaitForConversationIdAsync(CancellationToken.None);
        await rt.SendUserInputAsync("queued");
        const int refused = 50;
        var refusals = Task.Run(async () => { for (var i = 0; i < refused; i++) { try { await rt.SendUserInputAsync($"r{i}"); } catch { } } });
        await refusals;

        // user("first"), session_started, then one note per refusal. Counted, not waited for: a loaded
        // runner emits the tail of the notes past any fixed delay this test could pick.
        var drained = await Drain(rt, 2 + refused);
        await Assert.That(await journal.CompleteAsync()).IsTrue()
            .Because("an abandoned writer would surface below as a whole-journal diff instead");

        var journaled = JournalFiles.ReadLines(journal.Path).Skip(1).Select(l => { EnvelopeJournalFormat.TryRead(l, out var e); return (e.Kind, e.Text); });
        await Assert.That(journaled).IsEquivalentTo(drained.Select(e => (e.Kind, e.Text)), CollectionOrdering.Matching);
    }
}
