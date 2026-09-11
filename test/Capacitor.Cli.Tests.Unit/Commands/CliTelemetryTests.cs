using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class CliTelemetryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    TelemetryProbe StartCapturing(string command = "setup", string? serverUrl = null) {
        var probe = TelemetryProbe.Start(command, Config.Root, serverUrl);

        TelemetryTestGuards.AssertEnabled(command, Config.Root, probe.Telemetry);

        return probe;
    }

    [Test]
    public async Task Capture_records_the_event_with_shared_properties() {
        // A brand-new device state file (this test's own root) means the first-run notice fires and
        // cli_first_run lands here too, so filter by name rather than assume a single event.
        var probe = StartCapturing();

        probe.Telemetry.Capture("cli_setup_started", new JsonObject { ["no_prompt"] = false });

        var e = probe.Only("cli_setup_started");
        await Assert.That(e.Properties["source"]!.GetValue<string>()).IsEqualTo("cli");
        await Assert.That(e.Properties.ContainsKey("cli_version")).IsTrue();
        await Assert.That(e.Properties.ContainsKey("os")).IsTrue();
        await Assert.That(e.Properties.ContainsKey("arch")).IsTrue();
        await Assert.That(e.Properties.ContainsKey("is_ci")).IsTrue();
        await Assert.That(e.Properties["no_prompt"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task Record_command_emits_cli_command_with_exit_code() {
        var probe = StartCapturing("daemon");

        probe.Telemetry.RecordCommand("daemon", ["daemon", "start", "--foreground"], exitCode: 0, durationMs: 42);

        var e = probe.Only("cli_command");
        await Assert.That(e.Properties["command"]!.GetValue<string>()).IsEqualTo("daemon");
        await Assert.That(e.Properties["subcommand"]!.GetValue<string>()).IsEqualTo("start");
        await Assert.That(e.Properties["exit_code"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(e.Properties["duration_ms"]!.GetValue<long>()).IsEqualTo(42L);
    }

    // Started under a REPORTABLE command so the facade stays live, which means RecordCommand's own
    // `!CommandEvents.IsReportable(command)` guard — not the facade's — is what suppresses the
    // event. Load-bearing: a long-lived MCP server starts one facade, then RecordCommand may be
    // called per-invocation with a different, potentially denylisted, command string. Starting
    // under "hook" here would let the facade come up off and short-circuit RecordCommand before
    // its own guard ever ran. `command` here is not denylisted (it is not a verb at all), so
    // RecordCommand proceeds and CommandEvents.ReportableCommand is what has to catch it.
    [Test]
    public async Task Record_command_redacts_an_unrecognised_verb_to_unknown() {
        var probe = StartCapturing("status");

        probe.Telemetry.RecordCommand(
            "/Users/me/work/acme-private", ["/Users/me/work/acme-private"], exitCode: 1, durationMs: 3);

        await Assert.That(probe.Only("cli_command").Properties["command"]!.GetValue<string>()).IsEqualTo("unknown");
    }

    [Test]
    public async Task Denylisted_commands_emit_nothing() {
        var probe = StartCapturing("status");

        probe.Telemetry.RecordCommand("hook", ["hook", "--claude"], exitCode: 0, durationMs: 5);

        await Assert.That(probe.Names.Contains("cli_command")).IsFalse();
    }

    [Test]
    public async Task Disabled_telemetry_captures_nothing() {
        TelemetryState.SetEnabled(false, Config.Root);
        var probe = TelemetryProbe.Start("setup", Config.Root);

        probe.Telemetry.Capture("cli_setup_started", new JsonObject());
        probe.Telemetry.RecordCommand("setup", ["setup"], 0, 1);

        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await Assert.That(probe.Events).IsEmpty();
    }

    // The null object must be inert, not merely non-throwing: a swallowed exception and a
    // correctly-skipped capture look identical from the outside unless state is asserted.
    [Test]
    public async Task A_disabled_facade_is_inert() {
        var telemetry = CliTelemetry.Disabled();

        telemetry.Capture("orphan", new JsonObject());
        telemetry.RecordCommand("status", ["status"], 0, 1);
        await telemetry.FlushAndClose();

        await Assert.That(telemetry.Enabled).IsFalse();
        await Assert.That(telemetry.Join.Current).IsNull();
    }

    [Test]
    public async Task First_run_emits_cli_first_run_once_per_device() {
        var first  = TelemetryProbe.Start("setup", Config.Root);
        var second = TelemetryProbe.Start("status", Config.Root);

        await Assert.That(first.Names.Contains("cli_first_run")).IsTrue();
        await Assert.That(second.Names.Contains("cli_first_run")).IsFalse();
    }

    // "mcp-server" is the pseudo-command MCP servers start under (see McpTelemetry). An
    // agent-spawned MCP server's stderr is not watched by a human, and on a fresh machine it can
    // plausibly be the very first kcap-family process ever run — so it must never consume the
    // once-per-device first-run notice on a human's behalf. The first human-invoked, reportable
    // command afterward must still see it.
    [Test]
    public async Task Mcp_server_start_does_not_consume_the_first_run_notice() {
        var mcp   = TelemetryProbe.Start("mcp-server", Config.Root);
        var human = TelemetryProbe.Start("status", Config.Root);

        await Assert.That(mcp.Names.Contains("cli_first_run")).IsFalse();
        await Assert.That(human.Names.Contains("cli_first_run")).IsTrue();
        await Assert.That(TelemetryState.Read(Config.Root).NoticeShown).IsTrue();
    }

    // TelemetryDeviceId.GetOrCreate has no notion of the persisted flag to re-check
    // (TelemetryDeviceIdTests.Get_or_create_is_unaffected_by_telemetry_state pins that it mints
    // even when called directly against a disabled state), so the facade's own resolve is the one
    // gate left: it must never reach TelemetryDeviceId.GetOrCreate when the settings say disabled.
    [Test]
    public async Task Starting_never_mints_a_device_id_while_opted_out_via_persisted_config() {
        TelemetryState.SetEnabled(false, Config.Root);

        var probe = TelemetryProbe.Start("status", Config.Root);

        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsNull();
    }

    // KCAP_TELEMETRY outranks the persisted flag, so a user who persisted "off" can still opt back
    // in with it — which requires the device id to be minted on that path. A device-id check that
    // vetoed minting off the persisted flag independently would make the documented top-priority
    // env var unable to do so.
    //
    // Bare [NotInParallel] because this mutates the real process environment: a concurrent peer
    // spawning a child would inherit it, and EnvScope.Exclusive enforces the constraint.
    [Test]
    [NotInParallel]
    public async Task Kcap_telemetry_env_var_overrides_a_persisted_off_and_mints_a_device_id() {
        TelemetryState.SetEnabled(false, Config.Root);

        using var env = EnvScope.Exclusive("KCAP_TELEMETRY", "1");

        var probe = TelemetryProbe.Start("status", Config.Root);

        await Assert.That(probe.Telemetry.Enabled).IsTrue();
        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsNotNull();
    }

    // Program.cs pre-applies `config set telemetry off` to disk before starting the facade, so the
    // plain case never activates telemetry at all. But the facade CAN still come up live despite a
    // persisted "off" — KCAP_TELEMETRY=1 legitimately overrides it — so this simulates that:
    // telemetry is already live with cli_first_run queued by the time `config set telemetry off`
    // runs, and asserts TryApplyTelemetry's DiscardAndDisable tears it down completely in the SAME
    // process — dropping what is queued, disabling the facade, and deleting the id — rather than
    // leaving it to survive to this process's own ProcessExit flush.
    [Test]
    public async Task Opting_out_via_config_tears_down_telemetry_in_the_same_process_and_deletes_the_id() {
        var probe = TelemetryProbe.Start("config", Config.Root);

        // Sanity: telemetry actually came up live and minted an id — otherwise the assertions
        // below would trivially pass having exercised nothing.
        await Assert.That(probe.Telemetry.Enabled).IsTrue();
        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsNotNull();
        await Assert.That(probe.Names.Contains("cli_first_run")).IsTrue();

        var exit = await new ConfigCommand(Config.Root, new FixedCapacitorHttpClient(), probe.Telemetry)
            .HandleAsync(["config", "set", "telemetry", "off"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await Assert.That(probe.Events).IsEmpty();
        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsNull();
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsFalse();

        // The disabled facade must not resurrect anything on the exit-time flush either.
        await probe.Telemetry.FlushAndClose();
        await Assert.That(probe.Events).IsEmpty();
    }
}
