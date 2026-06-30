using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Import;

/// <summary>
/// kcap's own headless title / what's-done runs record a Claude transcript in
/// an ephemeral temp working dir that is deleted the moment the run ends, so
/// their cwd is permanently missing on disk. They must NOT show up in the
/// import's missing-cwd report — they aren't user sessions and the user can't
/// remap a path that only ever existed for a few seconds.
/// </summary>
public class ImportResolveReposSubSessionTests : IDisposable {
    readonly string _tempDir = Directory.CreateTempSubdirectory("kcap-resolve-subsession").FullName;

    public void Dispose() {
        try { Directory.Delete(_tempDir, recursive: true); } catch {
            /* best effort */
        }
    }

    [Test]
    public async Task ResolveTranscriptRepos_excludes_kcap_subsessions_from_missing_cwd_report() {
        // A genuine user session whose cwd has since gone missing — this SHOULD
        // be surfaced so the user can `kcap remap` it.
        var realMissing = "/does/not/exist/real-repo";
        var realPath    = Path.Combine(_tempDir, "real.jsonl");
        await File.WriteAllLinesAsync(
            realPath,
            [
                $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"{{{realMissing}}}","message":{"content":"hello"}}"""
            ]
        );

        // A kcap title sub-session: starts with a queue-operation carrying a
        // known kcap prompt, and (like the real ones) records a now-deleted
        // temp cwd. The cwd must never reach the missing-cwd report.
        var subPath = Path.Combine(_tempDir, "agent-title.jsonl");
        await File.WriteAllLinesAsync(
            subPath,
            [
                """{"type":"queue-operation","operation":"enqueue","content":"<role>\nYou label coding-session transcripts. You are NOT the assistant being addressed"}""",
                """{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/private/var/folders/x/T/kcap-claude-deadbeef","message":{"content":"x"}}"""
            ]
        );

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("real", realPath, "-does-not-exist-real-repo"),
            ("title", subPath, "-private-var-folders-x-T-kcap-claude-deadbeef"),
        };

        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal);

        await ImportCommand.ResolveTranscriptReposAsync(
            transcripts,
            codex: false,
            new ImportCommand.ImportDisplay { Tty = false },
            cwdRemap: null,
            sessionCwds: sessionCwds,
            worktreeAttributed: null
        );

        // The real session's missing cwd is reported; the sub-session's is not.
        await Assert.That(sessionCwds.ContainsKey("real")).IsTrue();
        await Assert.That(sessionCwds["real"]).IsEqualTo(realMissing);
        await Assert.That(sessionCwds.ContainsKey("title")).IsFalse();
    }
}
