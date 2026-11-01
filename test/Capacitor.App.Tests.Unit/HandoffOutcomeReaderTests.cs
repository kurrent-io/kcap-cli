using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class HandoffOutcomeReaderTests {
    sealed class Rig : IDisposable {
        public FakeDaemonClientService Daemon { get; } = new();
        public ScriptedLocalControlOps Ops { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public List<HandoffOutcomeDto> Seen { get; } = [];
        public bool Completed;
        public int Reads;
        public Queue<HandoffOutcomeDto?> Answers { get; } = new();
        IDisposable? _sub;

        public Rig(bool capable = true) {
            Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, capable ? ["input/1", HandoffWire.Capability] : ["input/1"]));
            Ops.ReadHandoffOutcome = _ => {
                Interlocked.Increment(ref Reads);
                return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);
            };
        }

        public void Watch(string id = "s2") =>
            _sub = new HandoffOutcomeReader(Daemon, Ops, Time).Watch(id).Subscribe(Seen.Add, () => Completed = true);

        /// Advances one poll gap, repeating until the next read lands: the delay is armed just after
        /// the previous read returns, so a single advance can fall before it.
        public async Task NextReadAsync() {
            var target = Volatile.Read(ref Reads) + 1;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Volatile.Read(ref Reads) < target && !Completed) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("the next read");
                Time.Advance(HandoffOutcomeReader.PollInterval);
                await Task.Delay(5);
            }
        }

        public void Dispose() => _sub?.Dispose();
    }

    /// Each call builds its own list, the shape a fresh deserialization has: an empty collection
    /// expression would share one array and hide a reference comparison.
    static HandoffOutcomeDto Outcome(string state) =>
        new("s1", "Old session", state, null, new List<HandoffOutcomeItemDto> { new("plan", "Plan A", "carried", null) });

    [Test]
    public async Task Running_polls_until_terminal() {
        using var rig = new Rig();
        rig.Answers.Enqueue(Outcome("running"));
        rig.Answers.Enqueue(Outcome("running"));
        rig.Answers.Enqueue(Outcome("done"));
        rig.Watch();

        await WaitUntilAsync(() => rig.Seen.Count == 1, what: "first answer");
        await Assert.That(rig.Completed).IsFalse();
        await rig.NextReadAsync();
        await rig.NextReadAsync();
        await WaitUntilAsync(() => rig.Completed, what: "completion on done");

        await Assert.That(rig.Seen.Select(o => o.State).ToArray()).IsEquivalentTo(new[] { "running", "done" });
        await Assert.That(rig.Reads).IsEqualTo(3);
    }

    [Test]
    public async Task An_outcome_stored_after_the_first_read_is_found() {
        using var rig = new Rig();
        rig.Answers.Enqueue(null);
        rig.Answers.Enqueue(Outcome("running"));
        rig.Answers.Enqueue(Outcome("done"));
        rig.Watch();

        await WaitUntilAsync(() => rig.Reads == 1, what: "first read");
        await Assert.That(rig.Seen).IsEmpty();
        await rig.NextReadAsync();
        await WaitUntilAsync(() => rig.Seen.Count == 1, what: "running found");
        await rig.NextReadAsync();
        await WaitUntilAsync(() => rig.Completed, what: "done");

        await Assert.That(rig.Seen.Select(o => o.State)).IsEquivalentTo(["running", "done"]);
    }

    [Test]
    public async Task A_session_with_no_handoff_stops_reading_after_the_window() {
        using var rig = new Rig();
        rig.Watch();

        await WaitUntilAsync(() => rig.Reads == 1, what: "first read");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!rig.Completed) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the window to lapse");
            rig.Time.Advance(HandoffOutcomeReader.PollInterval);
            await Task.Delay(2);
        }

        var reads = rig.Reads;
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);

        await Assert.That(rig.Seen).IsEmpty();
        await Assert.That(reads).IsGreaterThanOrEqualTo(2);
        await Assert.That(reads).IsLessThanOrEqualTo((int)(HandoffOutcomeReader.NullWindow / HandoffOutcomeReader.PollInterval) + 2);
        await Assert.That(rig.Reads).IsEqualTo(reads);
    }

    [Test]
    public async Task A_daemon_without_handoff_is_read_once_it_gains_it() {
        using var rig = new Rig(capable: false);
        rig.Answers.Enqueue(Outcome("done"));
        rig.Watch();

        rig.Time.Advance(TimeSpan.FromMinutes(3));
        await Task.Delay(50);
        await Assert.That(rig.Reads).IsEqualTo(0);
        await Assert.That(rig.Completed).IsFalse();

        rig.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/1", HandoffWire.Capability]));
        await WaitUntilAsync(() => rig.Completed, what: "completion on done");

        await Assert.That(rig.Seen.Select(o => o.State)).IsEquivalentTo(["done"]);
    }

    /// The daemon reruns an interrupted takeover when it resumes the successor.
    [Test]
    public async Task An_interrupted_outcome_is_followed_to_its_rerun() {
        using var rig = new Rig();
        rig.Answers.Enqueue(Outcome("interrupted"));
        rig.Answers.Enqueue(Outcome("running"));
        rig.Answers.Enqueue(Outcome("done"));
        rig.Watch();

        await WaitUntilAsync(() => rig.Seen.Count == 1, what: "interrupted");
        await Assert.That(rig.Completed).IsFalse();
        await rig.NextReadAsync();
        await rig.NextReadAsync();
        await WaitUntilAsync(() => rig.Completed, what: "completion on done");

        await Assert.That(rig.Seen.Select(o => o.State)).IsEquivalentTo(["interrupted", "running", "done"]);
    }
}
