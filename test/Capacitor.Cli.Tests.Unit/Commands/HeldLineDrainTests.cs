using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using Capacitor.Cli.Tests.Unit.Capture;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A line whose redaction runs out of time holds the drain at its own line number, so the server,
/// which drops a line numbered below one it already took, receives it before anything after it.
/// </summary>
public class HeldLineDrainTests {
    const string Secret = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";
    const string First  = "{\"a\":1}";
    const string Third  = "{\"c\":3}";
    const string Held   = "{\"text\":\"key " + Secret + "\"}";

    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    readonly SwitchableRedactionClock _clock = new() { Exhausted = false };

    WatchCommand Watch => field ??= new(Config.Root, Resolutions.None(Config.Root), TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), new FixedCredentialSource(), TestWatchers.For(Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient()), new GitProviderRouter(), _time, TestAccounts.None, Home) {
        RedactionClock = _clock
    };

    static readonly string Sid = Guid.NewGuid().ToString("N");

    static HubConnection UnconnectedHub() => new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();

    async Task<(IReadOnlyList<string> Lines, WatchState State)> Drain(string path, string? agentId, int from) {
        var state = new WatchState { LinesProcessed = from, ThresholdReached = agentId is not null };
        await using var hub = UnconnectedHub();
        var lines = await Watch.DrainNewLines(hub, Sid, path, agentId, state, "claude", CancellationToken.None);
        return (lines, state);
    }

    async Task<(IReadOnlyList<string> Lines, WatchState State)> DrainUntilDelivered(string path, string? agentId, int from) {
        for (var i = 0; i < 500; i++) {
            _time.Advance(TimeSpan.FromMinutes(5));
            var drained = await Drain(path, agentId, from);
            if (drained.Lines.Count > 0) return drained;
            await Task.Delay(10);
        }

        throw new TimeoutException("the held line never settled");
    }

    async Task<string> TranscriptWithHeldLine(string? agentId) {
        var path = Tmp.CreateFile("t.jsonl", First + "\n");
        await Drain(path, agentId, 0);
        await File.AppendAllTextAsync(path, Held + "\n" + Third + "\n");
        return path;
    }

    [Test]
    public async Task A_timed_out_line_holds_the_drain_without_a_marker() {
        var path = await TranscriptWithHeldLine(null);
        _clock.Exhausted = true;

        var (lines, state) = await Drain(path, null, 1);

        await Assert.That(lines.Count).IsEqualTo(0);
        await Assert.That(state.BufferedLines.Count).IsEqualTo(0);
        await Assert.That(state.LinesReadAhead).IsEqualTo(1);
        await Assert.That(Watch.HeldLineFor(Sid, null).Held!.LineNumber).IsEqualTo(1);
    }

    [Test]
    public async Task The_retried_line_keeps_its_source_line_number() {
        var path = await TranscriptWithHeldLine(null);
        _clock.Exhausted = true;
        await Drain(path, null, 1);
        _clock.Exhausted = false;

        var (lines, state) = await DrainUntilDelivered(path, null, 1);

        await Assert.That(state.BufferedLineNumbers).IsEquivalentTo([1, 2]);
        await Assert.That(lines[0]).Contains(SecretRedactor.RedactedMarker);
        await Assert.That(lines[1]).IsEqualTo(Third);
        await Assert.That(string.Concat(lines)).DoesNotContain(Secret);
        await Assert.That(string.Concat(lines)).DoesNotContain("kcap_capture_loss");
    }

    [Test]
    public async Task A_failed_send_of_the_retried_line_is_sent_again() {
        var path = await TranscriptWithHeldLine("agent-1");
        _clock.Exhausted = true;
        await Drain(path, "agent-1", 1);
        _clock.Exhausted = false;

        var (delivered, afterFailedSend) = await DrainUntilDelivered(path, "agent-1", 1);
        var (again, _) = await Drain(path, "agent-1", afterFailedSend.LinesProcessed);

        await Assert.That(afterFailedSend.LinesProcessed).IsEqualTo(1);
        await Assert.That(again).IsEquivalentTo(delivered);
        await Assert.That(string.Concat(again)).DoesNotContain(Secret);
    }

    [Test]
    public async Task Session_end_spools_the_held_line_redacted_and_releases_it() {
        var path = await TranscriptWithHeldLine(null);
        _clock.Exhausted = true;
        await Drain(path, null, 1);
        _clock.Exhausted = false;
        var spool = new TranscriptSpool(Tmp.PathTo("spool"), _time);

        var result = await Watch.SpoolUndeliveredTranscriptTailAsync(
            spool, path, Sid, null, "claude", 1, new CommitObservation.Uncovered(), CancellationToken.None);

        var replay = new List<string>();
        await spool.DrainAsync(Sid, body => { replay.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            () => false, CancellationToken.None);
        var batch = System.Text.Json.Nodes.JsonNode.Parse(replay.Single())!;
        await Assert.That(result).IsEqualTo(TranscriptSpool.AppendResult.Appended);
        await Assert.That(batch["line_numbers"]!.AsArray().Select(n => n!.GetValue<int>())).IsEquivalentTo([1, 2]);
        await Assert.That(replay.Single()).DoesNotContain(Secret);
        await Assert.That(Watch.HeldLineFor(Sid, null).Held).IsNull();
    }

    [Test]
    public async Task A_line_the_shutdown_budget_cannot_redact_flags_needs_import() {
        var path = await TranscriptWithHeldLine(null);
        _clock.Exhausted = true;
        await Drain(path, null, 1);
        var spool = new TranscriptSpool(Tmp.PathTo("spool"), _time);

        var result = await Watch.SpoolUndeliveredTranscriptTailAsync(
            spool, path, Sid, null, "claude", 1, new CommitObservation.Uncovered(), CancellationToken.None);

        await Assert.That(result).IsEqualTo(TranscriptSpool.AppendResult.MarkedNeedsImport);
        await Assert.That(spool.NeedsImport(Sid)).IsTrue();
        await Assert.That(spool.HasBacklog(Sid)).IsFalse();
        await Assert.That(Watch.HeldLineFor(Sid, null).Held).IsNull();
    }
}
