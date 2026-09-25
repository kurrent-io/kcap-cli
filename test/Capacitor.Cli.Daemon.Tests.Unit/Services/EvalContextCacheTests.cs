using System.Runtime.Versioning;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>No test runs a question, so no harness is reachable.</summary>
public class EvalContextCacheTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome]       public required TempHome       Home   { get; init; }
    [TempDir]        public required TempDir        Tmp    { get; init; }

    readonly EvidenceServerStub _stub = new();
    readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public void Dispose() => _stub.Dispose();

    sealed class NoopLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    string RunRoot => Tmp.PathTo("runs");

    int RunDirectories() => Directory.Exists(RunRoot) ? Directory.GetDirectories(RunRoot, EvidenceRunContext.DirectoryPrefix + "*").Length : 0;

    (ServerConnection Connection, EvalContextCache Cache) Daemon() {
        const string ad = """{"max_tool_calls":48,"judge_byte_budget_bytes":600000,"page_budget_bytes":65536,"one_shot_limit_chars":400000,"retrospective_evidence_bytes":200000,"coverage_policy_version":"coverage-v2"}""";
        _stub.Catalog(ad, "[]", """[{"category":"safety","id":"q1","title":"t","question_text":"q","prompt":"P {TRACE_JSON}","prompt_version":"3","needs_tools":false}]""");
        var issued   = _time.GetUtcNow();
        var manifest = EvidenceServerStub.Manifest("v1", "tok", null, issued, issued.AddMinutes(30), [EvidenceServerStub.Source(EvidenceServerStub.RootSource, 0, 124_999, 1)]);
        _stub.FreshScope(manifest);
        _stub.CursorPage("tok", manifest);
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(EvidenceServerStub.RootSource, [(0, 0, 124_999)]));
        _stub.Route("GET", "evidence-calls/summary", 200, EvidenceServerStub.Summary());
        _stub.Route("POST", "evals/v4", 200, "{}");

        var config     = new DaemonConfig { Name = "t", ServerUrl = _stub.Url, ConfigRoot = Config.Root, Profiles = Resolutions.At(_stub.Url, Config.Root) };
        var connection = new ServerConnection(config, AuthFixtures.NewTokenStore(Config.Root), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System);
        var cache      = new EvalContextCache(_time);
        _ = new EvalRunner(connection, cache, TestHarnesses.Under(Home, TestBinaries.None), config, new FixedCapacitorHttpClient(), new NoopLifetime(),
            NullLogger<EvalRunner>.Instance, _time) { TempRoot = RunRoot };
        return (connection, cache);
    }

    static string RunDirectoryOf(EvalContextCache cache, string runId) {
        using var lease = cache.LeaseEvidence(runId)!;
        return lease.Setup.Context.RunDirectory;
    }

    static PrepareEvalCommand Prepare(string runId) =>
        new(runId, EvidenceServerStub.SessionId, "sonnet", false, null, [new EvalQuestionDto { Category = "safety", Id = "q1", Text = "q1", Prompt = "q1" }]);

    [Test]
    public async Task Cancel_removes_the_run_directory() {
        var (connection, cache) = Daemon();
        await connection.PrepareEvalHandler!(Prepare("run-1"));
        await Assert.That(RunDirectories()).IsEqualTo(1);

        await connection.CancelEvalHandler!(new CancelEvalCommand("run-1"));

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(RunDirectories()).IsEqualTo(0);
    }

    [Test]
    public async Task Finalize_removes_the_run_directory_whatever_it_returns() {
        var (connection, cache) = Daemon();
        await connection.PrepareEvalHandler!(Prepare("run-1"));
        await Assert.That(RunDirectories()).IsEqualTo(1);

        var result = await connection.FinalizeEvalV2Handler!(new FinalizeEvalV2Command("run-1", [], [], "sonnet"));

        await Assert.That(result.Success).IsFalse();
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(RunDirectories()).IsEqualTo(0);
    }

    [Test]
    public async Task An_idle_entry_is_disposed_when_read_after_it_expires() {
        var (connection, cache) = Daemon();
        await connection.PrepareEvalHandler!(Prepare("run-1"));
        await Assert.That(RunDirectories()).IsEqualTo(1);

        // Moves the clock without firing the sweep, so the read is what removes it.
        _time.AdjustTime(_time.GetUtcNow() + TimeSpan.FromMinutes(31));

        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(cache.LeaseEvidence("run-1")).IsNull();
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(RunDirectories()).IsEqualTo(0);
    }

    [Test]
    public async Task The_sweep_disposes_what_it_expires() {
        var (connection, cache) = Daemon();
        await connection.PrepareEvalHandler!(Prepare("run-1"));
        await Assert.That(RunDirectories()).IsEqualTo(1);

        _time.Advance(TimeSpan.FromMinutes(35));

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(RunDirectories()).IsEqualTo(0);
    }

    [Test]
    public async Task A_second_prepare_under_the_same_id_disposes_the_first() {
        var (connection, cache) = Daemon();
        await connection.PrepareEvalHandler!(Prepare("run-1"));
        var first = RunDirectoryOf(cache, "run-1");

        await connection.PrepareEvalHandler!(Prepare("run-1"));

        await Assert.That(Directory.Exists(first)).IsFalse();
        await Assert.That(RunDirectoryOf(cache, "run-1")).IsNotEqualTo(first);
        await Assert.That(RunDirectories()).IsEqualTo(1);
    }

    [Test]
    public async Task Disposing_the_cache_with_two_prepared_runs_removes_both_directories() {
        var (connection, cache) = Daemon();
        await connection.PrepareEvalHandler!(Prepare("run-1"));
        await connection.PrepareEvalHandler!(Prepare("run-2"));
        await Assert.That(RunDirectories()).IsEqualTo(2);

        await cache.DisposeAsync();

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(RunDirectories()).IsEqualTo(0);
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_directory_that_cannot_be_removed_does_not_stop_the_others_going() {
        Skip.When(OperatingSystem.IsWindows(), "relies on POSIX directory permissions");
        var (connection, cache) = Daemon();
        await connection.PrepareEvalHandler!(Prepare("run-1"));
        await connection.PrepareEvalHandler!(Prepare("run-2"));
        var stuck = RunDirectoryOf(cache, "run-1");
        var child = Path.Combine(stuck, "locked");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "f"), "x");
        File.SetUnixFileMode(child, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try {
            await cache.DisposeAsync();

            await Assert.That(cache.Count).IsEqualTo(0);
            await Assert.That(Directory.Exists(stuck)).IsTrue();
            await Assert.That(RunDirectories()).IsEqualTo(1);
        } finally {
            File.SetUnixFileMode(child, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task A_leased_run_is_cancelled_when_removed_and_disposed_when_its_lease_ends() {
        var (connection, cache) = Daemon();
        await connection.PrepareEvalHandler!(Prepare("run-1"));
        var lease = cache.LeaseEvidence("run-1")!;

        await connection.CancelEvalHandler!(new CancelEvalCommand("run-1"));

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.LeaseEvidence("run-1")).IsNull();
        await Assert.That(lease.Cancelled.IsCancellationRequested).IsTrue();
        await Assert.That(Directory.Exists(lease.Setup.Context.RunDirectory)).IsTrue();

        lease.Dispose();

        await Assert.That(RunDirectories()).IsEqualTo(0);
    }

    [Test]
    public async Task A_second_prepare_cancels_a_leased_run_and_disposes_it_once_its_lease_ends() {
        var (connection, cache) = Daemon();
        await connection.PrepareEvalHandler!(Prepare("run-1"));
        var lease = cache.LeaseEvidence("run-1")!;

        await connection.PrepareEvalHandler!(Prepare("run-1"));

        await Assert.That(lease.Cancelled.IsCancellationRequested).IsTrue();
        await Assert.That(RunDirectories()).IsEqualTo(2);
        lease.Dispose();
        await Assert.That(Directory.Exists(lease.Setup.Context.RunDirectory)).IsFalse();
        await Assert.That(RunDirectories()).IsEqualTo(1);
    }

    [Test]
    public async Task A_leased_run_is_not_expired_and_its_idle_time_starts_when_the_lease_ends() {
        var (connection, cache) = Daemon();
        await connection.PrepareEvalHandler!(Prepare("run-1"));
        var lease = cache.LeaseEvidence("run-1")!;

        _time.Advance(TimeSpan.FromMinutes(35));

        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(lease.Cancelled.IsCancellationRequested).IsFalse();
        lease.Dispose();
        _time.Advance(TimeSpan.FromMinutes(20));
        await Assert.That(cache.Count).IsEqualTo(1);
        _time.Advance(TimeSpan.FromMinutes(15));
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(RunDirectories()).IsEqualTo(0);
    }
}
