using System.Text.Json;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class StatusJsonTests {
    static StatusJson Payload(
            bool configured = true, string? serverUrl = "https://acme.kcap.ai", string authState = "valid") =>
        new(configured, "default",
            new StatusServerJson(serverUrl, true, null),
            new StatusAuthJson(authState, "george", new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero)),
            new StatusVersionJson("1.1.2", "1.2.0", true, false),
            [new StatusHarnessJson("claude", true, false, "kcap plugin install")],
            new StatusDaemonJson(true, [new StatusDaemonEntryJson("laptop", 42)], false));

    [Test]
    public async Task Renders_snake_case_payload() {
        using var doc = JsonDocument.Parse(StatusJsonRender.Render(Payload()));
        var r = doc.RootElement;

        await Assert.That(r.GetProperty("configured").GetBoolean()).IsTrue();
        await Assert.That(r.GetProperty("server").GetProperty("url").GetString()).IsEqualTo("https://acme.kcap.ai");
        await Assert.That(r.GetProperty("server").GetProperty("status_code").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(r.GetProperty("auth").GetProperty("state").GetString()).IsEqualTo("valid");
        await Assert.That(r.GetProperty("version").GetProperty("update_available").GetString()).IsEqualTo("1.2.0");
        await Assert.That(r.GetProperty("version").GetProperty("server_capped").GetBoolean()).IsTrue();
        await Assert.That(r.GetProperty("harnesses")[0].GetProperty("install_command").GetString())
            .IsEqualTo("kcap plugin install");
        await Assert.That(r.GetProperty("daemon").GetProperty("daemons")[0].GetProperty("pid").GetInt32()).IsEqualTo(42);
    }

    // The question a caller deciding whether to run setup is actually asking.
    [Test]
    [Arguments("https://acme.kcap.ai", "valid", true)]
    [Arguments("https://acme.kcap.ai", "machine", true)]
    [Arguments("https://acme.kcap.ai", "expired", false)]
    [Arguments("https://acme.kcap.ai", "none", false)]
    [Arguments(null, "valid", false)]
    [Arguments(null, "none", false)]
    public async Task Configured_needs_a_server_and_credentials(string? serverUrl, string authState, bool expected) {
        await Assert.That(StatusJsonRender.IsConfigured(serverUrl, authState)).IsEqualTo(expected);
    }

    // An unreachable server is a network fact, not a setup one — a caller that treated it as
    // unconfigured would send someone through setup again over a dropped VPN.
    [Test]
    public async Task An_unreachable_server_is_still_configured() {
        await Assert.That(StatusJsonRender.IsConfigured("https://acme.kcap.ai", "valid")).IsTrue();
    }

    [Test]
    public async Task Expiry_reads_in_hours_past_an_hour_and_minutes_below_it() {
        await Assert.That(StatusCommand.FormatExpiry(TimeSpan.FromHours(5))).IsEqualTo("expires in 5h");
        await Assert.That(StatusCommand.FormatExpiry(TimeSpan.FromMinutes(20))).IsEqualTo("expires in 20m");
    }
}
