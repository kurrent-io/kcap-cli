using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SessionImporterCaptureTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    [Arguments(true, null)]
    [Arguments(false, null)]
    [Arguments(false, "child")]
    public async Task UploadPreservesLargeFailedResultAndRedactsItsSecret(bool wholeSession, string? agentId) {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        using var tmp = new TempDir();
        const string secret = "ghp_0123456789abcdefghij";
        // Not 'x': it starts the xox* vendor prefixes, so every position becomes a regex candidate
        // and a loaded runner breaches the watcher's per-call deadline, losing the record.
        var raw = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"large-edit\",\"is_error\":true,\"content\":\""
                + new string('a', 100_000) + " " + secret + "\"}]}}";
        var path = tmp.CreateFile("source.jsonl", "\n" + raw + "\n");
        using var client = new HttpClient();

        await UploadAsync(client, path, wholeSession, agentId);

        var body = _server.LogEntries.Single().RequestMessage.Body!;
        await Assert.That(body.Contains(secret, StringComparison.Ordinal)).IsFalse();
        using var batch = JsonDocument.Parse(body);
        await Assert.That(batch.RootElement.GetProperty("line_numbers")[0].GetInt32()).IsEqualTo(1);
        using var line = JsonDocument.Parse(batch.RootElement.GetProperty("lines")[0].GetString()!);
        var result = line.RootElement.GetProperty("message").GetProperty("content")[0];
        await Assert.That(result.GetProperty("tool_use_id").GetString()).IsEqualTo("large-edit");
        await Assert.That(result.GetProperty("is_error").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("content").GetString()).Contains("[REDACTED]");
    }

    [Test]
    [Arguments(true, null)]
    [Arguments(false, null)]
    [Arguments(false, "child")]
    public async Task UploadSendsNumberedLossMarkerInsteadOfSkippingOversizedLine(bool wholeSession, string? agentId) {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        using var tmp = new TempDir();
        var raw = "{\"content\":\"" + new string('x', 4 * 1024 * 1024) + "\"}";
        var path = tmp.CreateFile("source.jsonl", "{}\n\n" + raw + "\n{}\n");
        using var client = new HttpClient();

        await UploadAsync(client, path, wholeSession, agentId);

        using var batch = JsonDocument.Parse(_server.LogEntries.Single().RequestMessage.Body!);
        var lines = batch.RootElement.GetProperty("lines");
        await Assert.That(lines.GetArrayLength()).IsEqualTo(3);
        await Assert.That(batch.RootElement.GetProperty("line_numbers").EnumerateArray().Select(x => x.GetInt32()).ToArray())
            .IsEquivalentTo(new[] { 0, 2, 3 });
        using var marker = JsonDocument.Parse(lines[1].GetString()!);
        await Assert.That(marker.RootElement.GetProperty("type").GetString()).IsEqualTo("kcap_capture_loss");
        await Assert.That(marker.RootElement.GetProperty("reason").GetString()).IsEqualTo("input_limit");
        await Assert.That(marker.RootElement.GetProperty("input_utf16_length").GetInt32()).IsEqualTo(raw.Length);
    }

    async Task UploadAsync(HttpClient client, string path, bool wholeSession, string? agentId) {
        if (wholeSession) {
            await SessionImporter.ImportSessionAsync(client, _server.Url!, path, "capture-session",
                new SessionMetadata { Cwd = "/repo" }, encodedCwd: null, time: TimeProvider.System);
        } else {
            await SessionImporter.SendTranscriptBatches(client, _server.Url!, "capture-session", path,
                agentId, startLine: 0, time: TimeProvider.System);
        }
    }
}
