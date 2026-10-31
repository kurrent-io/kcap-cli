using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Capacitor.Cli.Core.LocalEdition;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class PortHolderTests {
    sealed class Lsof(Func<RunOptions, CancellationToken, Task<ProcessResult>> run) : IProcessRunner {
        public RunOptions? Options;

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Options = options;
            return run(options, ct);
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    static Task<PortHolder?> Find(Lsof lsof, TimeProvider? time = null, CancellationToken ct = default) =>
        PortHolder.FindAsync(lsof, time ?? TimeProvider.System, 9473, ct);

    [Test, ExcludeOn(OS.Windows)]
    public async Task Lsof_output_names_the_holder() {
        var lsof = new Lsof((_, _) => Task.FromResult(new ProcessResult(0, "p812\ncnginx\nf6\nn127.0.0.1:9473\n", "", false)));

        await Assert.That(await Find(lsof)).IsEqualTo(new PortHolder(812, "nginx"));
        await Assert.That(lsof.Options!.Timeout).IsEqualTo(PortHolder.LookupTimeout);
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task An_lsof_that_runs_out_of_time_names_no_one() =>
        await Assert.That(await Find(new Lsof((_, _) => Task.FromResult(new ProcessResult(-1, "p812\ncnginx\nf6\nn127.0.0.1:9473\n", "", true)))))
            .IsNull();

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_missing_lsof_names_no_one() =>
        await Assert.That(await Find(new Lsof((_, _) => throw new System.ComponentModel.Win32Exception(2)))).IsNull();

    /// <summary>A kill cannot end a process stuck in an uninterruptible wait on a stale mount, so the caller stops
    /// waiting on its own clock rather than on the process.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task An_lsof_that_outlives_its_kill_is_abandoned() {
        var time = new FakeTimeProvider();
        var find = Find(new Lsof((_, _) => new TaskCompletionSource<ProcessResult>().Task), time);

        time.Advance(PortHolder.LookupTimeout + PortHolder.KillGrace);

        await Assert.That(await find).IsNull();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_cancelled_lookup_throws_rather_than_naming_no_one() {
        using var cts = new CancellationTokenSource();
        var find = Find(new Lsof((_, _) => new TaskCompletionSource<ProcessResult>().Task), ct: cts.Token);

        await cts.CancelAsync();

        await Assert.That(async () => await find).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task The_lookup_hint_is_each_platforms_own_command() {
        await Assert.That(PortHolder.LookupCommand(9473, OSPlatform.Windows)).IsEqualTo("netstat -ano -p TCP | findstr :9473");
        await Assert.That(PortHolder.LookupCommand(9473, OSPlatform.Linux)).IsEqualTo("sudo ss -ltnp 'sport = :9473'");
        await Assert.That(PortHolder.LookupCommand(9473, OSPlatform.OSX)).IsEqualTo("sudo lsof -nP -iTCP:9473 -sTCP:LISTEN");
    }

    [Test]
    [Arguments("127.0.0.1:9473")]
    [Arguments("*:9473")]
    [Arguments("[::1]:9473")]
    [Arguments("[::]:9473")]
    public async Task A_listener_on_loopback_or_every_address_holds_the_port(string address) =>
        await Assert.That(PortHolder.Parse($"p812\ncnginx\nf6\nn{address}\n")).IsEqualTo(new PortHolder(812, "nginx"));

    [Test]
    public async Task A_listener_on_another_address_only_shares_the_port() =>
        await Assert.That(PortHolder.Parse("p812\ncnginx\nf6\nn192.168.1.5:9473\n")).IsNull();

    [Test]
    public async Task The_first_conflicting_process_is_named() =>
        await Assert.That(PortHolder.Parse("p812\ncnginx\nf6\nn192.168.1.5:9473\np900\ncPython\nf3\nn127.0.0.1:9473\n"))
            .IsEqualTo(new PortHolder(900, "Python"));

    [Test]
    public async Task Nothing_listening_is_null() =>
        await Assert.That(PortHolder.Parse("")).IsNull();

    [Test]
    [Arguments("kcap-server", true)]
    [Arguments("kcap-server.exe", true)]
    [Arguments("KCAP-SERVER", true)]
    [Arguments("python3", false)]
    [Arguments("kcap-server-old", false)]
    public async Task Only_the_servers_own_name_reads_as_the_server(string command, bool server) =>
        await Assert.That(new PortHolder(1, command).IsServerProcess).IsEqualTo(server);

    /// <summary>Windows names the holder from its TCP table, not from <c>lsof</c>: a listener this test opens on
    /// loopback is found as this process.</summary>
    [Test, RunOn(OS.Windows)]
    public async Task Windows_names_the_process_listening_on_loopback() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var holder = PortHolder.Find(port);

            await Assert.That(holder).IsNotNull();
            await Assert.That(holder!.Pid).IsEqualTo(Environment.ProcessId);
        } finally {
            listener.Stop();
        }
    }
}
