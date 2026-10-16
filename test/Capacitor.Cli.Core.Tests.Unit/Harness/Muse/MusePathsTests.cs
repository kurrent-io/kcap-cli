using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Muse;

public class MusePathsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Defaults_follow_xdg_fallbacks_under_home() {
        var p = new MusePaths(new UserHome(Tmp.Path), null, null);
        await Assert.That(p.ConfigRoot).IsEqualTo(Path.Combine(Tmp.Path, ".config", "muse"));
        await Assert.That(p.DataRoot).IsEqualTo(Path.Combine(Tmp.Path, ".local", "share", "muse"));
        await Assert.That(p.SettingsJson).IsEqualTo(Path.Combine(p.ConfigRoot, "settings.json"));
        await Assert.That(p.SessionsDir).IsEqualTo(Path.Combine(p.DataRoot, "sessions"));
        await Assert.That(p.SkillsDir).IsEqualTo(Path.Combine(p.ConfigRoot, "skills"));
    }

    [Test]
    public async Task Xdg_variables_replace_the_roots_wholesale() {
        var p = new MusePaths(new UserHome(Tmp.Path), "/cfg", "/data");
        await Assert.That(p.ConfigRoot).IsEqualTo(Path.Combine("/cfg", "muse"));
        await Assert.That(p.DataRoot).IsEqualTo(Path.Combine("/data", "muse"));
    }

    [Test]
    public async Task Session_log_path_is_dated_by_creation_day() {
        var p = new MusePaths(new UserHome(Tmp.Path), null, null);
        var log = p.SessionLogPath("01a13694-b0ad-7ae2-8bf5-2af6357a0847", new DateOnly(2026, 10, 13))!;
        await Assert.That(log).IsEqualTo(Path.Combine(p.SessionsDir, "2026", "10", "13", "01a13694-b0ad-7ae2-8bf5-2af6357a0847", "session.jsonl"));
        await Assert.That(MusePaths.ChildSessionLogPath(log, "66d6cfba-1cf6-45b3-a2a8-e5c60f4fafc9"))
            .IsEqualTo(Path.Combine(Path.GetDirectoryName(log)!, "subagent", "66d6cfba-1cf6-45b3-a2a8-e5c60f4fafc9", "session.jsonl"));
    }

    [Test]
    public async Task Resolve_prefers_today_then_adjacent_days_then_glob_then_null() {
        var p   = new MusePaths(new UserHome(Tmp.Path), null, null);
        var sid = "01a13694-b0ad-7ae2-8bf5-2af6357a0847";
        var today = new DateOnly(2026, 10, 13);

        await Assert.That(p.ResolveSessionLog(sid, today)).IsNull();

        var yesterday = p.SessionLogPath(sid, today.AddDays(-1))!;
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, yesterday), "");
        await Assert.That(p.ResolveSessionLog(sid, today)).IsEqualTo(yesterday);

        var old = Path.Combine(p.SessionsDir, "2026", "01", "02", "deadbeef-0000-0000-0000-000000000000", "session.jsonl");
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, old), "");
        await Assert.That(p.ResolveSessionLog("deadbeef-0000-0000-0000-000000000000", today)).IsEqualTo(old);
    }

    /// <summary>A session id is joined into a path, so one that could climb out of the sessions
    /// directory must resolve to nothing even when a file sits where it would point.</summary>
    [Test]
    [Arguments("../escape")]
    [Arguments("..")]
    [Arguments("a/b")]
    [Arguments("a\\b")]
    [Arguments("")]
    public async Task Path_shaped_session_ids_resolve_to_nothing(string sessionId) {
        var p     = new MusePaths(new UserHome(Tmp.Path), null, null);
        var today = new DateOnly(2026, 10, 13);
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, Path.Combine(p.SessionsDir, "2026", "10", "escape", "session.jsonl")), "");
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, Path.Combine(p.SessionsDir, "2026", "10", "13", "a", "b", "session.jsonl")), "");

        await Assert.That(p.SessionLogPath(sessionId, today)).IsNull();
        await Assert.That(p.ResolveSessionLog(sessionId, today)).IsNull();
    }

    [Test]
    public async Task HasUserData_needs_settings_or_a_sessions_dir() {
        var p = new MusePaths(new UserHome(Tmp.Path), null, null);
        await Assert.That(p.HasUserData()).IsFalse();
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, p.SettingsJson), "{\"schema_version\":1}");
        await Assert.That(p.HasUserData()).IsTrue();
    }
}
