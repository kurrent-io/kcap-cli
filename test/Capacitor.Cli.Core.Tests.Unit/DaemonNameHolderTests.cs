using System.Net;
using System.Collections.Frozen;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>Setup offers only a daemon name no other machine's daemon holds on the account, which the
/// server would otherwise only refuse once the daemon starts.</summary>
public class DaemonNameHolderTests {
    const string Server = "https://kcap.example";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string ThisMachine() => new MachineId(Config.Root).Get();

    static string Daemons(params (string Name, string? MachineId, bool Connected)[] daemons) =>
        "[" + string.Join(",", daemons.Select(d =>
            $$"""{"name":"{{d.Name}}","platform":"linux x64","connected":{{(d.Connected ? "true" : "false")}},"machine_id":{{(d.MachineId is null ? "null" : $"\"{d.MachineId}\"")}}}""")) + "]";

    async Task<string?> HolderPlatformAsync(string body, string name, HttpStatusCode status = HttpStatusCode.OK) {
        using var http = new HttpClient(new SequencedHttpScript(SequencedHttpScript.Reply(status, body)));

        var held = await DaemonNameHolder.HeldElsewhereAsync(http, Server, Config.Root);

        return held.TryGetValue(name, out var holding) ? holding.Platform : null;
    }

    [Test]
    public async Task A_connected_daemon_on_another_machine_holds_the_name() {
        ThisMachine();
        await Assert.That(await HolderPlatformAsync(Daemons(("agent", "mach-office", true)), "agent")).IsEqualTo("linux x64");
    }

    [Test]
    public async Task This_machines_own_daemon_does_not_block_rerunning_setup() =>
        await Assert.That(await HolderPlatformAsync(Daemons(("agent", ThisMachine(), true)), "agent")).IsNull();

    /// <summary>The profile's memory-tagging machine id differs from the one a daemon reports; comparing
    /// against it would refuse this machine's own daemon.</summary>
    [Test]
    public async Task The_profile_machine_id_is_not_the_daemons_machine_id() {
        var daemonId = ThisMachine();
        await Assert.That(await MachineIdProvider.GetOrCreateAsync(Config.Root)).IsNotEqualTo(daemonId);

        await Assert.That(await HolderPlatformAsync(Daemons(("agent", daemonId, true)), "agent")).IsNull();
    }

    [Test]
    public async Task A_machine_that_never_ran_a_daemon_is_blocked_by_any_holder() =>
        await Assert.That(await HolderPlatformAsync(Daemons(("agent", "mach-office", true)), "agent")).IsNotNull();

    /// <summary>A holder that reports no machine id could be this machine's own older daemon, so it
    /// cannot prove a collision.</summary>
    [Test]
    public async Task A_holder_with_no_machine_id_is_not_proof_of_a_collision() {
        ThisMachine();
        await Assert.That(await HolderPlatformAsync(Daemons(("agent", null, true)), "agent")).IsNull();
    }

    [Test]
    public async Task Other_names_and_disconnected_daemons_do_not_hold_it() =>
        await Assert.That(await HolderPlatformAsync(
            Daemons(("agent-2", "mach-office", true), ("agent", "mach-office", false)), "agent")).IsNull();

    [Test]
    public async Task An_unreadable_or_refused_answer_finds_nothing() {
        await Assert.That(await HolderPlatformAsync("not json", "agent")).IsNull();
        await Assert.That(await HolderPlatformAsync(Daemons(("agent", "mach-office", true)), "agent", HttpStatusCode.Unauthorized)).IsNull();
    }

    /// <summary>A peer holding machine.json exclusively makes this machine's id unreadable for a moment;
    /// that must not turn this machine's own daemon into a collision.</summary>
    [Test]
    public async Task An_unreadable_machine_id_does_not_prove_a_collision() {
        ThisMachine();
        var path = Config.Root.Path("machine.json");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            await Assert.That(new MachineId(Config.Root).ReadPersisted()).IsNull();
            await Assert.That(await HolderPlatformAsync(Daemons(("agent", "mach-office", true)), "agent")).IsNull();
        }
    }

    [Test]
    public async Task A_free_name_is_offered_as_it_is() =>
        await Assert.That(DaemonNameHolder.FirstFree("studio", FrozenDictionary<string, DaemonNameHolding>.Empty)).IsEqualTo("studio");

    [Test]
    public async Task A_held_name_gets_the_first_free_numbered_suffix() {
        var held = new Dictionary<string, DaemonNameHolding> {
            ["studio"]   = new("linux x64", null),
            ["studio-1"] = new("linux x64", null),
            ["studio-3"] = new("linux x64", null),
        };

        await Assert.That(DaemonNameHolder.FirstFree("studio", held)).IsEqualTo("studio-2");
    }
}
