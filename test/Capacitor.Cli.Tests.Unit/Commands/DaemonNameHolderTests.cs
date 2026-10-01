using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Setup refuses a daemon name another machine's daemon already holds on the account, which
/// the server would otherwise only refuse once the daemon starts.</summary>
public class DaemonNameHolderTests {
    const string Server = "https://kcap.example";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string ThisMachine() => new MachineId(Config.Root).Get();

    static string Daemons(params (string Name, string? MachineId, bool Connected)[] daemons) =>
        "[" + string.Join(",", daemons.Select(d =>
            $$"""{"name":"{{d.Name}}","platform":"linux x64","connected":{{(d.Connected ? "true" : "false")}},"machine_id":{{(d.MachineId is null ? "null" : $"\"{d.MachineId}\"")}}}""")) + "]";

    async Task<string?> HolderPlatformAsync(string body, string name, HttpStatusCode status = HttpStatusCode.OK) {
        using var http = new HttpClient(new SequencedHttpScript(SequencedHttpScript.Reply(status, body)));

        return (await DaemonNameHolder.FindElsewhereAsync(http, Server, name, Config.Root))?.Platform;
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
}
