using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

// The debug-output tests here capture Console, which is process-global, and ConsoleOutput rejects
// an overlapping capture outright.
[NotInParallel]
public class SetupJoinTests {
    [TempDir] public required TempDir Tmp { get; init; }

    TelemetryProbe StartCapturing() => TelemetryProbe.Live("setup", new ConfigRoot(Tmp.Path));

    [Test]
    public async Task Mint_returns_32_lowercase_hex_and_sets_Current() {
        var probe = StartCapturing();

        var key = probe.Join.Mint();

        await Assert.That(key).IsNotNull();
        await Assert.That(key!.Length).IsEqualTo(32);
        await Assert.That(key.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'))).IsTrue();
        await Assert.That(probe.Join.Current).IsEqualTo(key);
    }

    [Test]
    public async Task Mint_stamps_join_id_on_every_subsequent_event() {
        var probe = StartCapturing();

        var key = probe.Join.Mint();
        probe.Funnel.Started(false, false, false);

        await Assert.That(probe.Events[^1].Properties["join_id"]!.GetValue<string>()).IsEqualTo(key);
    }

    // Every opt-out route — KCAP_TELEMETRY=0, DO_NOT_TRACK, the persisted flag, the
    // app-spawn marker — collapses to Enabled=false, so one check covers all of them and
    // every downstream surface is off by construction rather than by four separate guards.
    [Test]
    public async Task Mint_is_a_null_no_op_when_telemetry_is_disabled() {
        var join = CliTelemetry.Disabled().Join;

        var key = join.Mint();

        await Assert.That(key).IsNull();
        await Assert.That(join.Current).IsNull();
    }

    [Test]
    public async Task Mint_twice_keeps_the_first_key() {
        var probe = StartCapturing();

        var first  = probe.Join.Mint();
        var second = probe.Join.Mint();

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(probe.Join.Current).IsEqualTo(first);
    }

    [Test]
    public async Task FirstHopUrl_targets_the_provisioning_endpoint_with_key_and_port() {
        var probe = StartCapturing();
        var key = probe.Join.Mint();

        var url = probe.Join.FirstHopUrl(54321);

        await Assert.That(url).IsEqualTo($"{ProvisioningEndpoint.Url}/api/cli/return?j={key}&p=54321");
    }

    [Test]
    public async Task FirstHopUrl_is_null_without_a_key() {
        // A facade that is off mints nothing, so there is no key to build a hop from.
        await Assert.That(CliTelemetry.Disabled().Join.FirstHopUrl(54321)).IsNull();
    }

    // The key is the bridge token. A debug run would otherwise print it to stderr on every
    // cli_* event of the run; a placeholder still shows the useful signal, which is WHETHER
    // the property is attached.
    [Test]
    public async Task Debug_output_prints_a_placeholder_never_the_key() {
        using var console = ConsoleOutput.StartErrorCapture();

        var probe = TelemetryProbe.Live("setup", new ConfigRoot(Tmp.Path), debug: true);
        var key   = probe.Join.Mint();

        probe.Telemetry.Capture("cli_test_event", new JsonObject());

        var text = console.GetCapturedError();
        await Assert.That(key).IsNotNull();
        await Assert.That(text).Contains("\"join_id\":\"[set]\"");
        await Assert.That(text).DoesNotContain(key!);
    }

    // An event with no key must render with no placeholder at all. A non-auth command is the "no
    // key" state: it enables telemetry but mints nothing.
    [Test]
    public async Task Debug_output_is_unchanged_for_an_event_with_no_key() {
        using var console = ConsoleOutput.StartErrorCapture();

        var probe = TelemetryProbe.Live("recap", new ConfigRoot(Tmp.Path), debug: true);

        probe.Telemetry.Capture("cli_test_event", new JsonObject { ["k"] = "v" });

        await Assert.That(console.GetCapturedError()).DoesNotContain("[set]");
        await Assert.That(console.GetCapturedError()).Contains("\"k\":\"v\"");
    }

    // The key lives in memory for one auth attempt only, so nothing on the machine can replay it
    // into a later run. Scoped honestly: a recording sink never reaches the SPOOL — the one
    // component that does write properties to disk, on a failed flush — so this asserts only that
    // minting and flushing write nothing to the telemetry state files. The spool's own strip is
    // covered directly, against a real TelemetrySpool, in TelemetrySpoolTests; there is no endpoint
    // seam that would let this test drive the real client without posting to production.
    [Test]
    public async Task Minting_writes_nothing_to_the_telemetry_state_files() {
        var dir = Tmp.CreateDir("state");

        var probe = TelemetryProbe.Live("setup", new ConfigRoot(dir.Path));

        var key = probe.Join.Mint();
        await probe.Telemetry.FlushAndClose();

        var written = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);

        await Assert.That(written).IsNotEmpty().Because("a vacuous scan of an empty directory proves nothing");

        foreach (var file in written)
            await Assert.That(await File.ReadAllTextAsync(file)).DoesNotContain(key!);
    }

    // One key per auth run, and a run is a facade: two in the same process must not share one,
    // which is what stops one auth attempt's key reaching the next.
    [Test]
    public async Task Each_facade_mints_its_own_key() {
        var probe = StartCapturing();
        var next  = TelemetryProbe.Live("setup", new ConfigRoot(Tmp.CreateDir("second").Path));

        await Assert.That(probe.Join.Current).IsNotNull();
        await Assert.That(next.Join.Current).IsNotNull();
        await Assert.That(next.Join.Current).IsNotEqualTo(probe.Join.Current);
    }

    // SetupJoin IS the ILoopbackJoin the browser is handed, so the browser stays decoupled from it.
    [Test]
    public async Task The_loopback_join_builds_the_first_hop_url() {
        var probe = StartCapturing();
        var key = probe.Join.Mint();

        await Assert.That(probe.Join.FirstHopUrl(4242))
            .IsEqualTo($"{ProvisioningEndpoint.Url}/api/cli/return?j={key}&p=4242");
    }

    // Capture an event and read back the merged shared bag.
    static JsonObject SharedOf(TelemetryProbe probe) {
        probe.Telemetry.Capture("cli_probe", new JsonObject());

        return probe.Events[^1].Properties;
    }

    [Test]
    public async Task Accept_merges_all_three_properties_on_a_key_match() {
        var probe = StartCapturing();
        var key  = probe.Join.Mint();

        probe.Join.Accept($"?j={key}&v=redesign&w1=cap-device-1&w2=www-device-2");

        var props = SharedOf(probe);
        await Assert.That(props["site_variant"]!.GetValue<string>()).IsEqualTo("redesign");
        await Assert.That(props["web_device_id_capacitor"]!.GetValue<string>()).IsEqualTo("cap-device-1");
        await Assert.That(props["web_device_id_www"]!.GetValue<string>()).IsEqualTo("www-device-2");
    }

    // Arrival at the loopback port is not evidence the web hops happened — any local process,
    // browser tab or web page can cause a GET there. Only a key match is.
    [Test]
    public async Task Accept_drops_everything_on_a_key_mismatch() {
        var probe = StartCapturing();
        probe.Join.Mint();

        probe.Join.Accept("?j=00000000000000000000000000000000&v=legacy&w1=attacker");

        var props = SharedOf(probe);
        await Assert.That(props["site_variant"]).IsNull();
        await Assert.That(props["web_device_id_capacitor"]).IsNull();
        await Assert.That(props["web_device_id_www"]).IsNull();
    }

    [Test]
    public async Task Accept_drops_everything_when_j_is_missing() {
        var probe = StartCapturing();
        probe.Join.Mint();

        probe.Join.Accept("?v=legacy&w1=cap-device-1");

        await Assert.That(SharedOf(probe)["web_device_id_capacitor"]).IsNull();
    }

    [Test]
    public async Task A_second_matching_Accept_can_never_overwrite_merged_state() {
        var probe = StartCapturing();
        var key  = probe.Join.Mint();

        probe.Join.Accept($"?j={key}&v=legacy&w1=first");
        probe.Join.Accept($"?j={key}&v=redesign&w1=second");

        var props = SharedOf(probe);
        await Assert.That(props["site_variant"]!.GetValue<string>()).IsEqualTo("legacy");
        await Assert.That(props["web_device_id_capacitor"]!.GetValue<string>()).IsEqualTo("first");
    }

    // A mismatch deliberately does NOT consume the one-shot: burning it would let any local
    // process kill the bridge by racing a junk GET, and guessing a 128-bit key inside a
    // 15-second window is not a real threat.
    [Test]
    public async Task A_mismatch_does_not_burn_the_join() {
        var probe = StartCapturing();
        var key  = probe.Join.Mint();

        probe.Join.Accept("?j=00000000000000000000000000000000&w1=attacker");
        probe.Join.Accept($"?j={key}&w1=genuine");

        await Assert.That(SharedOf(probe)["web_device_id_capacitor"]!.GetValue<string>()).IsEqualTo("genuine");
    }

    // The common case today: the arm cookie only exists for visitors the experiment assigned,
    // and the experiment is scoped to one host, so most runs carry no variant at all. A rule
    // that required it would reject every valid web id alongside it and ship dead.
    [Test]
    public async Task Missing_v_still_merges_the_web_ids() {
        var probe = StartCapturing();
        var key  = probe.Join.Mint();

        probe.Join.Accept($"?j={key}&w1=cap-device-1&w2=www-device-2");

        var props = SharedOf(probe);
        await Assert.That(props["site_variant"]).IsNull();
        await Assert.That(props["web_device_id_capacitor"]!.GetValue<string>()).IsEqualTo("cap-device-1");
        await Assert.That(props["web_device_id_www"]!.GetValue<string>()).IsEqualTo("www-device-2");
    }

    [Test]
    public async Task An_out_of_vocabulary_v_never_discards_the_web_ids() {
        var probe = StartCapturing();
        var key  = probe.Join.Mint();

        probe.Join.Accept($"?j={key}&v=whatever&w1=cap-device-1&w2=www-device-2");

        var props = SharedOf(probe);
        await Assert.That(props["site_variant"]).IsNull();
        await Assert.That(props["web_device_id_capacitor"]!.GetValue<string>()).IsEqualTo("cap-device-1");
    }

    [Test]
    public async Task One_malformed_web_id_never_discards_the_good_one() {
        var probe = StartCapturing();
        var key  = probe.Join.Mint();

        probe.Join.Accept($"?j={key}&v=legacy&w1=has%20a%20space&w2=www-device-2");

        var props = SharedOf(probe);
        await Assert.That(props["site_variant"]!.GetValue<string>()).IsEqualTo("legacy");
        await Assert.That(props["web_device_id_capacitor"]).IsNull();
        await Assert.That(props["web_device_id_www"]!.GetValue<string>()).IsEqualTo("www-device-2");
    }

    [Test]
    public async Task An_over_long_web_id_is_dropped() {
        var probe = StartCapturing();
        var key  = probe.Join.Mint();
        var tooLong = new string('a', 65);
        var atLimit = new string('b', 64);

        probe.Join.Accept($"?j={key}&w1={tooLong}&w2={atLimit}");

        var props = SharedOf(probe);
        await Assert.That(props["web_device_id_capacitor"]).IsNull();
        await Assert.That(props["web_device_id_www"]!.GetValue<string>()).IsEqualTo(atLimit);
    }

    // The privacy guarantee, asserted directly: after identify(email) the web distinct_id IS an
    // email address, and the character class makes importing one structurally impossible.
    [Test]
    public async Task An_email_shaped_web_id_can_never_reach_the_property_bag() {
        var probe = StartCapturing();
        var key  = probe.Join.Mint();
        var email = Uri.EscapeDataString("someone@example.com");
        var other = Uri.EscapeDataString("other@example.com");

        probe.Join.Accept($"?j={key}&w1={email}&w2={other}");

        var props = SharedOf(probe);
        await Assert.That(props["web_device_id_capacitor"]).IsNull();
        await Assert.That(props["web_device_id_www"]).IsNull();
        await Assert.That(props.ToJsonString()).DoesNotContain("@");
    }

    // A mismatched value is attacker-chosen, so it must not surface anywhere at all.
    [Test]
    public async Task A_mismatched_value_never_reaches_debug_output() {
        using var console = ConsoleOutput.StartErrorCapture();

        var probe = TelemetryProbe.Live("setup", new ConfigRoot(Tmp.Path), debug: true);
        probe.Join.Mint();

        probe.Join.Accept("?j=00000000000000000000000000000000&w1=attacker-chosen-value");
        probe.Telemetry.Capture("cli_probe", new JsonObject());

        var printed = console.GetCapturedError();

        // Without this the assertion below passes on an empty buffer, which is what a debug run
        // that never printed looks like.
        await Assert.That(printed).Contains("cli_probe");
        await Assert.That(printed).DoesNotContain("attacker-chosen-value");
    }

    [Test]
    public async Task Accept_without_a_minted_key_is_a_no_op() {
        var probe = StartCapturing();

        probe.Join.Accept("?j=00000000000000000000000000000000&w1=cap-device-1");

        await Assert.That(SharedOf(probe)["web_device_id_capacitor"]).IsNull();
    }

    // Nothing here may throw: an exception escaping into the NativeAOT runtime aborts the
    // process, and this runs on a background thread nobody is watching.
    [Test]
    public async Task Accept_survives_a_junk_query_without_throwing() {
        var probe = StartCapturing();
        var key  = probe.Join.Mint();

        foreach (var junk in new[] { "", "?", "?????", "&&&", "?j", "?=x", "?j=", $"?j={key}&w1" })
            probe.Join.Accept(junk);

        await Assert.That(SharedOf(probe)["web_device_id_capacitor"]).IsNull();
    }

    // The return hop is merged on a background thread while the foreground is still running the
    // sign-in funnel — and those two moments coincide by design, because the funnel's
    // signin_completed fires immediately after the browser call returns, which is exactly when the
    // hop lands. Both touch the same shared property bag.
    //
    // Losing that race is silent: the merge invalidates the capture's enumeration, the exception is
    // swallowed on the way out (telemetry must never throw), and the event simply never appears.
    // The event most likely to vanish is the one the feature exists to measure.
    [Test]
    public async Task Capturing_while_the_return_hop_merges_never_drops_an_event() {
        var probe = StartCapturing();
        const int captures = 300;

        // A wide enumeration, so the window is reliably hit rather than occasionally: each capture
        // walks every shared property, and an insert during that walk is what faults.
        for (var i = 0; i < 2000; i++) probe.Telemetry.AddSharedProperty($"seed_{i}", i);

        var merging = Task.Run(() => {
            // Distinct names, so each is an insert rather than an overwrite — an insert is what
            // invalidates an enumeration already in flight.
            for (var i = 0; i < captures; i++) probe.Telemetry.AddSharedProperty($"probe_{i}", i);
        });

        for (var i = 0; i < captures; i++) probe.Telemetry.Capture("cli_probe", new JsonObject());

        await merging;

        await Assert.That(probe.Events.Count).IsEqualTo(captures)
            .Because("a concurrent merge must not silently swallow a captured event");
    }

    // The three accepted fields land together or not at all, so no event can carry a half-merged
    // web identity — one host's id present and the other's still missing.
    [Test]
    public async Task The_returned_context_is_merged_as_one_batch() {
        var probe = StartCapturing();
        var key  = probe.Join.Mint();

        probe.Join.Accept($"?j={key}&v=legacy&w1=cap-1&w2=www-1");

        probe.Telemetry.Capture("cli_probe", new JsonObject());
        var props = probe.Events[^1].Properties;

        await Assert.That(props["site_variant"]!.GetValue<string>()).IsEqualTo("legacy");
        await Assert.That(props["web_device_id_capacitor"]!.GetValue<string>()).IsEqualTo("cap-1");
        await Assert.That(props["web_device_id_www"]!.GetValue<string>()).IsEqualTo("www-1");
    }
}
