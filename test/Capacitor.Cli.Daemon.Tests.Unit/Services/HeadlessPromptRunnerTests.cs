using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Pins the daemon's headless lifetime: two runs at a time, a deadline that drops a queued request and bounds a
/// running one, cancellation by request id in either state, and a slot released whatever ends the run.</summary>
public sealed class HeadlessPromptRunnerTests : IDisposable {
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    readonly FakeTimeProvider        _time     = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    readonly CancellationTokenSource _shutdown = new();
    readonly FakeHarness             _claude   = new("claude");

    public void Dispose() => _shutdown.Dispose();

    HeadlessPromptRunner Runner(params FakeHarness[] harnesses) =>
        new(harnesses.Length == 0 ? [_claude] : harnesses, _time, NullLogger<HeadlessPromptRunner>.Instance, _shutdown.Token);

    RunHeadlessPromptArgs Args(string id, string harness = "claude", TimeSpan? left = null, string? schema = null) =>
        new(id, harness, "haiku", null, "system", "prompt", schema, 1, null, _time.GetUtcNow() + (left ?? TimeSpan.FromMinutes(10)));

    [Test]
    public async Task A_run_answers_with_the_harness_outcome() {
        var runner = Runner();
        var answer = runner.RunAsync(Args("r1"));
        (await _claude.NextCall()).Done.SetResult(new(new ClaudeCliResult("hi", null, 0, 0, 0, 0, null, 1), null));

        await Assert.That((await answer).Text).IsEqualTo("hi");
    }

    [Test]
    public async Task A_schema_answers_through_the_structured_json() {
        var runner = Runner();
        var answer = runner.RunAsync(Args("r1", schema: "{}"));
        (await _claude.NextCall()).Done.SetResult(new(new ClaudeCliResult("{}", null, 0, 0, 0, 0, null, 2), null));

        await Assert.That((await answer).StructuredJson).IsEqualTo("{}");
    }

    [Test]
    public async Task Unknown_harness_is_no_harness() {
        var response = await Runner().RunAsync(Args("r1", harness: "gemini"));

        await Assert.That((response.Started, response.NotStartedReason)).IsEqualTo((false, "no_harness"));
    }

    [Test]
    public async Task The_harness_token_matches_case_insensitively() {
        var runner = Runner();
        var answer = runner.RunAsync(Args("r1", harness: "Claude"));
        (await _claude.NextCall()).Done.SetResult(new(null, ClaudeCliFailure.ProcessFailure));

        await Assert.That((await answer).Started).IsTrue();
    }

    [Test]
    public async Task Arriving_past_its_deadline_never_starts() {
        var response = await Runner().RunAsync(Args("r1", left: TimeSpan.FromSeconds(-1)));

        await Assert.That((response.Started, response.NotStartedReason)).IsEqualTo((false, "deadline_passed"));
        await Assert.That(_claude.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Binary_gone_at_start_is_a_non_start() {
        _claude.Available = false;

        var response = await Runner().RunAsync(Args("r1"));

        await Assert.That((response.Started, response.NotStartedReason)).IsEqualTo((false, "binary_missing"));
        await Assert.That(_claude.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Run_timeout_is_the_time_left_to_the_deadline() {
        var runner = Runner();
        _ = runner.RunAsync(Args("r1", left: TimeSpan.FromMinutes(7)));

        await Assert.That((await _claude.NextCall()).Timeout).IsEqualTo(TimeSpan.FromMinutes(7));
    }

    [Test]
    public async Task Third_request_waits_for_a_slot() {
        var runner = Runner();
        _ = runner.RunAsync(Args("r1"));
        _ = runner.RunAsync(Args("r2"));
        var first = await _claude.NextCall();
        await _claude.NextCall();

        _ = runner.RunAsync(Args("r3"));
        await Assert.That(await _claude.TryNextCall(TimeSpan.FromMilliseconds(200))).IsNull();

        first.Done.SetResult(new(null, ClaudeCliFailure.ProcessFailure));
        await Assert.That((await _claude.NextCall()).Args.RequestId).IsEqualTo("r3");
    }

    [Test]
    public async Task Deadline_passing_while_queued_is_a_non_start() {
        var runner = Runner();
        _ = runner.RunAsync(Args("r1"));
        _ = runner.RunAsync(Args("r2"));
        await _claude.NextCall();
        await _claude.NextCall();

        var queued = runner.RunAsync(Args("r3", left: TimeSpan.FromMinutes(1)));
        _time.Advance(TimeSpan.FromMinutes(2));

        var response = await queued.WaitAsync(Wait);
        await Assert.That((response.Started, response.NotStartedReason)).IsEqualTo((false, "deadline_passed"));
        await Assert.That(_claude.CallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Cancel_queued_never_starts_it() {
        var runner = Runner();
        _ = runner.RunAsync(Args("r1"));
        _ = runner.RunAsync(Args("r2"));
        var first = await _claude.NextCall();
        await _claude.NextCall();

        var queued = runner.RunAsync(Args("r3"));
        await runner.CancelAsync(new("r3"));

        var response = await queued.WaitAsync(Wait);
        await Assert.That(response.Started).IsFalse();
        await Assert.That(response.NotStartedReason).IsNull();

        first.Done.SetResult(new(null, ClaudeCliFailure.ProcessFailure));
        await Assert.That(await _claude.TryNextCall(TimeSpan.FromMilliseconds(200))).IsNull();
    }

    [Test]
    public async Task Cancel_running_cancels_the_harness_token_and_frees_the_slot() {
        var runner  = Runner();
        var running = runner.RunAsync(Args("r1"));
        _ = runner.RunAsync(Args("r2"));
        var call = await _claude.NextCall();
        await _claude.NextCall();

        await runner.CancelAsync(new("r1"));

        var response = await running.WaitAsync(Wait);
        await Assert.That(call.Ct.IsCancellationRequested).IsTrue();
        await Assert.That((response.Started, response.FailureKind)).IsEqualTo((true, "process_failure"));

        _ = runner.RunAsync(Args("r3"));
        await Assert.That((await _claude.NextCall()).Args.RequestId).IsEqualTo("r3");
    }

    [Test]
    public async Task Cancel_for_an_unknown_id_is_a_no_op() {
        await Runner().CancelAsync(new("nobody"));
    }

    [Test]
    public async Task Shutdown_ends_a_queued_run() {
        var runner = Runner();
        _ = runner.RunAsync(Args("r1"));
        _ = runner.RunAsync(Args("r2"));
        await _claude.NextCall();
        await _claude.NextCall();
        var queued = runner.RunAsync(Args("r3"));

        await _shutdown.CancelAsync();

        await Assert.That((await queued.WaitAsync(Wait)).Started).IsFalse();
    }

    [Test]
    public async Task A_harness_that_throws_is_a_started_process_failure() {
        var runner = Runner();
        var answer = runner.RunAsync(Args("r1"));
        (await _claude.NextCall()).Done.SetException(new IOException("pipe"));

        var response = await answer;
        await Assert.That((response.Started, response.FailureKind)).IsEqualTo((true, "process_failure"));
    }

    [Test]
    public async Task Advertises_only_available_harnesses_in_order() {
        var codex = new FakeHarness("codex");
        var gone  = new FakeHarness("pi") { Available = false };

        await Assert.That(Runner(_claude, gone, codex).AdvertisedHarnesses()).IsEquivalentTo(["claude", "codex"]);
    }

    sealed record Call(RunHeadlessPromptArgs Args, TimeSpan Timeout, CancellationToken Ct, TaskCompletionSource<ClaudeCliOutcome> Done);

    sealed class FakeHarness(string harness) : IHeadlessHarness {
        readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        int                    _count;

        public bool   Available { get; set; } = true;
        public int    CallCount => Volatile.Read(ref _count);
        public string Harness   => harness;

        public bool IsAvailable() => Available;

        public Task<ClaudeCliOutcome> RunAsync(RunHeadlessPromptArgs args, TimeSpan timeout, CancellationToken ct) {
            var call = new Call(args, timeout, ct, new(TaskCreationOptions.RunContinuationsAsynchronously));
            ct.Register(() => call.Done.TrySetCanceled(ct));
            Interlocked.Increment(ref _count);
            _calls.Writer.TryWrite(call);

            return call.Done.Task;
        }

        public async Task<Call> NextCall() => await TryNextCall(Wait) ?? throw new TimeoutException("the harness was never called");

        public async Task<Call?> TryNextCall(TimeSpan within) {
            using var cts = new CancellationTokenSource(within);
            try {
                return await _calls.Reader.ReadAsync(cts.Token);
            } catch (OperationCanceledException) {
                return null;
            }
        }
    }
}
