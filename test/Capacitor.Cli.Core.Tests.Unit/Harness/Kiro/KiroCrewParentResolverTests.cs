using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Kiro;

/// <summary>Records shaped like a real Kiro Crew run: a dashboard chat whose Kiro session spawned sub-agent
/// <c>65eed35b</c>.</summary>
public class KiroCrewParentResolverTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Chat   = "dashboard:chat-2-1790266418";
    const string Parent = "ebc247c2-22b7-4f08-bbcf-f7053162d361";
    const string Child  = "c9996655-c526-4524-86f9-4ebd14dba13a";

    KiroCrewPaths Crew => new(Tmp.Path, null);

    void SeedSessionMap(string sid = Parent, string? discarded = null) {
        Directory.CreateDirectory(Crew.Root);
        var discardedField = discarded is null ? "" : $", \"discarded_sid\": \"{discarded}\"";
        File.WriteAllText(Crew.SessionMapJson,
            $"{{\"{Chat}\": {{\"sid\": \"{sid}\", \"provider\": \"acp\", \"cwd\": \"/Users/tony/dev/kcap-cli\"{discardedField}}}}}");
    }

    void SeedSubagent(string file, string? sessionId = Child, string id = "65eed35b") {
        var dir = Path.Combine(Crew.SubagentsDir, id);
        Directory.CreateDirectory(dir);
        var sessionField = sessionId is null ? "" : $", \"session_id\": \"{sessionId}\"";
        File.WriteAllText(Path.Combine(dir, file),
            $"{{\"id\": \"{id}\", \"agent\": \"kirocrew-research\", \"parent_session\": \"{Chat}\", \"status\": \"running\"{sessionField}}}");
    }

    [Test]
    public async Task A_sub_agent_resolves_to_its_parent_chats_session() {
        SeedSessionMap();
        SeedSubagent("state.json");

        await Assert.That(KiroCrewParentResolver.ParentOf(Crew, Child)).IsEqualTo(Parent);
        await Assert.That(KiroCrewParentResolver.ParentOf(Crew, Child.Replace("-", ""))).IsEqualTo(Parent);
    }

    [Test]
    public async Task A_finished_sub_agent_resolves_from_its_tombstone() {
        SeedSessionMap();
        SeedSubagent("tombstone.json");

        await Assert.That(KiroCrewParentResolver.ParentOf(Crew, Child)).IsEqualTo(Parent);
    }

    /// <summary>Crew writes the child's session id a moment after spawn; until then there is no link.</summary>
    [Test]
    public async Task A_sub_agent_Crew_has_not_recorded_yet_resolves_to_nothing() {
        SeedSessionMap();
        SeedSubagent("state.json", sessionId: null);

        await Assert.That(KiroCrewParentResolver.ParentOf(Crew, Child)).IsNull();
    }

    [Test]
    public async Task A_chat_session_is_nobodys_child() {
        SeedSessionMap(discarded: "8bd763b1-280e-47c3-bd4d-79438fc4a37b");
        SeedSubagent("state.json");

        await Assert.That(KiroCrewParentResolver.ParentOf(Crew, Parent)).IsNull();
        await Assert.That(KiroCrewParentResolver.IsChatSession(Crew, Parent)).IsTrue();
        await Assert.That(KiroCrewParentResolver.IsChatSession(Crew, "8bd763b1-280e-47c3-bd4d-79438fc4a37b")).IsTrue();
        await Assert.That(KiroCrewParentResolver.IsChatSession(Crew, Child)).IsFalse();
    }

    [Test]
    public async Task A_record_naming_the_child_as_its_own_parent_resolves_to_nothing() {
        SeedSessionMap(sid: Child);
        SeedSubagent("state.json");

        await Assert.That(KiroCrewParentResolver.ParentOf(Crew, Child)).IsNull();
    }

    [Test]
    public async Task Missing_or_malformed_crew_files_resolve_to_nothing() {
        await Assert.That(KiroCrewParentResolver.ParentOf(Crew, Child)).IsNull();

        SeedSubagent("state.json");
        Directory.CreateDirectory(Crew.Root);
        await File.WriteAllTextAsync(Crew.SessionMapJson, "{not json");

        await Assert.That(KiroCrewParentResolver.ParentOf(Crew, Child)).IsNull();
        await Assert.That(KiroCrewParentResolver.IsChatSession(Crew, Parent)).IsFalse();
    }
}
