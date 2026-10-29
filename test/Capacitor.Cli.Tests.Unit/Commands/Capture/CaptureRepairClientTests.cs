using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Commands.Capture;
using Capacitor.Cli.Commands.Capture.Wire;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Capture;

public class CaptureRepairClientTests {
    [Test]
    public async Task Retries_a_lost_ack_with_identical_bytes_digest_and_ordinal() {
        using var server = WireMockServer.Start();
        var rid = Guid.NewGuid().ToString("N");
        server.Given(Request.Create().WithPath("/repairs").UsingPost()).RespondWith(Response.Create().WithStatusCode(202)
            .WithHeader("Content-Type", "application/json").WithBody($$"""{"repair_id":"{{rid}}","phase":"preparing"}"""));
        server.Given(Request.Create().WithPath($"/repairs/{rid}/batches").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        using var http = new HttpClient(new LostRepairAcknowledgmentHandler());
        var client = new CaptureRepairClient(http, server.Url + "/repairs", TimeProvider.System);
        var source = new CaptureRepairSourceRequest("root", null, "claude");
        await client.PrepareAsync([source], false, default);
        await client.AddLineAsync(source, new(0, "{}", 2), default);
        var end = await client.EndSourceAsync(source, 0, default);
        var uploads = server.LogEntries.Where(e => e.RequestMessage.Path!.EndsWith("/batches", StringComparison.Ordinal)).ToArray();
        await Assert.That(uploads.Length).IsEqualTo(2);
        var first = uploads[0].RequestMessage.Body!;
        await Assert.That(uploads[1].RequestMessage.Body).IsEqualTo(first);
        await Assert.That(client.LastBatchDigest).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(first))));
        await Assert.That(end.LastBatchOrdinal).IsEqualTo(0);
        using var body = JsonDocument.Parse(first);
        await Assert.That(body.RootElement.GetProperty("batch_ordinal").GetInt32()).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Batches_by_actual_escaped_wire_bytes_and_line_count(bool multibyte) {
        using var server = WireMockServer.Start();
        var rid = Guid.NewGuid().ToString("N");
        server.Given(Request.Create().WithPath("/repairs").UsingPost()).RespondWith(Response.Create().WithStatusCode(202)
            .WithHeader("Content-Type", "application/json").WithBody($$"""{"repair_id":"{{rid}}","phase":"preparing"}"""));
        server.Given(Request.Create().WithPath($"/repairs/{rid}/batches").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        using var http = new HttpClient();
        var client = new CaptureRepairClient(http, server.Url + "/repairs", TimeProvider.System);
        var source = new CaptureRepairSourceRequest("root", null, "claude");
        await client.PrepareAsync([source], true, default);
        var count = multibyte ? 2 : 101;
        var text = multibyte ? "{\"text\":\"" + new string('漢', 500000) + "\"}" : "{}";
        for (var i = 0; i < count; i++) await client.AddLineAsync(source, new(i, text, text.Length), default);
        var end = await client.EndSourceAsync(source, count - 1, default);
        var uploads = server.LogEntries.Where(e => e.RequestMessage.Path!.EndsWith("/batches", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(uploads.Length).IsEqualTo(2);
        var numbers = new List<int>();
        foreach (var upload in uploads) {
            var bytes = Encoding.UTF8.GetByteCount(upload.RequestMessage.Body!);
            await Assert.That(bytes <= CaptureRepairClient.MaxBatchBytes).IsTrue();
            using var json = JsonDocument.Parse(upload.RequestMessage.Body!);
            var lines = json.RootElement.GetProperty("lines");
            await Assert.That(lines.GetArrayLength() <= 100).IsTrue();
            numbers.AddRange(lines.EnumerateArray().Select(l => l.GetProperty("number").GetInt32()));
        }
        await Assert.That(numbers.ToArray()).IsEquivalentTo(Enumerable.Range(0, count).ToArray());
        await Assert.That(end.LastBatchOrdinal).IsEqualTo(1);
    }

    [Test]
    public async Task Refuses_a_single_record_that_exceeds_the_encoded_request_limit() {
        using var http = new HttpClient();
        var client = new CaptureRepairClient(http, "https://unused.invalid/repairs", TimeProvider.System);
        var source = new CaptureRepairSourceRequest("root", null, "claude");
        var text = new string('漢', 800000);
        await Assert.That(async () => await client.AddLineAsync(source, new(0, text, text.Length), default)).Throws<IOException>();
    }
}
