using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>Runs the real binary as Claude runs a status line: payload on stdin, the account on the
/// command line, with no server configured and every proxy variable aimed at a local listener that
/// must see no connection.</summary>
[ParallelLimiter<SubprocessLimit>]
public class StatusLineWrapperTests : IDisposable {
    const string AccountId = "acct1";
    const string Marker    = "KCAP_STATUSLINE_TEST_MARKER";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempDir]         public required TempDir         Tmp     { get; init; }

    readonly TcpListener   _proxy   = new(IPAddress.Loopback, 0);
    readonly List<Process> _spawned = [];

    public StatusLineWrapperTests() => _proxy.Start();

    public void Dispose() {
        foreach (var p in _spawned) {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.Dispose();
        }
        _proxy.Stop();
    }

    AccountStore Accounts => AccountStore.Beside(Daemons.Store);

    void Register(string? original, bool backup = true) =>
        Accounts.Mutate(r => (r with {
            Accounts = [
                new VendorAccount(AccountId, HarnessId.Claude, Tmp.PathTo("claude-home"), "work", DateTimeOffset.UnixEpoch,
                    StatusLine: backup ? new StatusLineBackup(original) : null),
            ],
        }, 0));

    static string Payload(string session = "s1") => new JsonObject {
        ["session_id"] = session,
        ["cost"]       = new JsonObject { ["total_api_duration_ms"] = 1200 },
        ["rate_limits"] = new JsonObject {
            ["five_hour"] = new JsonObject { ["used_percentage"] = 12.5, ["resets_at"] = 1_800_000_000 },
            ["seven_day"] = new JsonObject { ["used_percentage"] = 40, ["resets_at"] = 1_800_500_000 },
        },
    }.ToJsonString();

    static string Quoted(string path) => "'" + path + "'";

    Process Start(string stdin, string? workingDirectory = null, string? daemonsDir = null) {
        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "statusline", "--account", AccountId);
        psi.WorkingDirectory = workingDirectory ?? Tmp.Path;
        if (daemonsDir is not null) psi.Environment[DaemonStore.DaemonsDirEnvVar] = daemonsDir;

        var proxy = $"http://127.0.0.1:{((IPEndPoint) _proxy.LocalEndpoint).Port}";
        foreach (var name in new[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "https_proxy", "http_proxy", "all_proxy" })
            psi.Environment[name] = proxy;
        psi.Environment.Remove("NO_PROXY");
        psi.Environment.Remove("no_proxy");
        psi.Environment[Marker] = "marker-value";

        var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start kcap");
        _spawned.Add(process);

        _ = Task.Run(async () => {
            try {
                await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(stdin));
            } catch (IOException) {
            } finally {
                try { process.StandardInput.Close(); } catch (IOException) { }
            }
        });

        return process;
    }

    async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string stdin, string? workingDirectory = null, string? daemonsDir = null) {
        var process = Start(stdin, workingDirectory, daemonsDir);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, await stdout, await stderr);
    }

    [Test]
    public async Task Reading_merged_from_stdin() {
        Register(original: null);

        var r = await RunAsync(Payload());

        await Assert.That(r.ExitCode).IsEqualTo(0);
        var reading = Accounts.Readings.LoadAll()[AccountId];
        await Assert.That(reading.Reading.Windows.Select(w => (w.LimitId, w.WindowMinutes, w.UsedPercent, w.ResetsAt)).ToArray())
            .IsEquivalentTo(new[] {
                ("claude", 300, 12.5, (DateTimeOffset?) DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)),
                ("claude", 10080, 40.0, (DateTimeOffset?) DateTimeOffset.FromUnixTimeSeconds(1_800_500_000)),
            });
        await Assert.That(reading.Sessions.Single().SessionId).IsEqualTo("s1");
    }

    [Test]
    public async Task Original_gets_identical_stdin_env_and_cwd_and_passes_output_and_exit_code() {
        var script = Tmp.CreateExecutable("original.sh", $"""
            #!/bin/sh
            cat
            echo
            echo "marker=${Marker}"
            pwd
            echo "to stderr" >&2
            exit 3
            """);
        Register(Quoted(script));
        Tmp.CreateDir("work");

        var payload = Payload();
        var r = await RunAsync(payload, Tmp.PathTo("work"));

        await Assert.That(r.ExitCode).IsEqualTo(3);
        await Assert.That(r.Stdout.ReplaceLineEndings("\n"))
            .IsEqualTo($"{payload}\nmarker=marker-value\n{Tmp.GetResolvedPath("work")}\n");
        await Assert.That(r.Stderr.Trim()).IsEqualTo("to stderr");
    }

    [Test]
    public async Task No_original_prints_nothing_and_exits_zero() {
        Register(original: null);

        var r = await RunAsync(Payload());

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stdout).IsEmpty();
        await Assert.That(r.Stderr).IsEmpty();
    }

    /// <summary>The filesystem root has no parent to put <c>accounts</c> beside, so resolving the store
    /// throws; the uncreatable path only fails later, on the first read.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Unusable_daemons_directory_prints_nothing_and_exits_zero(bool root) {
        var blocker    = Tmp.CreateFile("blocker");
        var daemonsDir = root ? Path.GetPathRoot(Tmp.Path)! : Path.Combine(blocker, "daemons");

        var r = await RunAsync(Payload(), daemonsDir: daemonsDir);

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stdout).IsEmpty();
        await Assert.That(r.Stderr).IsEmpty();
    }

    [Test]
    public async Task Held_readings_lock_skips_capture_and_original_still_prints_promptly() {
        var script = Tmp.CreateExecutable("original.sh", "#!/bin/sh\necho line\n");
        Register(Quoted(script));

        using var held = Accounts.Readings.TryLock(TimeSpan.FromSeconds(5)) ?? throw new InvalidOperationException("lock not taken");

        var watch = Stopwatch.StartNew();
        var r     = await RunAsync(Payload());
        watch.Stop();

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stdout.Trim()).IsEqualTo("line");
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
        await Assert.That(Accounts.Readings.LoadAll().ContainsKey(AccountId)).IsFalse();
    }

    [Test]
    public async Task Slow_original_is_killed_when_the_wrapper_is_terminated() {
        var pidFile = Tmp.PathTo("original.pid");
        var script  = Tmp.CreateExecutable("original.sh", $"""
            #!/bin/sh
            echo $$ > '{pidFile}.tmp'
            mv '{pidFile}.tmp' '{pidFile}'
            while :; do sleep 1; done
            """);
        Register(Quoted(script));

        var wrapper = Start(Payload());
        var stdout  = wrapper.StandardOutput.ReadToEndAsync();
        var stderr  = wrapper.StandardError.ReadToEndAsync();

        var deadline = Stopwatch.StartNew();
        while (!File.Exists(pidFile) && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        await Assert.That(File.Exists(pidFile)).IsTrue();

        var pid      = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var identity = PidIdentity.Capture(pid);

        using (var kill = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", $"kill -TERM {wrapper.Id}" }, UseShellExecute = false })!)
            await kill.WaitForExitAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await wrapper.WaitForExitAsync(cts.Token);
        await Task.WhenAll(stdout, stderr).WaitAsync(cts.Token);

        await Assert.That(wrapper.ExitCode).IsEqualTo(143);
        await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Terminating_the_wrapper_kills_the_original_s_background_children() {
        var pidFile = Tmp.PathTo("grandchild.pid");
        var script  = Tmp.CreateExecutable("original.sh", $"""
            #!/bin/sh
            sleep 300 &
            echo $! > '{pidFile}.tmp'
            mv '{pidFile}.tmp' '{pidFile}'
            wait
            """);
        Register(Quoted(script));

        var wrapper = Start(Payload());
        var stdout  = wrapper.StandardOutput.ReadToEndAsync();
        var stderr  = wrapper.StandardError.ReadToEndAsync();

        var deadline = Stopwatch.StartNew();
        while (!File.Exists(pidFile) && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        await Assert.That(File.Exists(pidFile)).IsTrue();

        var pid      = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var identity = PidIdentity.Capture(pid);

        using (var kill = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", $"kill -TERM {wrapper.Id}" }, UseShellExecute = false })!)
            await kill.WaitForExitAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await wrapper.WaitForExitAsync(cts.Token);
        await Task.WhenAll(stdout, stderr).WaitAsync(cts.Token);

        await Assert.That(wrapper.ExitCode).IsEqualTo(143);
        await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(5));
    }

    [Test]
    [Arguments("")]
    [Arguments("not json {")]
    public async Task Empty_or_non_json_stdin_still_runs_the_original(string stdin) {
        var script = Tmp.CreateExecutable("original.sh", "#!/bin/sh\ncat\necho ran\n");
        Register(Quoted(script));

        var r = await RunAsync(stdin);

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stdout).IsEqualTo(stdin + "ran\n");
        await Assert.That(Accounts.Readings.LoadAll().ContainsKey(AccountId)).IsFalse();
    }

    [Test]
    public async Task No_network_call() {
        var script = Tmp.CreateExecutable("original.sh", "#!/bin/sh\necho ok\n");
        Register(Quoted(script));

        var r = await RunAsync(Payload());

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(Accounts.Readings.LoadAll().ContainsKey(AccountId)).IsTrue();
        await Assert.That(_proxy.Pending()).IsFalse();
    }
}
