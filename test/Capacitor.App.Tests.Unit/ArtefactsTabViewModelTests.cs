using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The tab's list and reader: what a read lists, in what order, which document is open, how a row
/// is found from a path, and the lease that keeps a stale read from applying. Every read settles
/// through Dispatcher.UIThread, so every test runs under RunOnUiAsync and carries
/// [NotInParallel("AvaloniaSession")].
public class ArtefactsTabViewModelTests {
    const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    sealed class Harness {
        public FakePlanArtifactSource Source { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public PlanActivity Activity { get; } = new();
        public Dictionary<string, byte[]> Disk { get; } = new(StringComparer.Ordinal);
        public RecordingOpener Opener { get; } = new();
        public ArtefactsTabViewModel Vm { get; }

        public Harness(bool realFiles = false, IPageViewer? pageViewer = null) => Vm = new ArtefactsTabViewModel(Source, Activity, Time,
            realFiles ? null : path => Disk.TryGetValue(path.Replace('\\', '/'), out var bytes) ? new MemoryStream(bytes) : null,
            Opener, pageViewer);

        public async Task SwitchAsync(string? sessionId, string? root = "/repo") {
            Vm.SwitchSession(sessionId, root);
            await SettledAsync();
        }

        public Task SettledAsync() =>
            Task.WhenAll(Vm.PendingReadForTesting ?? Task.CompletedTask, Vm.PendingPagesForTesting ?? Task.CompletedTask);

        public void WritePlan(string callId = "c1") => Activity.Apply([new ChatProjectionResult([
            new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: callId, ToolName: PlanToolNames.DeclareDocument),
            new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: callId),
        ], [], [])]);

        public void PublishPage(string callId = "p1") => Activity.Apply([new ChatProjectionResult([
            new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: callId, ToolName: "mcp__plugin_kcap_kcap-pages__publish_page"),
            new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: callId),
        ], [], [])]);
    }

    static ArtefactDto Page(string id, string title, DateTimeOffset updatedAt, string? url = null) =>
        new(id, title, "u1", "org", 1, updatedAt, true, url ?? $"https://kcap.test/pages/{id}");

    static SessionPagesRead Pages(params ArtefactDto[] pages) => new(SessionPlansReadKind.Ready, pages);

    static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static PlanArtifactDto Doc(string id, string kind, string path, string content = "# x", string? hash = null, bool primary = false,
            string source = "declared", string state = "ok") => new() {
        ArtifactId = id, Kind = kind, Title = path, Source = source, SessionId = SessionA, Path = path, Content = content,
        ContentState = state, IsComplete = true, IsConfirmed = true, ContentHash = hash ?? Sha(content), Version = 1,
        DiscoveredAt = DateTimeOffset.UnixEpoch, Confidence = "high", Reason = source, IsPrimary = primary,
    };

    static PlanArtifactsRead Ready(params PlanArtifactDto[] docs) =>
        new(SessionPlansReadKind.Ready, new PlanArtifactsResponseDto { Artifacts = [.. docs] });

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false)]
    [Arguments(true)]
    public Task Repeated_refreshes_of_a_blocked_document_queue_only_one_follow_up(bool bodyUnavailable) => RunOnUiAsync(async () => {
        var source = new FakePlanArtifactSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var opens = 0;
        var vm = new ArtefactsTabViewModel(source, new PlanActivity(), TimeProvider.System, _ => {
            Interlocked.Increment(ref opens);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return null;
        });
        var reads = new List<Task>();
        try {
            var document = Doc("1", "plan", "plan.md");
            source.Enqueue(Ready(document));
            vm.SwitchSession(SessionA, "/repo");
            await vm.PendingReadForTesting!;
            vm.OpenDocument("plan.md");
            var first = vm.PendingDriftForTesting!;
            reads.Add(first);
            await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(entered.IsSet).IsTrue();
            for (var i = 0; i < 20; i++) {
                source.Enqueue(Ready(bodyUnavailable ? Doc("1", "plan", "plan.md", state: "unavailable") : document));
                vm.Refresh();
                await vm.PendingReadForTesting!;
                reads.Add(vm.PendingDriftForTesting!);
            }
            await Assert.That(ReferenceEquals(vm.PendingDriftForTesting, first)).IsTrue();
            await Assert.That(Volatile.Read(ref opens)).IsEqualTo(1);
            release.Set();
            await first;
            await WaitUntilAsync(() => Volatile.Read(ref opens) == 2, what: "one queued drift recheck");
            reads.Add(vm.PendingDriftForTesting!);
            await vm.PendingDriftForTesting!;
            await Assert.That(Volatile.Read(ref opens)).IsEqualTo(2);
            await Assert.That(vm.Reader!.Notice).IsEqualTo(bodyUnavailable
                ? "No body is available: this document was declared by hash alone."
                : "Working copy is gone: nothing is at this path any more.");
        } finally {
            release.Set();
            await Task.WhenAll(reads);
            Console.WriteLine($"DRIFT_REFRESH refreshes=20 attempts={reads.Distinct().Count()} opens={Volatile.Read(ref opens)}");
            await (vm.PendingDriftForTesting ?? Task.CompletedTask);
            await vm.TeardownAsync();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_refresh_keeps_the_drift_notice_until_the_new_hash_settles() => RunOnUiAsync(async () => {
        var source = new FakePlanArtifactSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var block = false;
        var vm = new ArtefactsTabViewModel(source, new PlanActivity(), TimeProvider.System, _ => {
            if (!block) return null;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new MemoryStream(Encoding.UTF8.GetBytes("changed"));
        });
        try {
            var document = Doc("1", "plan", "plan.md");
            source.Enqueue(Ready(document));
            vm.SwitchSession(SessionA, "/repo");
            await vm.PendingReadForTesting!;
            vm.OpenDocument("plan.md");
            await vm.PendingDriftForTesting!;
            var notice = vm.Reader!.Notice;
            await Assert.That(notice).IsEqualTo("Working copy is gone: nothing is at this path any more.");
            block = true;
            source.Enqueue(Ready(document));
            vm.Refresh();
            await vm.PendingReadForTesting!;
            await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(entered.IsSet).IsTrue();
            await Assert.That(vm.Reader!.Notice).IsEqualTo(notice);
            release.Set();
            await vm.PendingDriftForTesting!;
            await Assert.That(vm.Reader!.Notice).IsEqualTo("Working copy has changed since this was declared.");
        } finally {
            release.Set();
            await (vm.PendingDriftForTesting ?? Task.CompletedTask);
            await vm.TeardownAsync();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Teardown_returns_while_a_working_copy_read_is_stalled() => RunOnUiAsync(async () => {
        var source = new FakePlanArtifactSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var vm = new ArtefactsTabViewModel(source, new PlanActivity(), TimeProvider.System, _ => {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return null;
        });
        Task? pending = null;
        try {
            source.Enqueue(Ready(Doc("1", "plan", "plan.md")));
            vm.SwitchSession(SessionA, "/repo");
            await vm.PendingReadForTesting!;
            vm.OpenDocument("plan.md");
            pending = vm.PendingDriftForTesting!;
            await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(entered.IsSet).IsTrue();
            var reader = vm.Reader;
            await vm.TeardownAsync().WaitAsync(TimeSpan.FromSeconds(2));
            release.Set();
            await pending;
            await Assert.That(vm.Reader).IsSameReferenceAs(reader);
            await Assert.That(vm.Reader!.Notice).IsNull();
        } finally {
            release.Set();
            if (pending is not null) await pending;
            await vm.TeardownAsync();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_slow_drift_read_leaves_the_ui_free_and_cannot_replace_a_new_selection() {
        await RunOnUiAsync(async () => {
            var source = new FakePlanArtifactSource();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var readOnUi = false;
            var vm = new ArtefactsTabViewModel(source, new PlanActivity(), TimeProvider.System, path => {
                readOnUi |= Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
                if (!path.EndsWith("slow.md", StringComparison.Ordinal)) return null;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return new MemoryStream(Encoding.UTF8.GetBytes("changed"));
            });
            try {
                source.Enqueue(Ready(Doc("1", "plan", "slow.md", "# slow"), Doc("2", "spec", "fast.md", "# fast")));
                vm.SwitchSession(SessionA, "/repo");
                await vm.PendingReadForTesting!;
                await vm.SelectCommand.Execute(vm.Documents.Single(row => row.Path == "slow.md"));
                await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
                await Assert.That(entered.IsSet).IsTrue();
                var oldRead = vm.PendingDriftForTesting!;
                await Assert.That(await vm.SelectCommand.CanExecute.FirstAsync()).IsTrue();
                await vm.SelectCommand.Execute(vm.Documents.Single(row => row.Path == "fast.md"));
                var reader = vm.Reader;
                await vm.PendingDriftForTesting!.WaitAsync(TimeSpan.FromSeconds(2));
                await Assert.That(vm.Reader!.Notice).IsEqualTo("Working copy is gone: nothing is at this path any more.");
                await Assert.That(vm.Reader).IsSameReferenceAs(reader);
                release.Set();
                await oldRead;
                await Assert.That(readOnUi).IsFalse();
                await Assert.That(vm.Selected!.Path).IsEqualTo("fast.md");
                await Assert.That(vm.Reader!.Notice).IsEqualTo("Working copy is gone: nothing is at this path any more.");
                vm.SwitchSession(SessionB, "/other");
                await Assert.That(vm.Reader).IsNull();
            } finally {
                release.Set();
                await vm.TeardownAsync();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_read_lists_documents_plan_spec_design_other_primary_first_and_deduped_by_hash_then_path() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Vm.HasAny).IsFalse();

            h.Source.Enqueue(Ready(
                Doc("1", "design", "docs/d.md", "# d"),
                Doc("2", "plan", "docs/p2.md", "# p2"),
                Doc("3", "plan", "docs/p1.md", "# p1", primary: true),
                Doc("4", "design", "docs/d.md", "# d"),
                Doc("5", "checklist", "todo.md", "- [ ] x"),
                Doc("6", "spec", "docs/s.md", "# s", source: "repo_file"),
                Doc("7", "spec", "docs/s.md", "# s", source: "declared")));
            await h.SwitchAsync(SessionA);

            await Assert.That(h.Source.Requested).IsEquivalentTo(new[] { SessionA });
            await Assert.That(h.Vm.HasAny).IsTrue();
            await Assert.That(h.Vm.Documents.Select(d => d.Path))
                .IsEquivalentTo(new[] { "docs/p1.md", "docs/p2.md", "docs/s.md", "docs/d.md" }, CollectionOrdering.Matching);
            await Assert.That(h.Vm.Documents[2].Source).IsEqualTo("declared");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Selecting_a_row_builds_the_reader_and_marks_it_and_a_path_opens_its_row_by_suffix() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var opens = 0;
            h.Vm.OpenRequested += () => opens++;
            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md", "# p"), Doc("2", "design", @"docs\d.md", "# d")));
            await h.SwitchAsync(SessionA);
            await Assert.That(h.Vm.Selected).IsNull();
            await Assert.That(h.Vm.Reader).IsNull();

            await h.Vm.SelectCommand.Execute(h.Vm.Documents[0]);

            await h.Vm.PendingDriftForTesting!;
            await Assert.That(h.Vm.Selected).IsSameReferenceAs(h.Vm.Documents[0]);
            await Assert.That(h.Vm.Documents[0].IsSelected).IsTrue();
            await Assert.That(h.Vm.Reader!.Body).IsEqualTo("# p");
            await Assert.That(opens).IsEqualTo(0);

            await Assert.That(h.Vm.OpenDocument("/Users/me/repo/docs/d.md")).IsTrue();
            await Assert.That(h.Vm.Selected!.Path).IsEqualTo(@"docs\d.md");
            await Assert.That(h.Vm.Documents[0].IsSelected).IsFalse();
            await Assert.That(opens).IsEqualTo(1);

            await Assert.That(h.Vm.OpenDocument("docs/nope.md")).IsFalse();
            await Assert.That(opens).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Drift_compares_the_working_copy_hash_for_a_declared_document_only() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Disk["/repo/docs/same.md"] = Encoding.UTF8.GetBytes("# same");
            h.Disk["/repo/docs/changed.md"] = Encoding.UTF8.GetBytes("# changed on disk");
            h.Source.Enqueue(Ready(
                Doc("1", "plan", "docs/same.md", "# same"),
                Doc("2", "spec", "docs/changed.md", "# changed"),
                Doc("3", "design", "docs/gone.md", "# gone"),
                Doc("4", "design", "docs/found.md", "# found", source: "repo_file")));
            await h.SwitchAsync(SessionA, root: "/repo");

            await h.Vm.SelectCommand.Execute(h.Vm.Documents[0]);

            await h.Vm.PendingDriftForTesting!;
            await Assert.That(h.Vm.Reader!.HasNotice).IsFalse();
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[1]);
            await h.Vm.PendingDriftForTesting!;
            await Assert.That(h.Vm.Reader!.Notice).IsEqualTo("Working copy has changed since this was declared.");
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[3]);
            await h.Vm.PendingDriftForTesting!;
            await Assert.That(h.Vm.Reader!.Notice).IsEqualTo("Working copy is gone: nothing is at this path any more.");
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[2]);
            await h.Vm.PendingDriftForTesting!;
            await Assert.That(h.Vm.Reader!.HasNotice).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Hashless_entries_list_by_path_and_a_hashless_duplicate_path_lists_once() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(
                Doc("1", "plan", "docs/a.md", hash: ""),
                Doc("2", "spec", "docs/b.md", hash: ""),
                Doc("3", "spec", "docs/b.md", hash: "", source: "repo_file")));
            await h.SwitchAsync(SessionA);

            await Assert.That(h.Vm.Documents.Select(d => d.Path).ToArray()).IsEquivalentTo(new[] { "docs/a.md", "docs/b.md" });
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_path_outside_the_checkout_is_never_read_for_drift() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Disk["/repo/../outside.md"] = Encoding.UTF8.GetBytes("# x");
            h.Disk["/repo/outside.md"] = Encoding.UTF8.GetBytes("# x");
            h.Disk["/etc/hosts"] = Encoding.UTF8.GetBytes("# x");
            h.Source.Enqueue(Ready(Doc("1", "plan", "../outside.md"), Doc("2", "spec", "/etc/hosts")));
            await h.SwitchAsync(SessionA, root: "/repo");

            foreach (var document in h.Vm.Documents.ToArray()) {
                await h.Vm.SelectCommand.Execute(document);
                await h.Vm.PendingDriftForTesting!;
                await Assert.That(h.Vm.Reader!.HasNotice).IsFalse();
            }
            await h.Vm.TeardownAsync();
        });
    }

    /// A remote session has no root; the reader then says nothing about drift.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_root_there_is_no_drift_verdict() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md", "# p")));
            await h.SwitchAsync(SessionA, root: null);
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[0]);
            await h.Vm.PendingDriftForTesting!;
            await Assert.That(h.Vm.Reader!.HasNotice).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_write_in_the_transcript_reads_at_once_and_again_after_the_settle_delay() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await h.SwitchAsync(SessionA);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(1);

            h.WritePlan();
            await h.SettledAsync();
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);

            h.Time.Advance(PlanSectionViewModel.SettleDelay);
            await h.SettledAsync();
            await Assert.That(h.Source.Requested.Count).IsEqualTo(3);
            await h.Vm.TeardownAsync();
        });
    }

    /// The read in flight belongs to the session that started it: when the session changes before
    /// it lands, its rows never show, and a new session with nothing clears the list.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stale_read_never_applies_and_an_empty_session_clears_the_list() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md")));
            await h.SwitchAsync(SessionA);
            await Assert.That(h.Vm.HasAny).IsTrue();

            var gate = h.Source.Gate(ignoreCancellation: true);
            h.Vm.Refresh();
            var stale = h.Vm.PendingReadForTesting!;

            h.Source.Enqueue(Ready());
            await h.SwitchAsync(SessionB);
            gate.SetResult(Ready(Doc("2", "spec", "docs/a-only.md")));
            await stale;

            await Assert.That(h.Vm.HasAny).IsFalse();
            await Assert.That(h.Vm.Documents).IsEmpty();
            await Assert.That(h.Vm.Selected).IsNull();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_refresh_keeps_the_selection_when_the_document_is_still_listed() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md", "# p")));
            await h.SwitchAsync(SessionA);
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[0]);
            await h.Vm.PendingDriftForTesting!;

            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md", "# p v2"), Doc("2", "spec", "docs/s.md")));
            h.Vm.Refresh();
            await h.SettledAsync();

            await Assert.That(h.Vm.Selected!.Path).IsEqualTo("docs/p.md");
            await Assert.That(h.Vm.Reader!.Body).IsEqualTo("# p v2");
            await Assert.That(h.Vm.Documents.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_exact_path_wins_over_a_suffix_match_whatever_the_list_order() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/spec.md", "# a"), Doc("2", "design", "spec.md", "# b")));
            await h.SwitchAsync(SessionA);
            await Assert.That(h.Vm.Documents[0].Path).IsEqualTo("docs/spec.md");

            await Assert.That(h.Vm.OpenDocument("spec.md")).IsTrue();
            await Assert.That(h.Vm.Selected!.Path).IsEqualTo("spec.md");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Switch_session_is_loading_until_the_read_settles() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gate = h.Source.Gate();
            h.Vm.SwitchSession(SessionA, "/repo");

            await Assert.That(h.Vm.IsLoading).IsTrue();
            await Assert.That(h.Vm.HasAny).IsFalse();
            await Assert.That(h.Vm.EmptyReaderNote).IsEqualTo("Loading documents…");

            gate.SetResult(Ready());
            await h.SettledAsync();

            await Assert.That(h.Vm.IsLoading).IsFalse();
            await Assert.That(h.Vm.EmptyReaderNote).IsEqualTo("No documents yet.");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_open_before_the_first_read_lands_selects_the_row_when_it_does() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var opens = 0;
            h.Vm.OpenRequested += () => opens++;
            var gate = h.Source.Gate();
            h.Vm.SwitchSession(SessionA, "/repo");

            await Assert.That(h.Vm.OpenDocument("docs/p.md")).IsFalse();
            await Assert.That(opens).IsEqualTo(0);
            gate.SetResult(Ready(Doc("1", "plan", "docs/p.md", "# p")));
            await h.SettledAsync();

            await Assert.That(h.Vm.Selected!.Path).IsEqualTo("docs/p.md");
            await Assert.That(opens).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_switch_drops_the_pending_open() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gate = h.Source.Gate();
            h.Vm.SwitchSession(SessionA, "/repo");
            await Assert.That(h.Vm.OpenDocument("docs/p.md")).IsFalse();

            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md", "# p")));
            await h.SwitchAsync(SessionB);
            gate.TrySetResult(Ready());

            await Assert.That(h.Vm.Documents.Count).IsEqualTo(1);
            await Assert.That(h.Vm.Selected).IsNull();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_null_session_id_clears_the_list_and_starts_no_read() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md", "# p")));
            await h.SwitchAsync(SessionA);
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[0]);
            await h.Vm.PendingDriftForTesting!;

            await h.SwitchAsync(null);

            await Assert.That(h.Vm.HasAny).IsFalse();
            await Assert.That(h.Vm.Selected).IsNull();
            await Assert.That(h.Source.Requested.Count).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_read_is_reported_until_a_ready_one_lands() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(new PlanArtifactsRead(SessionPlansReadKind.Unreachable, null));
            await h.SwitchAsync(SessionA);
            await Assert.That(h.Vm.LastReadFailed).IsTrue();

            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md")));
            h.Vm.Refresh();
            await h.SettledAsync();
            await Assert.That(h.Vm.LastReadFailed).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_file_over_the_hash_ceiling_has_no_drift_verdict() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Disk["/repo/docs/big.md"] = new byte[4 * 1024 * 1024 + 1];
            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/big.md", "# big")));
            await h.SwitchAsync(SessionA, root: "/repo");

            await h.Vm.SelectCommand.Execute(h.Vm.Documents[0]);

            await h.Vm.PendingDriftForTesting!;
            await Assert.That(h.Vm.Reader!.HasNotice).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_symlink_that_leaves_the_checkout_is_never_read_for_drift() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var root = tmp.GetResolvedPath("repo");
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        var outside = tmp.CreateFile("outside.md", "# outside");
        File.CreateSymbolicLink(Path.Combine(root, "docs", "link.md"), outside);
        await RunOnUiAsync(async () => {
            var h = new Harness(realFiles: true);
            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/link.md", "# different")));
            await h.SwitchAsync(SessionA, root: root);

            await h.Vm.SelectCommand.Execute(h.Vm.Documents[0]);

            await h.Vm.PendingDriftForTesting!;
            await Assert.That(h.Vm.Reader!.HasNotice).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pages_the_server_lists_show_beside_the_documents_in_its_order_and_open_at_their_own_url() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var now = h.Time.GetUtcNow();
            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md")));
            h.Source.EnqueuePages(Pages(
                Page("new", "Findings", now - TimeSpan.FromHours(3)),
                Page("old", "Draft", now - TimeSpan.FromDays(2), url: "https://elsewhere.test/pages/old")));
            await h.SwitchAsync(SessionA);

            await Assert.That(h.Source.RequestedPages).IsEquivalentTo(new[] { SessionA });
            await Assert.That(h.Vm.Documents.Count).IsEqualTo(1);
            await Assert.That(h.Vm.Pages.Select(p => p.Title)).IsEquivalentTo(new[] { "Findings", "Draft" }, CollectionOrdering.Matching);
            await Assert.That(h.Vm.Pages[0].UpdatedLabel).IsEqualTo("Updated 3h ago");
            await Assert.That(h.Vm.HasPagesNote).IsFalse();

            await h.Vm.OpenPageCommand.Execute(h.Vm.Pages[1]);
            await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://elsewhere.test/pages/old" });
            await Assert.That(h.Vm.Selected).IsNull();
            await h.Vm.TeardownAsync();
        });
    }

    /// A page row opens through the page viewer it was given, with the page's id and listed URL, and never
    /// through the browser: an in-app viewer replaces the browser whole.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_given_page_viewer_opens_pages_instead_of_the_browser() {
        await RunOnUiAsync(async () => {
            var viewer = new RecordingPageViewer();
            var h = new Harness(pageViewer: viewer);
            h.Source.EnqueuePages(Pages(Page("p", "Report", h.Time.GetUtcNow(), url: "https://kcap.test/pages/p")));
            await h.SwitchAsync(SessionA);

            await h.Vm.OpenPageCommand.Execute(h.Vm.Pages[0]);
            await Assert.That(viewer.Opened).IsEquivalentTo(new[] { ("p", "https://kcap.test/pages/p") });
            await Assert.That(h.Opener.Opened).IsEmpty();
            await h.Vm.TeardownAsync();
        });
    }

    sealed class RecordingPageViewer : IPageViewer {
        public List<(string PageId, string Url)> Opened { get; } = [];
        public void Open(string pageId, string url) => Opened.Add((pageId, url));
    }

    /// The tab shows for a session whose only artefact is a page, and the reader says where pages open.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_with_only_pages_has_artefacts() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.EnqueuePages(Pages(Page("p", "Report", h.Time.GetUtcNow())));
            await h.SwitchAsync(SessionA);

            await Assert.That(h.Vm.HasDocuments).IsFalse();
            await Assert.That(h.Vm.HasAny).IsTrue();
            await Assert.That(h.Vm.EmptyReaderNote).IsEqualTo("Pages open in your browser.");
            await Assert.That(h.Vm.OpenDocument("docs/missing.md")).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pages_are_loading_until_their_read_settles() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gate = h.Source.GatePages();
            h.Vm.SwitchSession(SessionA, "/repo");

            await Assert.That(h.Vm.ShowsPagesSection).IsTrue();
            await Assert.That(h.Vm.IsLoadingPages).IsTrue();
            await Assert.That(h.Vm.PagesNote).IsEqualTo("Loading pages…");

            gate.SetResult(Pages());
            await h.SettledAsync();

            await Assert.That(h.Vm.IsLoadingPages).IsFalse();
            await Assert.That(h.Vm.PagesNote).IsEqualTo("No pages published from this session.");
            await h.Vm.TeardownAsync();
        });
    }

    /// A refused read (a session this viewer may not see, or a server without the route) and a
    /// signed-out one each say so, never "no pages", and list nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(SessionPlansReadKind.Unavailable, "Pages aren't available for this session.")]
    [Arguments(SessionPlansReadKind.SignedOut, "Sign in to see this session's pages.")]
    [Arguments(SessionPlansReadKind.Unreachable, "Couldn't reach the server to list pages.")]
    public async Task A_read_that_is_not_ready_is_told_apart_from_an_empty_list(SessionPlansReadKind kind, string note) {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.EnqueuePages(SessionPagesRead.Of(kind));
            await h.SwitchAsync(SessionA);

            await Assert.That(h.Vm.Pages).IsEmpty();
            await Assert.That(h.Vm.PagesNote).IsEqualTo(note);
            await Assert.That(h.Vm.LastReadFailed).IsEqualTo(kind == SessionPlansReadKind.Unreachable);
            await h.Vm.TeardownAsync();
        });
    }

    /// Losing access ends the list: a page the server stops returning is gone at the next read.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_refused_read_after_a_listing_clears_the_pages() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.EnqueuePages(Pages(Page("p", "Report", h.Time.GetUtcNow())));
            await h.SwitchAsync(SessionA);
            await Assert.That(h.Vm.HasPages).IsTrue();

            h.Source.EnqueuePages(SessionPagesRead.Of(SessionPlansReadKind.Unavailable));
            h.Vm.Refresh();
            await h.SettledAsync();

            await Assert.That(h.Vm.Pages).IsEmpty();
            await Assert.That(h.Vm.HasAny).IsFalse();
            await Assert.That(h.Vm.PagesNote).IsEqualTo("Pages aren't available for this session.");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unreachable_read_keeps_the_listed_pages_and_is_retried_until_one_lands() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.EnqueuePages(Pages(Page("p", "Report", h.Time.GetUtcNow())));
            await h.SwitchAsync(SessionA);

            h.Source.EnqueuePages(SessionPagesRead.Of(SessionPlansReadKind.Unreachable));
            h.Vm.Refresh();
            await h.SettledAsync();
            await Assert.That(h.Vm.Pages.Single().Title).IsEqualTo("Report");
            await Assert.That(h.Vm.LastReadFailed).IsTrue();

            h.Vm.Refresh();
            await h.SettledAsync();
            await Assert.That(h.Vm.LastReadFailed).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_entry_without_a_web_link_is_not_listed_and_no_link_is_made_for_it() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var now = h.Time.GetUtcNow();
            h.Source.EnqueuePages(Pages(
                Page("a", "Script", now, url: "javascript:alert(1)"),
                Page("b", "Mail", now, url: "mailto:someone@example.com"),
                Page("c", "Nothing", now, url: ""),
                Page("d", "Kept", now),
                Page("d", "Kept again", now)));
            await h.SwitchAsync(SessionA);

            await Assert.That(h.Vm.Pages.Select(p => p.Id)).IsEquivalentTo(new[] { "d" });
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Publishing_a_page_in_the_transcript_reads_the_pages_at_once_and_again_after_the_settle_delay() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await h.SwitchAsync(SessionA);
            await Assert.That(h.Source.RequestedPages.Count).IsEqualTo(1);

            h.Source.EnqueuePages(Pages(Page("p", "Fresh", h.Time.GetUtcNow())));
            h.PublishPage();
            await h.SettledAsync();
            await Assert.That(h.Source.RequestedPages.Count).IsEqualTo(2);
            await Assert.That(h.Vm.Pages.Single().Title).IsEqualTo("Fresh");

            h.Time.Advance(PlanSectionViewModel.SettleDelay);
            await WaitUntilAsync(() => h.Source.RequestedPages.Count == 3, what: "the settle re-read");
            await h.SettledAsync();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stale_pages_read_never_applies_after_a_session_switch_and_a_null_session_hides_the_section() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gate = h.Source.GatePages(ignoreCancellation: true);
            h.Vm.SwitchSession(SessionA, "/repo");
            var stale = h.Vm.PendingPagesForTesting!;

            await h.SwitchAsync(SessionB);
            gate.SetResult(Pages(Page("a", "Only A", h.Time.GetUtcNow())));
            await stale;

            await Assert.That(h.Vm.Pages).IsEmpty();
            await Assert.That(h.Vm.PagesNote).IsEqualTo("No pages published from this session.");

            await h.SwitchAsync(null);
            await Assert.That(h.Vm.ShowsPagesSection).IsFalse();
            await Assert.That(h.Vm.HasPagesNote).IsFalse();
            await Assert.That(h.Source.RequestedPages).IsEquivalentTo(new[] { SessionA, SessionB }, CollectionOrdering.Matching);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_new_root_for_the_same_session_keeps_its_pages_without_reading_them_again() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.EnqueuePages(Pages(Page("p", "Report", h.Time.GetUtcNow())));
            await h.SwitchAsync(SessionA, root: "/repo");

            await h.SwitchAsync(SessionA, root: "/repo-worktree");

            await Assert.That(h.Vm.Pages.Single().Title).IsEqualTo("Report");
            await Assert.That(h.Source.RequestedPages.Count).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }
}
