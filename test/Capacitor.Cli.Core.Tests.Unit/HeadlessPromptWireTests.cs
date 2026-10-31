using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>The headless prompt records bind the server's wire names, and an unreported token count stays null.</summary>
public class HeadlessPromptWireTests {
    [Test]
    public async Task The_request_binds_the_servers_wire_names() {
        const string json = """
            {"request_id":"r1","harness":"claude","model":"haiku","reasoning_effort":null,"system_prompt":"s","prompt":"p",
             "json_schema":"{}","max_turns":2,"max_budget_usd":0.5,"deadline":"2026-10-31T12:00:00+00:00"}
            """;

        var args = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.RunHeadlessPromptArgs);

        await Assert.That(args).IsEqualTo(new RunHeadlessPromptArgs("r1", "claude", "haiku", null, "s", "p", "{}", 2, 0.5,
            new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public async Task The_response_keeps_unreported_tokens_null() {
        var response = new HeadlessPromptResponse(true, null, null, null, null, null, FailureKind: "not_signed_in", Error: "e");

        var json = JsonSerializer.Serialize(response, CapacitorJsonContext.Default.HeadlessPromptResponse);

        await Assert.That(json).Contains("\"started\":true").And.Contains("\"input_tokens\":null").And.Contains("\"failure_kind\":\"not_signed_in\"");
        await Assert.That(JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.HeadlessPromptResponse)).IsEqualTo(response);
    }

    [Test]
    public async Task The_cancel_binds_the_request_id() {
        var args = JsonSerializer.Deserialize("""{"request_id":"r1"}""", CapacitorJsonContext.Default.CancelHeadlessPromptArgs);

        await Assert.That(args.RequestId).IsEqualTo("r1");
    }
}
