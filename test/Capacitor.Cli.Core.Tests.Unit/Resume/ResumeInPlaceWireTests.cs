using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.Resume;

public class ResumeInPlaceWireTests {
    [Test]
    public async Task A_launch_command_reads_the_servers_resume_block() {
        const string json = """
            {"agent_id":"a1","resume_in_place":{"source_session_id":"s0","directory":"/w/r","reservation_token":"tok",
             "account_id":"acct","config_dir":"/Users/me/.claude"}}
            """;

        var command = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(command.ResumeInPlace).IsEqualTo(new Capacitor.Cli.Core.Resume.ResumeInPlaceLaunch("s0", "/w/r", "tok", "acct", "/Users/me/.claude"));
    }

    [Test]
    public async Task A_launch_command_without_the_block_reads_null() =>
        await Assert.That(JsonSerializer.Deserialize("""{"agent_id":"a1"}""", CapacitorJsonContext.Default.LaunchAgentCommand).ResumeInPlace).IsNull();

    [Test]
    public async Task DaemonConnect_advertises_in_place_resume() {
        var json = JsonSerializer.Serialize(new DaemonConnect("n", "p", [], 1, [], SupportsResumeInPlace: true), CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(json).Contains("\"supports_resume_in_place\":true");
    }

    [Test]
    public async Task The_probe_frame_round_trips_through_the_codec() {
        var frame = new LocalFrame(FrameType.ResumeProbe) { Text = """{"session_id":"s"}""" };
        using var stream = new MemoryStream();

        await FrameCodec.WriteAsync(stream, frame, CancellationToken.None);
        stream.Position = 0;

        var read = await FrameCodec.ReadAsync(stream, CancellationToken.None);
        await Assert.That((read!.Type, read.Text)).IsEqualTo((FrameType.ResumeProbe, frame.Text));
    }
}
