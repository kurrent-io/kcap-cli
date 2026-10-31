using System.Net.Sockets;
using System.Text;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// What <c>kcap status</c> prints for a server that gave no answer, and how its lines survive a stdout
/// that cannot carry the status marks or a terminal narrower than the Hooks line.
/// </summary>
public class StatusServerLineTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Server = "http://127.0.0.1:9";

    [Test]
    public async Task A_refused_probe_is_classified_as_refused() {
        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (127.0.0.1:9)",
            new SocketException((int)SocketError.ConnectionRefused));

        var reach = await StatusCommand.ProbeServerAsync(
            new RecordingCapacitorHttpClient(new FaultingHandler(refused)), Server, Config.Root, TimeProvider.System);

        await Assert.That(reach.Reachable).IsFalse();
        await Assert.That(reach.Failure).IsEqualTo(ConnectionFailure.Refused);
        await Assert.That(StatusCommand.FormatReachability(reach)).IsEqualTo("✗ unreachable (connection refused)");
        await Assert.That(StatusCommand.UnreachableRemedy(reach)).IsEqualTo("Start or restart the server, or retry once it is running.");
    }

    /// <summary>A failure status is an answer: the server is up, so the start-the-server advice does
    /// not apply.</summary>
    [Test]
    public async Task Only_a_server_that_gave_no_answer_gets_the_recovery_line() {
        await Assert.That(StatusCommand.UnreachableRemedy(new StatusCommand.ServerReach(Server, true, null))).IsNull();
        await Assert.That(StatusCommand.UnreachableRemedy(new StatusCommand.ServerReach(Server, false, 503))).IsNull();
        await Assert.That(StatusCommand.UnreachableRemedy(new StatusCommand.ServerReach(null, false, null))).IsNull();
        await Assert.That(StatusCommand.UnreachableRemedy(new StatusCommand.ServerReach(Server, false, null))).IsNotNull();
    }

    [Test]
    public async Task Only_a_unicode_stdout_gets_the_marks() {
        await Assert.That(StatusGlyphs.Encodable(new UTF8Encoding(false))).IsTrue();
        await Assert.That(StatusGlyphs.Encodable(Encoding.Unicode)).IsTrue();
        await Assert.That(StatusGlyphs.Encodable(Encoding.ASCII)).IsFalse();
        await Assert.That(StatusGlyphs.Encodable(Encoding.Latin1)).IsFalse();
    }

    /// <summary>Encoded as "?", a wired and an unwired harness read the same; the stand-ins keep them apart.</summary>
    [Test]
    public async Task The_plain_hooks_line_tells_wired_from_unwired_in_ascii() {
        var line  = StatusCommand.BuildHooksStatusLine([(HarnessId.Claude, true), (HarnessId.Codex, false)]);
        var plain = StatusGlyphs.Plain(line);

        await Assert.That(plain).IsEqualTo("Claude ok  Codex x");
        await Assert.That(Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(plain))).IsEqualTo(plain);
    }

    [Test]
    public async Task Every_mark_status_prints_has_a_plain_stand_in() {
        var plain = StatusGlyphs.Plain("✓ ✗ ⚠ — …");

        await Assert.That(plain.All(char.IsAscii)).IsTrue();
    }

    [Test]
    public async Task A_hooks_line_wider_than_the_terminal_breaks_between_entries_under_its_label() {
        var line = StatusCommand.BuildHooksStatusLine(HarnessRegistry.Identities.Select(i => (i.Id, true)));
        var rows = StatusCommand.WrapHooksLine(line, 60).Split(Environment.NewLine);

        await Assert.That(rows.Length).IsGreaterThan(1);
        await Assert.That(rows.All(r => "  Hooks:   ".Length + r.TrimStart().Length < 60)).IsTrue();
        await Assert.That(rows.Skip(1).All(r => r.StartsWith(new string(' ', 11), StringComparison.Ordinal))).IsTrue();
        await Assert.That(string.Join("  ", rows.Select(r => r.Trim()))).IsEqualTo(line);
    }

    [Test]
    public async Task A_line_that_fits_or_has_no_terminal_stays_whole() {
        var line = StatusCommand.BuildHooksStatusLine(HarnessRegistry.Identities.Select(i => (i.Id, true)));

        await Assert.That(StatusCommand.WrapHooksLine(line, null)).IsEqualTo(line);
        await Assert.That(StatusCommand.WrapHooksLine("Claude ✓", 80)).IsEqualTo("Claude ✓");
    }

    sealed class FaultingHandler(Exception fault) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(fault);
    }
}
