using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

public class HookActivityTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly DateTimeOffset Start = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task An_agent_that_never_ran_a_hook_has_no_last_event() {
        var activity = new HookActivity(new ConfigRoot(Tmp.Path), new FakeTimeProvider(Start));

        await Assert.That(activity.LastEvent("claude")).IsNull();
    }

    /// <summary>Hooks fire many times a minute, so a second stamp inside the interval must not write;
    /// once the interval passes the time moves forward.</summary>
    [Test]
    public async Task Stamps_inside_a_minute_keep_the_first_time_and_later_ones_move_it() {
        var time     = new FakeTimeProvider(Start);
        var activity = new HookActivity(new ConfigRoot(Tmp.Path), time);

        activity.Stamp("codex");
        time.Advance(TimeSpan.FromSeconds(30));
        activity.Stamp("codex");

        await Assert.That(activity.LastEvent("codex")).IsEqualTo(Start);

        time.Advance(TimeSpan.FromMinutes(2));
        activity.Stamp("codex");

        await Assert.That(activity.LastEvent("codex")).IsEqualTo(Start.AddSeconds(150));
        await Assert.That(activity.LastEvent("claude")).IsNull();
    }

    /// <summary>A hook must not fail because the stamp could not be written.</summary>
    [Test]
    public async Task A_stamp_that_cannot_be_written_does_not_throw() {
        var blocker  = Tmp.CreateFile("hook-activity", "");
        var activity = new HookActivity(new ConfigRoot(Path.GetDirectoryName(blocker)!), new FakeTimeProvider(Start));

        activity.Stamp("claude");

        await Assert.That(activity.LastEvent("claude")).IsNull();
    }
}
