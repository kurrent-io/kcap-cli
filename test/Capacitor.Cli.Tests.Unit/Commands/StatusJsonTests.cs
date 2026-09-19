using System.Text.Json;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class StatusJsonTests {
    const string Server = "https://acme.kcap.ai";

    static readonly DateTimeOffset Expiry = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    static StatusJson Payload(
            StatusCommand.ServerReach? server = null, StatusCommand.AuthSnapshot? auth = null,
            IReadOnlyList<StatusCommand.DaemonEntry>? daemons = null) =>
        StatusCommand.BuildPayload(
            "default",
            server ?? new StatusCommand.ServerReach(Server, true, null),
            auth ?? new StatusCommand.AuthSnapshot(StatusAuthState.Valid, "george", Expiry),
            "1.1.2", new UpdateAdvisory("1.1.2", "1.2.0", Newer: true, ServerCapped: true), bundled: false,
            [new StatusHarnessJson("claude", true, false, "kcap plugin install")],
            daemons ?? [new StatusCommand.DaemonEntry("laptop", 42, true)]);

    [Test]
    public async Task Renders_snake_case_payload() {
        using var doc = JsonDocument.Parse(StatusJsonRender.Render(Payload()));
        var r = doc.RootElement;

        await Assert.That(r.GetProperty("configured").GetBoolean()).IsTrue();
        await Assert.That(r.GetProperty("server").GetProperty("url").GetString()).IsEqualTo(Server);
        await Assert.That(r.GetProperty("server").GetProperty("status_code").IsNull).IsTrue();
        await Assert.That(r.GetProperty("auth").GetProperty("state").GetString()).IsEqualTo("valid");
        await Assert.That(r.GetProperty("version").GetProperty("update_available").GetString()).IsEqualTo("1.2.0");
        await Assert.That(r.GetProperty("version").GetProperty("server_capped").GetBoolean()).IsTrue();
        await Assert.That(r.GetProperty("harnesses")[0].GetProperty("install_command").GetString())
            .IsEqualTo("kcap plugin install");
        await Assert.That(r.GetProperty("daemon").GetProperty("daemons")[0].GetProperty("pid").GetInt32()).IsEqualTo(42);
    }

    // The question a caller deciding whether to run setup is actually asking.
    [Test]
    [Arguments(Server, StatusAuthState.Valid, true)]
    [Arguments(Server, StatusAuthState.Machine, true)]
    [Arguments(Server, StatusAuthState.Expired, false)]
    [Arguments(Server, StatusAuthState.None, false)]
    [Arguments(null, StatusAuthState.Valid, false)]
    [Arguments(null, StatusAuthState.Machine, false)]
    [Arguments(null, StatusAuthState.None, false)]
    // A server that asks for no auth is fully set up with an empty token store: gating setup on a
    // credential there would re-run it on every call.
    [Arguments(Server, StatusAuthState.NotRequired, true)]
    // Either variable alone diverts auth off the token store and then fails, so nothing records
    // and there is no credential to skip setup on.
    [Arguments(Server, StatusAuthState.MachineIncomplete, false)]
    // A token bound to another server is withheld before the request is sent, so it cannot stand
    // in for setup however valid it looks in the store.
    [Arguments(Server, StatusAuthState.WrongServer, false)]
    public async Task Configured_needs_a_server_and_usable_credentials(
            string? serverUrl, StatusAuthState auth, bool expected) {
        await Assert.That(StatusJsonRender.IsConfigured(serverUrl, auth)).IsEqualTo(expected);
    }

    // An unreachable server is a network fact, not a setup one — a caller that treated it as
    // unconfigured would send someone through setup again over a dropped VPN.
    [Test]
    [Arguments(null)]
    [Arguments(503)]
    public async Task An_unreachable_server_is_reported_and_still_configured(int? statusCode) {
        var payload = Payload(server: new StatusCommand.ServerReach(Server, false, statusCode));

        await Assert.That(payload.Configured).IsTrue();
        await Assert.That(payload.Server.Reachable).IsFalse();
        await Assert.That(payload.Server.StatusCode).IsEqualTo(statusCode);
    }

    [Test]
    public async Task No_server_has_no_reachability_to_report() {
        var payload = Payload(server: new StatusCommand.ServerReach(null, false, null));

        await Assert.That(payload.Configured).IsFalse();
        await Assert.That(payload.Server.Reachable).IsNull();
    }

    // A marker with no usable PID is a file someone still has to clean up, so it counts as stale
    // without ever being listed as a daemon.
    [Test]
    public async Task Dead_and_unreadable_markers_are_stale_and_never_listed() {
        var payload = Payload(daemons: [
            new StatusCommand.DaemonEntry("laptop", 42, true),
            new StatusCommand.DaemonEntry("gone", 43, false),
            new StatusCommand.DaemonEntry("corrupt", null, false),
        ]);

        await Assert.That(payload.Daemon.Running).IsTrue();
        await Assert.That(payload.Daemon.Daemons).IsEquivalentTo([new StatusDaemonEntryJson("laptop", 42)]);
        await Assert.That(payload.Daemon.StalePidFiles).IsTrue();
    }

    [Test]
    public async Task Every_auth_state_has_its_own_wire_spelling() {
        var spellings = Enum.GetValues<StatusAuthState>().Select(s => s.Wire).ToList();

        await Assert.That(spellings.Distinct().Count()).IsEqualTo(spellings.Count);
    }

    [Test]
    public async Task Expiry_reads_in_hours_past_an_hour_and_minutes_below_it() {
        await Assert.That(StatusCommand.FormatExpiry(TimeSpan.FromHours(5))).IsEqualTo("expires in 5h");
        await Assert.That(StatusCommand.FormatExpiry(TimeSpan.FromMinutes(20))).IsEqualTo("expires in 20m");
    }
}
