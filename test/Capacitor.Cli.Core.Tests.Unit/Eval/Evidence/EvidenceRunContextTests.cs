using System.Runtime.Versioning;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Evidence;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>The run directory is owner-only before any file exists, its files are owner-only from creation, it is gone
/// after disposal, a crash's leftovers are swept within bounds, and retained facts drain once or not at all.</summary>
public class EvidenceRunContextTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const UnixFileMode OwnerOnlyDir  = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode OwnerOnlyFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    string Stale(string name, TimeSpan age, UnixFileMode mode = OwnerOnlyDir) {
        var dir = Path.Combine(Tmp.Path, EvidenceRunContext.DirectoryPrefix + name);
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, mode);
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - age);
        return dir;
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task The_directory_is_0700_its_files_0600_and_disposal_removes_them() {
        Skip.When(OperatingSystem.IsWindows(), "Unix modes do not apply on Windows");

        var ctx = EvidenceRunContext.Create("run-1", Tmp.Path);
        await Assert.That(Path.GetFileName(ctx.RunDirectory)).StartsWith(EvidenceRunContext.DirectoryPrefix);
        await Assert.That(Path.GetFileName(ctx.RunDirectory).Length).IsEqualTo(EvidenceRunContext.DirectoryPrefix.Length + 16);
        await Assert.That(File.GetUnixFileMode(ctx.RunDirectory)).IsEqualTo(OwnerOnlyDir);

        await using (var run = OwnerOnlyFile.CreateNew(ctx.RunFilePath(1))) run.WriteByte(1);
        await using (var ledger = OwnerOnlyFile.CreateNew(ctx.LedgerFilePath(1))) ledger.WriteByte(1);
        await Assert.That(File.GetUnixFileMode(ctx.RunFilePath(1))).IsEqualTo(OwnerOnlyFileMode);
        await Assert.That(File.GetUnixFileMode(ctx.LedgerFilePath(1))).IsEqualTo(OwnerOnlyFileMode);
        await Assert.That(Path.GetFileName(ctx.RunFilePath(3))).IsEqualTo("q3.run.json");
        await Assert.That(Path.GetFileName(ctx.LedgerFilePath(3))).IsEqualTo("q3.ledger.jsonl");

        await ctx.DisposeAsync();
        await ctx.DisposeAsync();
        await Assert.That(Directory.Exists(ctx.RunDirectory)).IsFalse();
    }

    /// <summary>A sibling made with a plain CreateDirectory shows this process's default mode; when that default is
    /// already owner-only the comparison proves nothing, so the test skips.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task With_the_windows_flag_no_unix_mode_is_set_and_files_still_open_create_new() {
        Skip.When(OperatingSystem.IsWindows(), "the flag is exercised on a Unix host");
        var control = Tmp.CreateDir("control").Path;
        Skip.When(File.GetUnixFileMode(control) == OwnerOnlyDir, "the process umask already yields owner-only directories");

        await using var ctx = EvidenceRunContext.Create("run-1", Tmp.Path, isWindows: true);

        await Assert.That(File.GetUnixFileMode(ctx.RunDirectory)).IsEqualTo(File.GetUnixFileMode(control));
        await using (OwnerOnlyFile.CreateNew(ctx.RunFilePath(1))) { }
        await Assert.That(() => OwnerOnlyFile.CreateNew(ctx.RunFilePath(1))).Throws<IOException>();
    }

    [Test]
    public async Task The_sweep_removes_a_directory_25_hours_old_and_keeps_one_an_hour_old() {
        var old    = Stale("aaaaaaaaaaaaaaaa", TimeSpan.FromHours(25));
        var recent = Stale("bbbbbbbbbbbbbbbb", TimeSpan.FromHours(1));
        var other  = Tmp.CreateDir("not-a-run").Path;
        Directory.SetLastWriteTimeUtc(other, DateTime.UtcNow - TimeSpan.FromDays(3));

        var removed = EvidenceRunContext.SweepStale(Tmp.Path, new FakeTimeProvider(DateTimeOffset.UtcNow), _ => { });

        await Assert.That(removed).IsEqualTo(1);
        await Assert.That(Directory.Exists(old)).IsFalse();
        await Assert.That(Directory.Exists(recent)).IsTrue();
        await Assert.That(Directory.Exists(other)).IsTrue();
    }

    [Test]
    public async Task The_sweep_stops_at_its_bound() {
        for (var i = 0; i < EvidenceRunContext.MaxStaleSweep + 1; i++) Stale($"{i:x16}", TimeSpan.FromDays(2));

        var removed = EvidenceRunContext.SweepStale(Tmp.Path, new FakeTimeProvider(DateTimeOffset.UtcNow), _ => { });

        await Assert.That(removed).IsEqualTo(EvidenceRunContext.MaxStaleSweep);
        await Assert.That(Directory.EnumerateDirectories(Tmp.Path, EvidenceRunContext.DirectoryPrefix + "*").Count()).IsEqualTo(1);
    }

    [Test]
    public async Task The_sweep_skips_a_directory_that_is_not_owner_only() {
        Skip.When(OperatingSystem.IsWindows(), "Unix modes do not apply on Windows");
        var shared = Stale("cccccccccccccccc", TimeSpan.FromDays(2), OwnerOnlyDir | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        var removed = EvidenceRunContext.SweepStale(Tmp.Path, new FakeTimeProvider(DateTimeOffset.UtcNow), _ => { });

        await Assert.That(removed).IsEqualTo(0);
        await Assert.That(Directory.Exists(shared)).IsTrue();
    }

    [Test]
    public async Task Retained_facts_drain_once_after_buffering_and_a_discard_posts_nothing() {
        await using var ctx = EvidenceRunContext.Create("run-1", Tmp.Path);
        var observer = new RecordingEvalObserver();
        var posted   = new List<string>();
        Task<bool> Post(string category, EvalService.RetainedFact fact, CancellationToken _) { posted.Add($"{category}:{fact.Fact}"); return Task.FromResult(true); }

        ctx.BufferRetainedFact("safety", new EvalService.RetainedFact("dropped", null, null));
        ctx.DiscardRetainedFacts();
        await Assert.That(await ctx.DrainRetainedFactsAsync(Post, observer, CancellationToken.None)).IsEqualTo(0);

        ctx.BufferRetainedFact("safety", new EvalService.RetainedFact("f1", null, null));
        ctx.BufferRetainedFact("quality", new EvalService.RetainedFact("f2", ["claude"], null));
        await Assert.That(ctx.BufferedFactCount).IsEqualTo(2);
        await Assert.That(await ctx.DrainRetainedFactsAsync(Post, observer, CancellationToken.None)).IsEqualTo(2);
        await Assert.That(await ctx.DrainRetainedFactsAsync(Post, observer, CancellationToken.None)).IsEqualTo(0);

        await Assert.That(posted).IsEquivalentTo(["safety:f1", "quality:f2"]);
        await Assert.That(observer.FactsRetained).IsEquivalentTo([("safety", "f1"), ("quality", "f2")]);
    }

    [Test]
    public async Task Disposal_discards_the_buffer() {
        var ctx = EvidenceRunContext.Create("run-1", Tmp.Path);
        ctx.BufferRetainedFact("safety", new EvalService.RetainedFact("f", null, null));
        await ctx.DisposeAsync();
        await Assert.That(ctx.BufferedFactCount).IsEqualTo(0);
    }
}
