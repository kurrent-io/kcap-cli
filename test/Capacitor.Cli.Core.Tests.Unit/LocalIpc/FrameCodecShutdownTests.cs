using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class FrameCodecShutdownTests {
    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, CancellationToken.None);
        ms.Position = 0;
        return (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
    }

    [Test]
    public async Task Shutdown_frame_round_trips_through_the_codec() {
        var dto = new ShutdownRequestDto("keep", "relaunch");
        var f = await RoundTrip(LocalFrame.Shutdown(dto));
        await Assert.That(f.Type).IsEqualTo(FrameType.Shutdown);
        await Assert.That(ShutdownIpc.Parse(f.Text)).IsEqualTo(dto);
    }

    [Test]
    public async Task Agent_resume_and_ack_frames_round_trip_their_text() {
        var resume = await RoundTrip(LocalFrame.AgentResume("agent-1"));
        await Assert.That(resume.Type).IsEqualTo(FrameType.AgentResume);
        await Assert.That(resume.Text).IsEqualTo("agent-1");

        var shutdownAck = await RoundTrip(LocalFrame.ShutdownAck("stopping"));
        await Assert.That(shutdownAck.Type).IsEqualTo(FrameType.ShutdownAck);
        await Assert.That(shutdownAck.Text).IsEqualTo("stopping");

        var resumeAck = await RoundTrip(LocalFrame.AgentResumeAck("resuming"));
        await Assert.That(resumeAck.Type).IsEqualTo(FrameType.AgentResumeAck);
        await Assert.That(resumeAck.Text).IsEqualTo("resuming");
    }

    [Test]
    [Arguments("""{"agents":"maybe","exit":"final"}""")]
    [Arguments("""{"agents":"keep","exit":"never"}""")]
    [Arguments("""{"agents":"keep"}""")]
    [Arguments("not json")]
    [Arguments("")]
    public async Task Parse_rejects_unknown_agents_or_exit_values(string text) {
        await Assert.That(ShutdownIpc.Parse(text)).IsNull();
    }

    [Test]
    public async Task Parse_accepts_every_allowed_combination() {
        foreach (var agents in new[] { "keep", "end" })
        foreach (var exit in new[] { "relaunch", "final" }) {
            var dto = ShutdownIpc.Parse($$"""{"agents":"{{agents}}","exit":"{{exit}}"}""");
            await Assert.That(dto).IsEqualTo(new ShutdownRequestDto(agents, exit));
        }
    }

    /// <summary>A request from a client that predates <c>when_idle</c> carries none and stops at once.</summary>
    [Test]
    public async Task When_idle_round_trips_and_reads_as_false_when_absent() {
        var dto = new ShutdownRequestDto("keep", "final", WhenIdle: true);

        await Assert.That(ShutdownIpc.Serialize(dto)).Contains("\"when_idle\":true");
        await Assert.That(ShutdownIpc.Parse(ShutdownIpc.Serialize(dto))).IsEqualTo(dto);
        await Assert.That(ShutdownIpc.Parse("""{"agents":"keep","exit":"final"}""")!.WhenIdle).IsFalse();
    }

    [Test]
    public async Task Numeric_values_are_pinned() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((int)FrameType.Shutdown).IsEqualTo(28);
        await Assert.That((int)FrameType.AgentResume).IsEqualTo(29);
        await Assert.That((int)FrameType.ShutdownAck).IsEqualTo(83);
        await Assert.That((int)FrameType.AgentResumeAck).IsEqualTo(84);
#pragma warning restore TUnitAssertions0005
    }
}
