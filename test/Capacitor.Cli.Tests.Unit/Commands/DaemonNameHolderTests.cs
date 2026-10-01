using System.Net;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Setup refuses a daemon name another machine's daemon already holds on the account, which
/// the server would otherwise only refuse once the daemon starts.</summary>
public class DaemonNameHolderTests {
    const string Server = "https://kcap.example";

    static string Daemons(params (string Name, string? MachineId, bool Connected)[] daemons) =>
        "[" + string.Join(",", daemons.Select(d =>
            $$"""{"name":"{{d.Name}}","platform":"linux x64","connected":{{(d.Connected ? "true" : "false")}},"machine_id":{{(d.MachineId is null ? "null" : $"\"{d.MachineId}\"")}}}""")) + "]";

    static async Task<string?> HolderPlatformAsync(string body, string name, string? localMachineId, HttpStatusCode status = HttpStatusCode.OK) {
        using var http = new HttpClient(new SequencedHttpScript(SequencedHttpScript.Reply(status, body)));

        return (await DaemonNameHolder.FindElsewhereAsync(http, Server, name, localMachineId))?.Platform;
    }

    [Test]
    public async Task A_connected_daemon_on_another_machine_holds_the_name() =>
        await Assert.That(await HolderPlatformAsync(Daemons(("agent", "mach-office", true)), "agent", "mach-laptop"))
            .IsEqualTo("linux x64");

    [Test]
    public async Task This_machines_own_daemon_does_not_block_rerunning_setup() =>
        await Assert.That(await HolderPlatformAsync(Daemons(("agent", "mach-laptop", true)), "agent", "mach-laptop")).IsNull();

    [Test]
    public async Task A_machine_that_never_ran_a_daemon_is_blocked_by_any_holder() =>
        await Assert.That(await HolderPlatformAsync(Daemons(("agent", "mach-office", true)), "agent", null)).IsNotNull();

    /// <summary>A holder that reports no machine id could be this machine's own older daemon, so it
    /// cannot prove a collision.</summary>
    [Test]
    public async Task A_holder_with_no_machine_id_is_not_proof_of_a_collision() =>
        await Assert.That(await HolderPlatformAsync(Daemons(("agent", null, true)), "agent", "mach-laptop")).IsNull();

    [Test]
    public async Task Other_names_and_disconnected_daemons_do_not_hold_it() =>
        await Assert.That(await HolderPlatformAsync(
            Daemons(("agent-2", "mach-office", true), ("agent", "mach-office", false)), "agent", "mach-laptop")).IsNull();

    [Test]
    public async Task An_unreadable_or_refused_answer_finds_nothing() {
        await Assert.That(await HolderPlatformAsync("not json", "agent", "mach-laptop")).IsNull();
        await Assert.That(await HolderPlatformAsync(Daemons(("agent", "mach-office", true)), "agent", "mach-laptop", HttpStatusCode.Unauthorized)).IsNull();
    }
}
