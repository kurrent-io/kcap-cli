using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// Pure: the reader is a projection of one row and one drift verdict.
public class DocumentReaderViewModelTests {
    static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    static PlanArtifactDto Dto(string kind = "design", string path = "docs/superpowers/specs/x-design.md", string? content = "# Design",
            string state = "ok", long? bytes = 2048, string source = "declared") => new() {
        ArtifactId = "a1", Kind = kind, Title = "Design", Source = source, SessionId = "s", Path = path, Content = content,
        ContentState = state, IsComplete = true, IsConfirmed = true, OriginalBytes = bytes, ContentHash = "abc", Version = 1,
        DiscoveredAt = Now.AddMinutes(-12), Confidence = "high", Reason = "declared", IsPrimary = true,
    };

    [Test]
    public async Task A_whole_declared_document_reads_with_its_header_and_no_notice() {
        var reader = DocumentReaderViewModel.For(DocumentRow.From(Dto()), DriftState.Same, Now);
        await Assert.That(reader.KindLabel).IsEqualTo("Design");
        await Assert.That(reader.FileName).IsEqualTo("x-design.md");
        await Assert.That(reader.Path).IsEqualTo("docs/superpowers/specs/x-design.md");
        await Assert.That(reader.Body).IsEqualTo("# Design");
        await Assert.That(reader.HasBody).IsTrue();
        await Assert.That(reader.HasNotice).IsFalse();
        await Assert.That(reader.SizeLabel).IsEqualTo("2 KB");
        await Assert.That(reader.DeclaredLabel).IsEqualTo("Declared 12m ago");
        await Assert.That(reader.MetaLabel).IsEqualTo("Declared 12m ago · 2 KB");
    }

    [Test]
    public async Task A_document_nobody_declared_reads_written_and_a_missing_size_leaves_no_dangling_separator() {
        var written = DocumentReaderViewModel.For(DocumentRow.From(Dto(source: "repo_file", bytes: null)), DriftState.Unknown, Now);
        await Assert.That(written.DeclaredLabel).IsEqualTo("Written 12m ago");
        await Assert.That(written.MetaLabel).IsEqualTo("Written 12m ago");
    }

    [Test]
    public async Task Truncated_and_unavailable_bodies_say_so() {
        var truncated = DocumentReaderViewModel.For(DocumentRow.From(Dto(content: "# Desi", state: "truncated", bytes: 400_000)), DriftState.Same, Now);
        await Assert.That(truncated.HasBody).IsTrue();
        await Assert.That(truncated.Notice).IsEqualTo("Truncated: the server keeps the first 256 KB of a document.");

        var unavailable = DocumentReaderViewModel.For(DocumentRow.From(Dto(content: null, state: "unavailable")), DriftState.Unknown, Now);
        await Assert.That(unavailable.HasBody).IsFalse();
        await Assert.That(unavailable.Body).IsEqualTo("");
        await Assert.That(unavailable.Notice).IsEqualTo("No body is available: this document was declared by hash alone.");
    }

    [Test]
    public async Task Drift_is_named_when_the_body_is_whole() {
        await Assert.That(DocumentReaderViewModel.For(DocumentRow.From(Dto()), DriftState.Changed, Now).Notice)
            .IsEqualTo("Working copy has changed since this was declared.");
        await Assert.That(DocumentReaderViewModel.For(DocumentRow.From(Dto()), DriftState.Missing, Now).Notice)
            .IsEqualTo("Working copy is gone: nothing is at this path any more.");
        await Assert.That(DocumentReaderViewModel.For(DocumentRow.From(Dto()), DriftState.Unknown, Now).HasNotice).IsFalse();
    }

    [Test]
    [Arguments("plan", "Plan")]
    [Arguments("spec", "Spec")]
    [Arguments("design", "Design")]
    [Arguments("checklist", "Document")]
    public async Task Kind_labels(string kind, string label) {
        await Assert.That(DocumentReaderViewModel.For(DocumentRow.From(Dto(kind: kind)), DriftState.Unknown, Now).KindLabel).IsEqualTo(label);
    }

    [Test]
    public async Task A_row_cuts_its_file_name_on_either_separator_and_matches_by_suffix() {
        var row = DocumentRow.From(Dto(path: @"docs\plans\a.md"));
        await Assert.That(row.FileName).IsEqualTo("a.md");
        await Assert.That(row.MatchesPath("docs/plans/a.md")).IsTrue();
        await Assert.That(row.MatchesPath("/Users/me/repo/docs/plans/a.md")).IsTrue();
        await Assert.That(row.MatchesPath("a.md")).IsTrue();
        await Assert.That(row.MatchesPath("docs/plans/b.md")).IsFalse();
        await Assert.That(row.MatchesPath("xa.md")).IsFalse();
    }
}
