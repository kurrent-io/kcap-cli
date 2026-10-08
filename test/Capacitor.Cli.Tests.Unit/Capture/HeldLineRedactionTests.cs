using System.Text.Json;
using Capacitor.Cli.Capture;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Capture;

public class HeldLineRedactionTests {
    const string Sid    = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Secret = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";

    static readonly string[] Raw = ["{\"a\":1}", $"{{\"text\":\"key {Secret}\"}}", "{\"c\":3}"];
    static readonly int[] Numbers = [4, 5, 7];

    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    readonly SwitchableRedactionClock _clock = new();
    readonly List<(RedactionLossReason Reason, int Count)> _losses = [];

    HeldLineStore Store => new(Tmp.PathTo("held.json"), Sid, null);

    HeldLineRedaction NewRedaction() => new(Store, _time, _clock, _ => { });

    CapturedLines Capture(HeldLineRedaction redaction, string[]? raw = null, int[]? numbers = null) =>
        redaction.Capture(raw ?? Raw, numbers ?? Numbers, (reason, count) => _losses.Add((reason, count)));

    static async Task<CapturedLines> CaptureUntil(Func<CapturedLines> capture, Func<CapturedLines, bool> done) {
        for (var i = 0; i < 500; i++) {
            var captured = capture();
            if (done(captured)) return captured;
            await Task.Delay(10);
        }

        throw new TimeoutException("the held line never settled");
    }

    [Test]
    public async Task A_budget_failure_holds_the_source_instead_of_marking_the_line() {
        var captured = Capture(NewRedaction(), [Raw[1], Raw[2]], [5, 7]);

        await Assert.That(captured.Consumed).IsEqualTo(0);
        await Assert.That(captured.Lines.Count).IsEqualTo(0);
        await Assert.That(_losses.Count).IsEqualTo(0);
        await Assert.That(Store.Load()!.LineNumber).IsEqualTo(5);
    }

    [Test]
    public async Task Lines_before_a_held_line_pass_and_lines_after_it_wait() {
        _clock.Exhausted = false;
        Store.Save(new HeldLine {
            SessionId = Sid, LineNumber = 5, LineSha256 = Sha256(Raw[1]), Reason = "record_budget",
            Attempts = 1, NextAttemptAt = _time.GetUtcNow().AddMinutes(1)
        });

        var captured = Capture(NewRedaction());

        await Assert.That(captured.Consumed).IsEqualTo(1);
        await Assert.That(captured.Lines.Single()).IsEqualTo(Raw[0]);
    }

    static string Sha256(string raw) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));

    [Test]
    public async Task A_later_retry_delivers_the_redacted_line_in_place() {
        var redaction = NewRedaction();
        Capture(redaction, [Raw[1], Raw[2]], [5, 7]);
        _clock.Exhausted = false;

        // The attempt already running may have read the exhausted clock; its successor will not.
        var captured = await CaptureUntil(() => {
            _time.Advance(TimeSpan.FromMinutes(5));
            return Capture(redaction, [Raw[1], Raw[2]], [5, 7]);
        }, c => c.Consumed == 2);

        await Assert.That(captured.Lines[0]).Contains(SecretRedactor.RedactedMarker);
        await Assert.That(captured.Lines[0]).DoesNotContain(Secret);
        await Assert.That(captured.Lines[1]).IsEqualTo(Raw[2]);
        await Assert.That(redaction.Held).IsNull();
        await Assert.That(File.Exists(Store.FilePath)).IsFalse();
    }

    [Test]
    public async Task A_failed_retry_is_rescheduled_with_backoff_and_never_marked() {
        var redaction = NewRedaction();
        Capture(redaction, [Raw[1]], [5]);

        await CaptureUntil(() => Capture(redaction, [Raw[1]], [5]), _ => redaction.Held!.Attempts == 1);
        var held = Store.Load()!;

        await Assert.That(held.NextAttemptAt).IsEqualTo(_time.GetUtcNow() + HeldLineRedaction.DelayAfter(1));
        _clock.Exhausted = false;
        await Task.Delay(50);
        await Assert.That(Capture(redaction, [Raw[1]], [5]).Consumed).IsEqualTo(0);

        _time.Advance(HeldLineRedaction.DelayAfter(1));
        var captured = await CaptureUntil(() => Capture(redaction, [Raw[1]], [5]), c => c.Consumed == 1);

        await Assert.That(captured.Lines.Single()).DoesNotContain(Secret);
        await Assert.That(_losses.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_restarted_watcher_resumes_the_held_line_from_disk() {
        var before = NewRedaction();
        Capture(before, [Raw[1]], [5]);
        await CaptureUntil(() => Capture(before, [Raw[1]], [5]), _ => before.Held!.Attempts == 1);

        var after = NewRedaction();
        await Assert.That(after.Held!.LineNumber).IsEqualTo(5);
        await Assert.That(after.Held!.Attempts).IsEqualTo(1);

        _clock.Exhausted = false;
        _time.Advance(HeldLineRedaction.DelayAfter(1));
        var captured = await CaptureUntil(() => Capture(after, [Raw[1], Raw[2]], [5, 7]), c => c.Consumed == 2);

        await Assert.That(captured.Lines[0]).DoesNotContain(Secret);
        await Assert.That(after.Held).IsNull();
    }

    [Test]
    public async Task A_held_line_the_server_already_has_is_released() {
        var before = NewRedaction();
        Capture(before, [Raw[1]], [5]);
        _clock.Exhausted = false;

        var after = NewRedaction();
        var captured = Capture(after, [Raw[2]], [7]);

        await Assert.That(captured.Consumed).IsEqualTo(1);
        await Assert.That(after.Held).IsNull();
        await Assert.That(File.Exists(Store.FilePath)).IsFalse();
    }

    [Test]
    public async Task A_rewritten_held_line_is_redacted_afresh() {
        var redaction = NewRedaction();
        Capture(redaction, [Raw[1]], [5]);
        _clock.Exhausted = false;

        var captured = Capture(redaction, [Raw[0]], [5]);

        await Assert.That(captured.Lines.Single()).IsEqualTo(Raw[0]);
        await Assert.That(redaction.Held).IsNull();
    }

    [Test]
    [Arguments("not json", "malformed_input")]
    [Arguments("{\"unterminated\":", "malformed_input")]
    public async Task Reasons_a_retry_cannot_fix_still_become_markers(string raw, string reason) {
        _clock.Exhausted = false;

        var captured = Capture(NewRedaction(), [raw], [3]);

        using var marker = JsonDocument.Parse(captured.Lines.Single());
        await Assert.That(marker.RootElement.GetProperty("type").GetString()).IsEqualTo("kcap_capture_loss");
        await Assert.That(marker.RootElement.GetProperty("reason").GetString()).IsEqualTo(reason);
        await Assert.That(Store.Load()).IsNull();
    }

    [Test]
    public async Task An_oversized_line_is_marked_not_held() {
        var raw = "{\"content\":\"" + new string('x', SecretRedactor.MaxRecordBytes) + "\"}";

        var captured = Capture(NewRedaction(), [raw], [0]);

        await Assert.That(captured.Consumed).IsEqualTo(1);
        await Assert.That(captured.Lines.Single()).Contains("input_limit");
        await Assert.That(_losses.Single().Reason).IsEqualTo(RedactionLossReason.InputLimit);
    }

    [Test]
    public async Task Budgets_and_delays_grow_to_their_caps() {
        await Assert.That(HeldLineRedaction.BudgetFor(1)).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(HeldLineRedaction.BudgetFor(3)).IsEqualTo(TimeSpan.FromSeconds(20));
        await Assert.That(HeldLineRedaction.BudgetFor(50)).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert.That(HeldLineRedaction.DelayAfter(2)).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(HeldLineRedaction.DelayAfter(5000)).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task A_new_hold_below_the_first_is_not_stalled_by_its_attempt() {
        var redaction = NewRedaction();
        Capture(redaction, [Raw[1]], [5]);
        Capture(redaction, [Raw[0]], [4]);
        _clock.Exhausted = false;

        var captured = await CaptureUntil(() => {
            _time.Advance(TimeSpan.FromMinutes(5));
            return Capture(redaction, [Raw[0]], [4]);
        }, c => c.Consumed == 1);

        await Assert.That(captured.Lines.Single()).IsEqualTo(Raw[0]);
    }

    [Test]
    public async Task A_retried_line_read_again_after_a_failed_send_is_not_held_again() {
        var redaction = NewRedaction();
        Capture(redaction, [Raw[1]], [5]);
        _clock.Exhausted = false;
        var delivered = await CaptureUntil(() => {
            _time.Advance(TimeSpan.FromMinutes(5));
            return Capture(redaction, [Raw[1]], [5]);
        }, c => c.Consumed == 1);
        _clock.Exhausted = true;

        var again = Capture(redaction, [Raw[1]], [5]);

        await Assert.That(again.Lines).IsEquivalentTo(delivered.Lines);
        await Assert.That(redaction.Held).IsNull();
    }

    [Test]
    public async Task A_shutdown_tail_reuses_a_finished_retry_of_the_same_line_only() {
        var redaction = NewRedaction();
        Capture(redaction, [Raw[1]], [5]);
        _clock.Exhausted = false;
        var delivered = await CaptureUntil(() => {
            _time.Advance(TimeSpan.FromMinutes(5));
            return Capture(redaction, [Raw[1]], [5]);
        }, c => c.Consumed == 1);
        _clock.Exhausted = true;
        var budget = new RedactionBudget(_clock, TimeSpan.FromSeconds(1));

        var reused  = TranscriptCapture.EncodeTail([Raw[1]], [5], budget, redaction.Redacted, (_, _) => { });
        var changed = TranscriptCapture.EncodeTail([Raw[0]], [5], budget, redaction.Redacted, (_, _) => { });

        await Assert.That(reused.Lines).IsEquivalentTo(delivered.Lines);
        await Assert.That(changed.Consumed).IsEqualTo(0);
    }
}
