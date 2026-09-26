using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Kiro;

/// <summary>Crew's session map and sub-agent records, written in the shapes Crew writes them.</summary>
public class KiroCrewSessionEndWatchTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Chat  = "dashboard:chat-2-1790266418";
    const string Sid   = "ebc247c2-22b7-4f08-bbcf-f7053162d361";
    const string Next  = "833d6822-a457-4498-9813-f8b7a2868dc3";
    const string Child = "c9996655-c526-4524-86f9-4ebd14dba13a";

    KiroCrewPaths Crew => new(Tmp.Path, null);

    KiroCrewSessionEndWatch Watch(string session) => new(Crew, Tmp.PathTo("sessions", "cli"), session, TimeProvider.System);

    void Map(string sid, string? discarded = null) =>
        Tmp.CreateFile(["crew", "session_map.json"],
            $"{{\"{Chat}\": {{\"sid\": \"{sid}\", \"provider\": \"acp\"{(discarded is null ? "" : $", \"discarded_sid\": \"{discarded}\"")}}}}}");

    void Subagent(string file, string session, string id = "65eed35b") =>
        Tmp.CreateFile(["crew", "subagents", id, file],
            $"{{\"id\": \"{id}\", \"session_id\": \"{session}\", \"parent_session\": \"{Chat}\", \"status\": \"running\"}}");

    [Test]
    public async Task A_sub_agent_is_finished_once_crew_writes_its_tombstone() {
        Map(Sid);
        Subagent("state.json", Child);
        var watch = Watch(Child);

        await Assert.That(watch.IsFinished()).IsFalse();

        Subagent("tombstone.json", Child);

        await Assert.That(watch.IsFinished()).IsTrue();
    }

    /// <summary>Once found, the sub-agent's own directory is the one read, so many later sub-agents do
    /// not push it out of view.</summary>
    [Test]
    public async Task A_found_sub_agent_stays_watched_behind_many_newer_ones() {
        Map(Sid);
        Subagent("state.json", Child);
        var watch = Watch(Child);
        await Assert.That(watch.IsFinished()).IsFalse();

        Directory.SetLastWriteTimeUtc(Tmp.PathTo("crew", "subagents", "65eed35b"), DateTime.UtcNow.AddDays(-1));
        for (var i = 0; i < 80; i++) Subagent("state.json", Guid.NewGuid().ToString("D"), id: $"n{i:D3}");
        Subagent("tombstone.json", Child);
        Directory.SetLastWriteTimeUtc(Tmp.PathTo("crew", "subagents", "65eed35b"), DateTime.UtcNow.AddDays(-1));

        await Assert.That(watch.IsFinished()).IsTrue();
    }

    [Test]
    public async Task A_chat_session_is_finished_once_crew_moves_the_chat_on() {
        Map(Sid);
        var watch = Watch(Sid);

        await Assert.That(watch.IsFinished()).IsFalse();

        Map(Next, discarded: Sid);

        await Assert.That(watch.IsFinished()).IsTrue();
    }

    [Test]
    public async Task A_chat_session_already_replaced_when_first_checked_is_finished() {
        Map(Next, discarded: Sid);

        await Assert.That(Watch(Sid).IsFinished()).IsTrue();
    }

    /// <summary>Crew rewrites the map in place, so a chat briefly missing or unreadable is not an end;
    /// one still gone after several checks is.</summary>
    [Test]
    [Arguments("{}")]
    [Arguments("{\"dashboard:chat-2-1790266418\": {}}")]
    public async Task A_chat_entry_gone_ends_the_session_only_once_it_stays_gone(string rewritten) {
        Map(Sid);
        var watch = Watch(Sid);
        await Assert.That(watch.IsFinished()).IsFalse();

        Tmp.CreateFile(["crew", "session_map.json"], rewritten);
        await Assert.That(watch.IsFinished()).IsFalse();

        Map(Sid);
        await Assert.That(watch.IsFinished()).IsFalse();

        Tmp.CreateFile(["crew", "session_map.json"], rewritten);
        await Assert.That(watch.IsFinished()).IsFalse();
        await Assert.That(watch.IsFinished()).IsFalse();
        await Assert.That(watch.IsFinished()).IsTrue();
    }

    /// <summary>A half-written map reads as nothing, never as the chat having moved on.</summary>
    [Test]
    public async Task An_unreadable_map_ends_nothing() {
        Map(Sid);
        var watch = Watch(Sid);
        await Assert.That(watch.IsFinished()).IsFalse();

        Tmp.CreateFile(["crew", "session_map.json"], "{\"dashboard:chat-2-1790266418\": {\"sid\"");

        for (var i = 0; i < 5; i++) await Assert.That(watch.IsFinished()).IsFalse();
    }

    /// <summary>A session current in one chat is live, whatever another chat's discarded entry says and
    /// in whichever order the map lists them.</summary>
    [Test]
    public async Task A_session_current_in_any_chat_is_not_ended_by_a_stale_discard() {
        Tmp.CreateFile(["crew", "session_map.json"],
            $"{{\"dashboard:chat-1\": {{\"sid\": \"{Next}\", \"discarded_sid\": \"{Sid}\"}}, \"{Chat}\": {{\"sid\": \"{Sid}\"}}}}");

        await Assert.That(Watch(Sid).IsFinished()).IsFalse();
    }

    /// <summary>A session that moves to another chat stays live even after the chat it was first seen in
    /// moves on or disappears.</summary>
    [Test]
    public async Task A_session_resumed_in_another_chat_is_not_ended() {
        Map(Sid);
        var watch = Watch(Sid);
        await Assert.That(watch.IsFinished()).IsFalse();

        Tmp.CreateFile(["crew", "session_map.json"],
            $"{{\"{Chat}\": {{\"sid\": \"{Next}\"}}, \"dashboard:chat-9\": {{\"sid\": \"{Sid}\"}}}}");
        await Assert.That(watch.IsFinished()).IsFalse();

        Tmp.CreateFile(["crew", "session_map.json"], $"{{\"dashboard:chat-9\": {{\"sid\": \"{Sid}\"}}}}");
        for (var i = 0; i < 4; i++) await Assert.That(watch.IsFinished()).IsFalse();
    }

    /// <summary>An unreadable read breaks a run of absent reads, so absences interleaved with unreadable
    /// reads never add up to an end.</summary>
    [Test]
    public async Task Absences_interleaved_with_unreadable_reads_do_not_end_a_chat() {
        Map(Sid);
        var watch = Watch(Sid);
        await Assert.That(watch.IsFinished()).IsFalse();

        for (var i = 0; i < 4; i++) {
            Tmp.CreateFile(["crew", "session_map.json"], "{}");
            await Assert.That(watch.IsFinished()).IsFalse();
            Tmp.CreateFile(["crew", "session_map.json"], "{\"half");
            await Assert.That(watch.IsFinished()).IsFalse();
        }
    }

    /// <summary>However many other sub-agents a busy Crew spawned since, a recent one is still found.</summary>
    [Test]
    public async Task A_recent_sub_agent_is_found_behind_many_newer_ones() {
        Subagent("tombstone.json", Child);
        Directory.SetLastWriteTimeUtc(Tmp.PathTo("crew", "subagents", "65eed35b"), DateTime.UtcNow.AddMinutes(-2));

        for (var i = 0; i < 100; i++) Subagent("state.json", Guid.NewGuid().ToString("D"), id: $"new{i:D3}");

        await Assert.That(Watch(Child).IsFinished()).IsTrue();
    }

    /// <summary>A watcher started long after its sub-agent spawned still finds it: the search is anchored
    /// at the session's creation, not at the watch.</summary>
    [Test]
    public async Task A_sub_agent_is_found_by_a_watch_started_long_after_it_spawned() {
        var spawned = DateTimeOffset.UtcNow.AddHours(-3);
        Tmp.CreateFile(["sessions", "cli", $"{Child}.json"],
            $"{{\"session_id\": \"{Child}\", \"created_at\": \"{spawned.UtcDateTime:yyyy-MM-ddTHH:mm:ss.ffffffZ}\"}}");
        Subagent("state.json", Child);
        Directory.SetLastWriteTimeUtc(Tmp.PathTo("crew", "subagents", "65eed35b"), spawned.UtcDateTime.AddSeconds(-5));
        var watch = Watch(Child);
        await Assert.That(watch.IsFinished()).IsFalse();

        Subagent("tombstone.json", Child);

        await Assert.That(watch.IsFinished()).IsTrue();
    }

    /// <summary>A session's metadata caught mid-write on the first check does not fix the search to the
    /// watch's start: a later readable read still reaches a sub-agent spawned long before.</summary>
    [Test]
    public async Task An_unreadable_first_metadata_read_does_not_strand_an_old_sub_agent() {
        var spawned = DateTimeOffset.UtcNow.AddHours(-3);
        Tmp.CreateFile(["sessions", "cli", $"{Child}.json"], "{\"session_id\"");
        Subagent("state.json", Child);
        Directory.SetLastWriteTimeUtc(Tmp.PathTo("crew", "subagents", "65eed35b"), spawned.UtcDateTime.AddSeconds(-5));
        var watch = Watch(Child);
        await Assert.That(watch.IsFinished()).IsFalse();

        Tmp.CreateFile(["sessions", "cli", $"{Child}.json"],
            $"{{\"session_id\": \"{Child}\", \"created_at\": \"{spawned.UtcDateTime:yyyy-MM-ddTHH:mm:ss.ffffffZ}\"}}");
        Subagent("tombstone.json", Child);
        Directory.SetLastWriteTimeUtc(Tmp.PathTo("crew", "subagents", "65eed35b"), spawned.UtcDateTime.AddSeconds(-5));

        await Assert.That(watch.IsFinished()).IsTrue();
    }

    /// <summary>A session not found as a sub-agent early on stops being searched for, so a long-lived
    /// session does not rescan Crew's history every check.</summary>
    [Test]
    public async Task A_session_not_found_as_a_sub_agent_early_stops_being_searched_for() {
        var watch = Watch(Child);
        Tmp.CreateDir("crew", "subagents");
        for (var i = 0; i < 12; i++) await Assert.That(watch.IsFinished()).IsFalse();

        Subagent("tombstone.json", Child);

        await Assert.That(watch.IsFinished()).IsFalse();
    }

    [Test]
    public async Task A_session_crew_does_not_know_is_never_finished() {
        Map(Sid);
        Subagent("tombstone.json", Child);

        await Assert.That(Watch("11111111-2222-3333-4444-555555555555").IsFinished()).IsFalse();
        await Assert.That(new KiroCrewSessionEndWatch(new KiroCrewPaths(Tmp.PathTo("nowhere"), null), Tmp.PathTo("nowhere"), Child, TimeProvider.System).IsFinished()).IsFalse();
    }
}
