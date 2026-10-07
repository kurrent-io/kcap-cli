using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Plans;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

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
        public ArtefactsTabViewModel Vm { get; }

        public Harness() => Vm = new ArtefactsTabViewModel(Source, Activity, Time, path => Disk.TryGetValue(path, out var bytes) ? bytes : null);

        public async Task SwitchAsync(string sessionId, string? root = "/repo") {
            Vm.SwitchSession(sessionId, root);
            await SettledAsync();
        }

        public Task SettledAsync() => Vm.PendingReadForTesting ?? Task.CompletedTask;

        public void WritePlan(string callId = "c1") => Activity.Apply([new ChatProjectionResult([
            new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: callId, ToolName: PlanToolNames.DeclareDocument),
            new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: callId),
        ], [], [])]);
    }

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
            await Assert.That(h.Vm.SummaryText).IsEqualTo("4 documents");
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
            await Assert.That(h.Vm.Reader!.HasNotice).IsFalse();
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[1]);
            await Assert.That(h.Vm.Reader!.Notice).IsEqualTo("Working copy has changed since this was declared.");
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[3]);
            await Assert.That(h.Vm.Reader!.Notice).IsEqualTo("Working copy is gone: nothing is at this path any more.");
            await h.Vm.SelectCommand.Execute(h.Vm.Documents[2]);
            await Assert.That(h.Vm.Reader!.HasNotice).IsFalse();
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
            var gate = h.Source.Gate();
            h.Vm.SwitchSession(SessionA, "/repo");
            var stale = h.Vm.PendingReadForTesting!;

            h.Source.Enqueue(Ready());
            await h.SwitchAsync(SessionB);
            gate.SetResult(Ready(Doc("1", "plan", "docs/p.md")));
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

            h.Source.Enqueue(Ready(Doc("1", "plan", "docs/p.md", "# p v2"), Doc("2", "spec", "docs/s.md")));
            h.Vm.Refresh();
            await h.SettledAsync();

            await Assert.That(h.Vm.Selected!.Path).IsEqualTo("docs/p.md");
            await Assert.That(h.Vm.Reader!.Body).IsEqualTo("# p v2");
            await Assert.That(h.Vm.Documents.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }
}
