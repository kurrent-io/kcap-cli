using Capacitor.Cli.Daemon.Harness.Pi;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

[ParallelLimiter<SubprocessLimit>]
public class PiModelCatalogProbeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Response = """{"type":"response","command":"get_available_models","success":true,"data":{"models":[{"id":"claude-opus-5","name":"Claude Opus 5","provider":"anthropic"}]}}""";

    static Task<IReadOnlyList<Capacitor.Cli.Core.VendorModelOption>?> Run(FakePiRpcProcess fake, string dir, TimeProvider? time = null) =>
        PiModelCatalogProbe.RunAsync("/opt/pi", dir, (_, _) => Task.FromResult<IPiRpcProcess>(fake),
            time ?? TimeProvider.System, NullLogger.Instance, CancellationToken.None);

    [Test]
    public async Task Start_info_carries_offline_no_extensions_no_session_and_the_probe_directory() {
        var psi = PiModelCatalogProbe.BuildStartInfo("/opt/pi", "/state/pi-probe");
        await Assert.That(psi.ArgumentList).IsEquivalentTo(["--mode", "rpc", "--offline", "--no-extensions", "--no-session"], CollectionOrdering.Matching);
        await Assert.That(psi.WorkingDirectory).IsEqualTo("/state/pi-probe");
        await Assert.That(psi.Environment[PiLaunchEnvironment.PureVariable]).IsEqualTo("1");
        await Assert.That(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError).IsTrue();
    }

    [Test]
    public async Task Sends_the_command_reads_past_noise_and_closes_stdin_only_after_the_response() {
        var fake = new FakePiRpcProcess { AutoStateResponse = null, ExitsOnInputClose = true };
        var closedBeforeResponse = false;
        fake.OnWrite = _ => {
            fake.Push("""{"type":"noise"}""");
            closedBeforeResponse = fake.InputCloseCalls > 0;
            fake.Push(Response);
        };

        var models = await Run(fake, PiModelCatalogProbe.DirectoryFor(Tmp.Path));

        await Assert.That(fake.Writes.Single()).Contains("\"get_available_models\"");
        await Assert.That(closedBeforeResponse).IsFalse();
        await Assert.That(fake.InputCloseCalls).IsEqualTo(1);
        await Assert.That(models!.Single().Value).IsEqualTo("anthropic/claude-opus-5");
    }

    [Test]
    public async Task Exits_before_answering_returns_null() {
        var fake = new FakePiRpcProcess { AutoStateResponse = null };
        fake.OnWrite = _ => fake.EndOfStream(exitCode: 1);

        await Assert.That(await Run(fake, PiModelCatalogProbe.DirectoryFor(Tmp.Path))).IsNull();
    }

    [Test]
    public async Task A_failed_write_returns_null() {
        var fake = new FakePiRpcProcess { AutoStateResponse = null, FailWrites = true };

        await Assert.That(await Run(fake, PiModelCatalogProbe.DirectoryFor(Tmp.Path))).IsNull();
        await Assert.That(fake.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Deadline_expiry_terminates_the_child_and_returns_null() {
        var time = new FakeTimeProvider();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakePiRpcProcess { AutoStateResponse = null, OnWrite = _ => sent.TrySetResult() };
        var run  = Run(fake, PiModelCatalogProbe.DirectoryFor(Tmp.Path), time);

        // Past the spawn, so the deadline lands on the running exchange rather than the start.
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        time.Advance(PiModelCatalogProbe.Deadline + TimeSpan.FromSeconds(1));

        await Assert.That(await run).IsNull();
        await Assert.That(fake.TerminateCalls).IsGreaterThan(0);
    }

    [Test]
    public async Task A_stalled_spawn_is_bounded_by_the_deadline_and_a_late_process_is_disposed() {
        var time    = new FakeTimeProvider();
        var release = new TaskCompletionSource<IPiRpcProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late    = new FakePiRpcProcess { AutoStateResponse = null };
        var run     = PiModelCatalogProbe.RunAsync("/opt/pi", PiModelCatalogProbe.DirectoryFor(Tmp.Path),
            (_, _) => release.Task, time, NullLogger.Instance, CancellationToken.None);

        time.Advance(PiModelCatalogProbe.Deadline + TimeSpan.FromSeconds(1));
        await Assert.That(await run).IsNull();

        release.SetResult(late);
        for (var waited = 0; late.DisposeCalls == 0 && waited < 30_000; waited += 10) await Task.Delay(10);
        await Assert.That(late.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task A_spawn_failing_after_the_deadline_is_observed() {
        var time    = new FakeTimeProvider();
        var release = new TaskCompletionSource<IPiRpcProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger  = new Capacitor.Tests.Helpers.CapturingLogger();
        var run     = PiModelCatalogProbe.RunAsync("/opt/pi", PiModelCatalogProbe.DirectoryFor(Tmp.Path),
            (_, _) => release.Task, time, logger, CancellationToken.None);

        time.Advance(PiModelCatalogProbe.Deadline + TimeSpan.FromSeconds(1));
        await Assert.That(await run).IsNull();

        release.SetException(new InvalidOperationException("late start failure"));
        for (var waited = 0; !logger.Warnings.Any(w => w.Contains("could not start")) && waited < 30_000; waited += 10) await Task.Delay(10);
        await Assert.That(logger.Warnings.Any(w => w.Contains("could not start"))).IsTrue();
    }

    [Test]
    public async Task Invalid_envelope_returns_null_and_empty_catalog_returns_empty() {
        var bad = new FakePiRpcProcess { AutoStateResponse = null };
        bad.OnWrite = _ => bad.Push("""{"type":"response","command":"get_available_models","success":true}""");
        await Assert.That(await Run(bad, PiModelCatalogProbe.DirectoryFor(Tmp.Path))).IsNull();

        var empty = new FakePiRpcProcess { AutoStateResponse = null };
        empty.OnWrite = _ => empty.Push("""{"type":"response","command":"get_available_models","success":true,"data":{"models":[]}}""");
        var result = await Run(empty, PiModelCatalogProbe.DirectoryFor(Tmp.Path));
        await Assert.That(result).IsNotNull();
        await Assert.That(result!).IsEmpty();
    }

    [Test]
    public async Task Probe_directory_is_created_when_missing_and_emptied_when_it_holds_leftovers() {
        var dir = PiModelCatalogProbe.DirectoryFor(Tmp.Path);
        PiModelCatalogProbe.PrepareDirectory(dir);
        await Assert.That(Directory.Exists(dir)).IsTrue();

        Tmp.CreateFile(["pi-probe", "leftover"], "x");
        Tmp.CreateDir("pi-probe", ".pi", "commands");
        PiModelCatalogProbe.PrepareDirectory(dir);

        await Assert.That(Directory.Exists(dir)).IsTrue();
        await Assert.That(Directory.EnumerateFileSystemEntries(dir)).IsEmpty();
    }

    [Test]
    public async Task Emptying_the_probe_directory_never_follows_a_link() {
        Skip.When(OperatingSystem.IsWindows(), "Creating a symlink needs a privilege Windows runners lack.");
        var outside = Path.Combine(Tmp.Path, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "nested"));
        File.WriteAllText(Path.Combine(outside, "keep"), "x");
        File.WriteAllText(Path.Combine(outside, "nested", "keep"), "x");

        var dir = PiModelCatalogProbe.DirectoryFor(Path.Combine(Tmp.Path, "state"));
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        Directory.CreateSymbolicLink(dir, outside);
        PiModelCatalogProbe.PrepareDirectory(dir);

        await Assert.That(new DirectoryInfo(dir).LinkTarget).IsNull();
        await Assert.That(Directory.EnumerateFileSystemEntries(dir)).IsEmpty();
        await Assert.That(File.Exists(Path.Combine(outside, "keep"))).IsTrue();

        Directory.CreateSymbolicLink(Path.Combine(dir, "inner"), outside);
        PiModelCatalogProbe.PrepareDirectory(dir);

        await Assert.That(Directory.EnumerateFileSystemEntries(dir)).IsEmpty();
        await Assert.That(File.Exists(Path.Combine(outside, "nested", "keep"))).IsTrue();
    }

    [Test]
    public async Task Factory_probes_nothing_when_pi_is_not_installed() {
        var factory = new PiRpcHostedAgentRuntimeFactory(new DaemonConfig(), NullLoggerFactory.Instance, TimeProvider.System,
            processSource: (_, _) => throw new InvalidOperationException("must not spawn"),
            binaryExists: _ => false);

        await Assert.That(await factory.ProbeModelsAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Factory_fingerprints_pi_auth_and_models_files() {
        var paths   = new Capacitor.Cli.Core.Harness.Pi.PiPaths(new Capacitor.Cli.Core.UserHome(Tmp.Path), null);
        var factory = new PiRpcHostedAgentRuntimeFactory(new DaemonConfig(), NullLoggerFactory.Instance, TimeProvider.System, paths: paths);

        await Assert.That(((IHostedAgentRuntimeFactory) factory).CatalogFingerprintPaths)
            .IsEquivalentTo([paths.AuthJson, paths.ModelsJson], CollectionOrdering.Matching);
    }

    /// The fake pi emulates Pi's EOF-as-shutdown: after reading the command it waits a second and
    /// answers only if stdin has not reached EOF, so a probe that closes stdin right after writing gets
    /// no response and fails here with a null catalog.
    [Test]
    public async Task Real_child_answers_only_when_stdin_stays_open_until_the_response() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary is a POSIX shell script.");
        var cwdFile = Tmp.PathTo("cwd");
        var eofFile = Tmp.PathTo("eof");
        // A background reader marks stdin EOF; `read -t` cannot tell EOF from a timeout on bash 3.2.
        var pi = Tmp.CreateExecutable("pi", $$"""
            #!/bin/sh
            pwd -P > "{{cwdFile}}"
            echo '{"type":"noise"}'
            read cmd || exit 3
            { while read _; do :; done; : > "{{eofFile}}"; } 0<&0 &
            sleep 1
            [ -e "{{eofFile}}" ] && exit 4
            echo '{{Response}}'
            wait
            exit 0
            """);
        var probeDir = PiModelCatalogProbe.DirectoryFor(Tmp.Path);
        int pid = 0;
        string? identity = null;

        var models = await PiModelCatalogProbe.RunAsync(pi, probeDir,
            (psi, _) => {
                var p = new PiRpcProcess(psi, NullLogger<PiRpcProcess>.Instance, TimeProvider.System);
                pid = p.Pid;
                identity = PidIdentity.Capture(pid);
                return Task.FromResult<IPiRpcProcess>(p);
            },
            TimeProvider.System, NullLogger.Instance, CancellationToken.None);

        await Assert.That(models!.Single().Value).IsEqualTo("anthropic/claude-opus-5");
        await Assert.That(File.ReadAllText(cwdFile).Trim()).IsEqualTo(Tmp.GetResolvedPath("pi-probe"));
        await PidIdentity.WaitUntilGoneAsync(pid, identity!, TimeSpan.FromSeconds(5));
    }
}
