using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Kiro;

/// <summary>Records shaped like a real Kiro Crew run: a dashboard chat whose Kiro session spawned sub-agent
/// <c>65eed35b</c>.</summary>
public class KiroCrewParentResolverTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Chat     = "dashboard:chat-2-1790266418";
    const string Parent   = "ebc247c2-22b7-4f08-bbcf-f7053162d361";
    const string Earlier  = "8bd763b1-280e-47c3-bd4d-79438fc4a37b";
    const string Child    = "c9996655-c526-4524-86f9-4ebd14dba13a";

    /// <summary>The child's spawn time, in Crew's epoch-seconds form.</summary>
    const double ChildStarted = 1790266768.153635;

    static readonly DateTimeOffset ChildStartedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(ChildStarted * 1000));

    KiroCrewPaths Crew        => new(Tmp.Path, null);
    string        SessionsDir => Tmp.PathTo("sessions", "cli");

    void SeedSessionMap(string sid = Parent, string? discarded = null) {
        Directory.CreateDirectory(Crew.Root);
        var discardedField = discarded is null ? "" : $", \"discarded_sid\": \"{discarded}\"";
        File.WriteAllText(Crew.SessionMapJson,
            $"{{\"{Chat}\": {{\"sid\": \"{sid}\", \"provider\": \"acp\", \"cwd\": \"/Users/tony/dev/kcap-cli\"{discardedField}}}}}");
    }

    void SeedKiroSession(string sessionId, DateTimeOffset createdAt) {
        Directory.CreateDirectory(SessionsDir);
        File.WriteAllText(Path.Combine(SessionsDir, $"{sessionId}.json"),
            $"{{\"session_id\": \"{sessionId}\", \"created_at\": \"{createdAt.UtcDateTime:yyyy-MM-ddTHH:mm:ss.ffffffZ}\"}}");
    }

    void SeedSubagent(string file, string? sessionId = Child, string id = "65eed35b") {
        var dir = Path.Combine(Crew.SubagentsDir, id);
        Directory.CreateDirectory(dir);
        var sessionField = sessionId is null ? "" : $", \"session_id\": \"{sessionId}\"";
        File.WriteAllText(Path.Combine(dir, file),
            $"{{\"id\": \"{id}\", \"agent\": \"kirocrew-research\", \"parent_session\": \"{Chat}\", \"started\": {ChildStarted}, \"status\": \"running\"{sessionField}}}");
    }

    string? ParentOf(string sessionId) => KiroCrewParentResolver.ParentOf(Crew, SessionsDir, sessionId);

    [Test]
    public async Task A_sub_agent_resolves_to_its_parent_chats_session() {
        SeedSessionMap();
        SeedKiroSession(Parent, ChildStartedAt.AddMinutes(-5));
        SeedSubagent("state.json");

        await Assert.That(ParentOf(Child)).IsEqualTo(Parent);
        await Assert.That(ParentOf(Child.Replace("-", ""))).IsEqualTo(Parent);
    }

    [Test]
    public async Task A_finished_sub_agent_resolves_from_its_tombstone() {
        SeedSessionMap();
        SeedKiroSession(Parent, ChildStartedAt.AddMinutes(-5));
        SeedSubagent("tombstone.json");

        await Assert.That(ParentOf(Child)).IsEqualTo(Parent);
    }

    /// <summary>Crew writes the child's session id a moment after spawn; until then there is no link.</summary>
    [Test]
    public async Task A_sub_agent_Crew_has_not_recorded_yet_resolves_to_nothing() {
        SeedSessionMap();
        SeedKiroSession(Parent, ChildStartedAt.AddMinutes(-5));
        SeedSubagent("state.json", sessionId: null);

        await Assert.That(ParentOf(Child)).IsNull();
    }

    /// <summary>The chat moved to a new session after spawning the child: the session current at spawn,
    /// now the discarded one, is the parent — not the chat's current session.</summary>
    [Test]
    public async Task A_chat_that_moved_on_after_the_spawn_resolves_to_the_session_current_at_spawn() {
        SeedSessionMap(sid: Parent, discarded: Earlier);
        SeedKiroSession(Earlier, ChildStartedAt.AddMinutes(-20));
        SeedKiroSession(Parent, ChildStartedAt.AddMinutes(10));
        SeedSubagent("state.json");

        await Assert.That(ParentOf(Child)).IsEqualTo(Earlier);
    }

    /// <summary>Both mapped sessions were created after the child: the one that spawned it is no longer
    /// named anywhere, so no parent beats a wrong one.</summary>
    [Test]
    public async Task A_chat_that_moved_on_twice_since_the_spawn_resolves_to_nothing() {
        SeedSessionMap(sid: Parent, discarded: Earlier);
        SeedKiroSession(Earlier, ChildStartedAt.AddMinutes(5));
        SeedKiroSession(Parent, ChildStartedAt.AddMinutes(10));
        SeedSubagent("state.json");

        await Assert.That(ParentOf(Child)).IsNull();
    }

    [Test]
    public async Task A_parent_without_readable_metadata_is_not_asserted() {
        SeedSessionMap();
        SeedSubagent("state.json");

        await Assert.That(ParentOf(Child)).IsNull();
    }

    [Test]
    public async Task A_chat_session_is_nobodys_child() {
        SeedSessionMap(discarded: Earlier);
        SeedKiroSession(Parent, ChildStartedAt.AddMinutes(-5));
        SeedSubagent("state.json");

        await Assert.That(ParentOf(Parent)).IsNull();
        await Assert.That(KiroCrewParentResolver.IsChatSession(Crew, Parent)).IsTrue();
        await Assert.That(KiroCrewParentResolver.IsChatSession(Crew, Earlier)).IsTrue();
        await Assert.That(KiroCrewParentResolver.IsChatSession(Crew, Child)).IsFalse();
    }

    [Test]
    public async Task A_record_naming_the_child_as_its_own_parent_resolves_to_nothing() {
        SeedSessionMap(sid: Child);
        SeedKiroSession(Child, ChildStartedAt.AddMinutes(-5));
        SeedSubagent("state.json");

        await Assert.That(ParentOf(Child)).IsNull();
    }

    [Test]
    public async Task Missing_or_malformed_crew_files_resolve_to_nothing() {
        await Assert.That(ParentOf(Child)).IsNull();

        SeedSubagent("state.json");
        SeedKiroSession(Parent, ChildStartedAt.AddMinutes(-5));
        await File.WriteAllTextAsync(Crew.SessionMapJson, "{not json");

        await Assert.That(ParentOf(Child)).IsNull();
        await Assert.That(KiroCrewParentResolver.IsChatSession(Crew, Parent)).IsFalse();
    }

    /// <summary>An import reaches a child however many sub-agents ran after it; the live hook's scan
    /// stops at its limit.</summary>
    [Test]
    public async Task An_import_finds_a_child_older_than_the_live_scan_reaches() {
        SeedSessionMap();
        SeedKiroSession(Parent, ChildStartedAt.AddMinutes(-5));
        SeedSubagent("state.json");
        Directory.SetLastWriteTimeUtc(Path.Combine(Crew.SubagentsDir, "65eed35b"), DateTime.UtcNow.AddDays(-30));

        for (var i = 0; i < KiroCrewParentResolver.LiveScanLimit; i++)
            SeedSubagent("state.json", sessionId: Guid.NewGuid().ToString("D"), id: $"n{i:D6}");

        await Assert.That(ParentOf(Child)).IsNull();
        await Assert.That(KiroCrewParentResolver.AllParents(Crew, SessionsDir)[Guid.Parse(Child)]).IsEqualTo(Parent);
    }
}
