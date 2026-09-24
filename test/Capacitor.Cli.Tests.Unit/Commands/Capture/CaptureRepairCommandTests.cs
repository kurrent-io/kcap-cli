using System.Text.Json;
using Capacitor.Cli.Commands.Capture;
using Capacitor.Cli.Core.Harness;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Capture;

[NotInParallel]
public class CaptureRepairCommandTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Missing_capability_stops_before_local_discovery_or_upload() {
        using var server = WireMockServer.Start();
        var sid = Guid.NewGuid().ToString("N");
        server.Given(Request.Create().WithPath($"/api/sessions/{sid}/capture-repairs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        var source = new RepairImportSource();
        using var output = ConsoleOutput.StartFullCapture();
        var command = new CaptureRepairCommand(Resolutions.At(server.Url!, Config.Root), new FixedCapacitorHttpClient(), TimeProvider.System);
        var exit = await command.HandleAsync(sid, false, [source]);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(source.DiscoveryCount).IsEqualTo(0);
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Method == "POST")).IsFalse();
        await Assert.That(output.GetCapturedError()).Contains("upgrade");
    }
    [Test]
    [Arguments(false, 14)]
    [Arguments(true, 14)]
    [Arguments(false, 0)]
    [Arguments(true, 0)]
    public async Task Sends_redacted_root_and_child_coordinates_and_reports_remaining_gaps(bool dryRun, int gaps) {
        using var server = WireMockServer.Start();
        using var tmp = new TempDir();
        var sid = Guid.NewGuid().ToString("N");
        var rid = Guid.NewGuid().ToString("N");
        var path = $"/api/sessions/{sid}/capture-repairs";
        server.Given(Request.Create().WithPath(path).UsingGet()).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Content-Type", "application/json").WithBody("""{"protocol_version":1}"""));
        server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(202)
            .WithHeader("Content-Type", "application/json").WithBody($$"""{"repair_id":"{{rid}}","phase":"preparing"}"""));
        server.Given(Request.Create().WithPath($"{path}/{rid}/batches").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        var phase = dryRun ? "preview" : "validated";
        server.Given(Request.Create().WithPath($"{path}/{rid}/complete").UsingPost()).RespondWith(Response.Create().WithStatusCode(dryRun ? 200 : 202)
            .WithHeader("Content-Type", "application/json").WithBody(Report(rid, phase, 0, false, gaps)));
        server.Given(Request.Create().WithPath($"{path}/{rid}").UsingGet()).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Content-Type", "application/json").WithBody(Report(rid, "complete", 3, true, gaps)));
        var secret = "ghp_" + new string('a', 36);
        var raw = "{\"text\":\"漢🙂" + new string('x', 70000) + "\",\"token\":\"" + secret + "\"}";
        var transcript = tmp.CreateFile("root.jsonl", raw + "\n{}\n");
        tmp.CreateFile("root/subagents/agent-child.jsonl", "{}\n");
        var source = new RepairImportSource();
        source.Sessions = [new(sid, HarnessId.Claude, null, null, new Dictionary<string, object?> { ["FilePath"] = transcript })];
        using var output = ConsoleOutput.StartFullCapture();
        var command = new CaptureRepairCommand(Resolutions.At(server.Url!, Config.Root), new FixedCapacitorHttpClient(), TimeProvider.System);
        await Assert.That(await command.HandleAsync(sid, dryRun, [source])).IsEqualTo(gaps == 0 ? 0 : 2);
        var uploads = server.LogEntries.Where(e => e.RequestMessage.Path == $"{path}/{rid}/batches").ToArray();
        await Assert.That(uploads.Length).IsEqualTo(2);
        var root = uploads.Single(e => !e.RequestMessage.Body!.Contains("child"));
        using var batch = JsonDocument.Parse(root.RequestMessage.Body!);
        var lines = batch.RootElement.GetProperty("lines");
        await Assert.That(lines.GetArrayLength()).IsEqualTo(2);
        await Assert.That(lines[0].GetProperty("number").GetInt32()).IsEqualTo(0);
        await Assert.That(lines[0].GetProperty("original_utf16_length").GetInt32()).IsEqualTo(raw.Length);
        await Assert.That(root.RequestMessage.Body!).DoesNotContain(secret);
        using var prepared = JsonDocument.Parse(server.LogEntries.Single(e => e.RequestMessage.Path == path && e.RequestMessage.Method == "POST").RequestMessage.Body!);
        await Assert.That(prepared.RootElement.GetProperty("sources").GetArrayLength()).IsEqualTo(2);
        await Assert.That(prepared.RootElement.GetProperty("dry_run").GetBoolean()).IsEqualTo(dryRun);
        await Assert.That(output.GetCapturedOutput()).Contains($"{gaps} remaining gaps");
        await Assert.That(output.GetCapturedOutput()).Contains(dryRun ? "3 candidate records" : "3 restored records");
        await Assert.That(output.GetCapturedOutput()).DoesNotContain("fully repaired");
    }

    [Test]
    [Arguments(409, "session_active")]
    [Arguments(404, "source_not_found")]
    public async Task Active_session_and_foreign_child_refusals_do_not_upload(int status, string error) {
        using var server = WireMockServer.Start();
        using var tmp = new TempDir();
        var sid = Guid.NewGuid().ToString("N");
        var path = $"/api/sessions/{sid}/capture-repairs";
        server.Given(Request.Create().WithPath(path).UsingGet()).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Content-Type", "application/json").WithBody("""{"protocol_version":1}"""));
        server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(status)
            .WithHeader("Content-Type", "application/json").WithBody($$"""{"error":"{{error}}"}"""));
        var transcript = tmp.CreateFile("root.jsonl", "{}\n");
        tmp.CreateFile("root/subagents/agent-foreign.jsonl", "{}\n");
        var source = new RepairImportSource { Sessions = [new(sid, HarnessId.Claude, null, null,
            new Dictionary<string, object?> { ["FilePath"] = transcript })] };
        using var output = ConsoleOutput.StartFullCapture();
        var command = new CaptureRepairCommand(Resolutions.At(server.Url!, Config.Root), new FixedCapacitorHttpClient(), TimeProvider.System);
        await Assert.That(await command.HandleAsync(sid, false, [source])).IsEqualTo(1);
        await Assert.That(output.GetCapturedError()).Contains(error);
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Method == "POST")).IsEqualTo(1);
    }

    [Test]
    [Arguments("paused")]
    [Arguments("cancelled")]
    [Arguments("complete")]
    [Arguments("validated")]
    public async Task Reports_unfinished_accounting_without_claiming_success(string phase) {
        using var server = WireMockServer.Start();
        using var tmp = new TempDir();
        var sid = Guid.NewGuid().ToString("N");
        var rid = Guid.NewGuid().ToString("N");
        var path = $"/api/sessions/{sid}/capture-repairs";
        Configure(server, path, rid);
        server.Given(Request.Create().WithPath($"{path}/{rid}/complete").UsingPost()).RespondWith(Response.Create().WithStatusCode(202)
            .WithHeader("Content-Type", "application/json").WithBody(Report(rid, phase, 3, false)));
        var transcript = tmp.CreateFile("root.jsonl", "{}\n");
        var source = new RepairImportSource { Sessions = [new(sid, HarnessId.Claude, null, null,
            new Dictionary<string, object?> { ["FilePath"] = transcript })] };
        var clock = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromMinutes(3) };
        using var output = ConsoleOutput.StartFullCapture();
        var command = new CaptureRepairCommand(Resolutions.At(server.Url!, Config.Root), new FixedCapacitorHttpClient(), clock);
        await Assert.That(await command.HandleAsync(sid, false, [source])).IsEqualTo(1);
        await Assert.That(output.GetCapturedOutput()).Contains($"phase {phase}");
        await Assert.That(output.GetCapturedError()).Contains(rid);
    }

    [Test]
    public async Task Invalid_utf8_is_a_reported_refusal_without_completing_the_upload() {
        using var server = WireMockServer.Start();
        using var tmp = new TempDir();
        var sid = Guid.NewGuid().ToString("N");
        var rid = Guid.NewGuid().ToString("N");
        var path = $"/api/sessions/{sid}/capture-repairs";
        Configure(server, path, rid);
        var transcript = tmp.CreateFile("root.jsonl", "");
        await File.WriteAllBytesAsync(transcript, [0xFF, 0xFE, 0xFF]);
        var source = new RepairImportSource { Sessions = [new(sid, HarnessId.Claude, null, null,
            new Dictionary<string, object?> { ["FilePath"] = transcript })] };
        using var output = ConsoleOutput.StartFullCapture();
        var command = new CaptureRepairCommand(Resolutions.At(server.Url!, Config.Root), new FixedCapacitorHttpClient(), TimeProvider.System);
        await Assert.That(await command.HandleAsync(sid, false, [source])).IsEqualTo(1);
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path!.EndsWith("/complete", StringComparison.Ordinal))).IsFalse();
        await Assert.That(output.GetCapturedError()).Contains("UTF-8");
    }

    [Test]
    public async Task Codex_discovery_selects_descendant_own_ids_and_excludes_foreign_rollouts() {
        using var server = WireMockServer.Start();
        using var tmp = new TempDir();
        var sid = Guid.NewGuid().ToString("N");
        var child = Guid.NewGuid().ToString("N");
        var foreign = Guid.NewGuid().ToString("N");
        var rid = Guid.NewGuid().ToString("N");
        var path = $"/api/sessions/{sid}/capture-repairs";
        Configure(server, path, rid);
        server.Given(Request.Create().WithPath($"{path}/{rid}/complete").UsingPost()).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Content-Type", "application/json").WithBody(Report(rid, "preview", 0, false, 0)));
        var rootPath = tmp.CreateFile($"sessions/2026/01/01/rollout-2026-01-01T00-00-00-{Guid.Parse(sid):D}.jsonl",
            $$"""{"type":"session_meta","payload":{"id":"{{sid}}","source":"cli"} }""" + "\n");
        tmp.CreateFile($"sessions/2026/01/01/rollout-2026-01-01T00-00-01-{Guid.Parse(child):D}.jsonl",
            $$"""{"type":"session_meta","payload":{"id":"{{child}}","session_id":"{{sid}}","thread_source":"subagent","parent_thread_id":"{{sid}}"} }""" + "\n");
        tmp.CreateFile($"sessions/2026/01/01/rollout-2026-01-01T00-00-02-{Guid.Parse(foreign):D}.jsonl",
            $$"""{"type":"session_meta","payload":{"id":"{{foreign}}","thread_source":"subagent","parent_thread_id":"{{Guid.NewGuid():N}}"} }""" + "\n");
        var source = new RepairImportSource { Vendor = HarnessId.Codex, Sessions = [new(sid, HarnessId.Codex, null, null,
            new Dictionary<string, object?> { ["FilePath"] = rootPath })] };
        using var output = ConsoleOutput.StartFullCapture();
        var command = new CaptureRepairCommand(Resolutions.At(server.Url!, Config.Root), new FixedCapacitorHttpClient(), TimeProvider.System);
        await Assert.That(await command.HandleAsync(sid, true, [source])).IsEqualTo(0);
        var body = server.LogEntries.Single(e => e.RequestMessage.Path == path && e.RequestMessage.Method == "POST").RequestMessage.Body!;
        using var prepared = JsonDocument.Parse(body);
        var sources = prepared.RootElement.GetProperty("sources");
        await Assert.That(sources.GetArrayLength()).IsEqualTo(2);
        await Assert.That(sources[1].GetProperty("agent_id").GetString()).IsEqualTo(child);
        await Assert.That(body).DoesNotContain(foreign);
        await Assert.That(sources.EnumerateArray().All(s => s.GetProperty("vendor").GetString() == "codex")).IsTrue();
    }

    static void Configure(WireMockServer server, string path, string rid) {
        server.Given(Request.Create().WithPath(path).UsingGet()).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Content-Type", "application/json").WithBody("""{"protocol_version":1}"""));
        server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(202)
            .WithHeader("Content-Type", "application/json").WithBody($$"""{"repair_id":"{{rid}}","phase":"preparing"}"""));
        server.Given(Request.Create().WithPath($"{path}/{rid}/batches").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
    }

    static string Report(string id, string phase, int restored, bool accounting, int gaps = 14) =>
        $$"""{"repair_id":"{{id}}","phase":"{{phase}}","scanned_sources":2,"matched_records":5,"restored_records":{{restored}},"candidate_records":3,"gaps":[],"remaining_gap_count":{{gaps}},"accounting_current":{{(accounting ? "true" : "false")}}}""";

}
