using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Tests the pure decision logic of <see cref="CodexSessionRolloutLocator"/> — the daemon's
/// best-effort fallback that links a hosted Codex reviewer to its session id when the
/// session-start hook doesn't land. Codex writes each session to
/// <c>~/.codex/sessions/YYYY/MM/DD/rollout-&lt;ts&gt;-&lt;uuid&gt;.jsonl</c>; the session id is the
/// filename UUID (== the hook-reported <c>session_id</c> for a top-level CLI session), and the
/// cwd lives in the opening <c>session_meta</c> envelope's <c>payload.cwd</c> (NOT at the JSONL
/// root the way Claude stores it).
/// </summary>
public class CodexSessionRolloutLocatorTests {
    const string Cwd = "/Users/dev/repo";

    static string Meta(string cwd) =>
        "{\"type\":\"session_meta\",\"payload\":{\"id\":\"019f0021-29e3-7461-a781-e2646e16e271\","
      + "\"session_id\":\"019f0021-29e3-7461-a781-e2646e16e271\",\"cwd\":\"" + cwd
      + "\",\"originator\":\"codex-tui\"}}";

    // ── cwd match / mismatch (payload.cwd, not root cwd) ─────────────────

    [Test]
    public async Task CwdMatch_ReturnsYes() {
        await Assert.That(CodexSessionRolloutLocator.MatchRollout([Meta(Cwd)], Cwd, StringComparison.Ordinal))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.Yes);
    }

    [Test]
    public async Task ForeignCwd_ReturnsNo() {
        // The user's own concurrent session in a different repo — must not be claimed.
        await Assert.That(CodexSessionRolloutLocator.MatchRollout([Meta("/Users/dev/other")], Cwd, StringComparison.Ordinal))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.No);
    }

    [Test]
    public async Task RootLevelCwd_IsIgnored_ReturnsUnknown() {
        // Claude puts cwd at the root; Codex does NOT. A root-level cwd must not be treated
        // as a Codex match, or a Claude transcript would be mis-parsed.
        var claudeShaped = $$"""{"type":"user","cwd":"{{Cwd}}"}""";

        await Assert.That(CodexSessionRolloutLocator.MatchRollout([claudeShaped], Cwd, StringComparison.Ordinal))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.Unknown);
    }

    [Test]
    public async Task NoCwdYet_ReturnsUnknown() {
        // A freshly-created rollout whose session_meta line isn't flushed yet.
        var eventOnly = """{"type":"event_msg","payload":{"type":"task_started"}}""";

        await Assert.That(CodexSessionRolloutLocator.MatchRollout([eventOnly], Cwd, StringComparison.Ordinal))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.Unknown);
    }

    [Test]
    public async Task TrailingSeparator_IsTolerated() {
        await Assert.That(CodexSessionRolloutLocator.MatchRollout([Meta(Cwd + "/")], Cwd, StringComparison.Ordinal))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.Yes);
    }

    [Test]
    public async Task InvalidJsonLines_AreSkipped() {
        string[] lines = [
            "not json",
            """{"type":"session_meta","payload":{"cwd":123}}""", // cwd not a string
            """{"truncated":"partial""",
            Meta(Cwd)
        ];

        await Assert.That(CodexSessionRolloutLocator.MatchRollout(lines, Cwd, StringComparison.Ordinal))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.Yes);
    }

    [Test]
    public async Task WindowsCaseInsensitive_DifferentCase_Matches() {
        var lines = new[] { Meta(@"C:\\Users\\Dev\\Repo") };

        await Assert.That(CodexSessionRolloutLocator.MatchRollout(lines, @"c:\users\dev\repo", StringComparison.OrdinalIgnoreCase))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.Yes);
    }

    // ── TryLocate over a real ~/.codex/sessions-shaped tree ──────────────

    static string WriteRollout(string sessionsRoot, DateTime day, string uuid, string cwd) {
        var dir = Path.Combine(sessionsRoot, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"rollout-{day:yyyy-MM-dd}T00-00-00-{uuid}.jsonl");
        File.WriteAllText(file, Meta(cwd) + "\n");
        return file;
    }

    [Test]
    public async Task TryLocate_returns_dashless_session_id_of_the_matching_rollout() {
        var root = Directory.CreateTempSubdirectory("kcap-codexrollout-").FullName;
        try {
            var spawn = DateTime.UtcNow;
            var uuid  = "019f0022-1702-7a02-a630-edbfb043add4";
            var wt    = Path.Combine(root, "worktree");
            WriteRollout(root, DateTime.Now, uuid, wt);

            var id = CodexSessionRolloutLocator.TryLocate(root, wt, spawn.AddSeconds(-1));

            await Assert.That(id).IsEqualTo(uuid.Replace("-", ""));
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryLocate_ignores_a_foreign_cwd_rollout() {
        var root = Directory.CreateTempSubdirectory("kcap-codexrollout-").FullName;
        try {
            var spawn = DateTime.UtcNow;
            WriteRollout(root, DateTime.Now, "019f0022-1702-7a02-a630-edbfb043add4", "/some/other/cwd");

            var id = CodexSessionRolloutLocator.TryLocate(root, Path.Combine(root, "worktree"), spawn.AddSeconds(-1));

            await Assert.That(id).IsNull();
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryLocate_missing_sessions_root_returns_null() {
        var missing = Path.Combine(Path.GetTempPath(), "kcap-codexrollout-missing-" + Guid.NewGuid().ToString("N"));

        await Assert.That(CodexSessionRolloutLocator.TryLocate(missing, "/wt", DateTime.UtcNow)).IsNull();
    }
}
