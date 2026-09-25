using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class PreviousSessionTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ClearStart = """{"hook_event_name":"SessionStart","session_id":"bbbb-2222","source":"clear"}""";

    static readonly int Pid = Environment.ProcessId;

    void Claim(string session) => AgentSessions.OnThisMachine(Config.Root).Claim(Pid, SessionId.Parse(session)!);

    string? PreviousAfterStamp(string body, int? agentPid) {
        var hook = JsonNode.Parse(body)!.AsObject();
        PreviousSession.Stamp(hook, Config.Root, () => agentPid);

        return hook["previous_session_id"]?.GetValue<string>();
    }

    [Test]
    public async Task A_clear_start_names_the_session_its_own_process_ran() {
        Claim("aaaa-1111");

        await Assert.That(PreviousAfterStamp(ClearStart, Pid)).IsEqualTo("aaaa1111");
    }

    [Test]
    public async Task Another_process_never_takes_the_link() {
        Claim("aaaa-1111");

        await Assert.That(PreviousAfterStamp(ClearStart, 9001)).IsNull();
    }

    /// <summary>
    /// Once the start's own claim has landed, a redelivered start names nothing.
    /// </summary>
    [Test]
    public async Task A_start_never_names_itself() {
        Claim("bbbb-2222");

        await Assert.That(PreviousAfterStamp(ClearStart, Pid)).IsNull();
    }

    /// <summary>
    /// A note from an earlier holder of the pid carries another start token, and one whose
    /// process could not be identified carries none.
    /// </summary>
    [Test]
    [Arguments("aaaa1111\nlx:another-boot:1")]
    [Arguments("aaaa1111\n")]
    [Arguments("aaaa1111")]
    public async Task A_note_without_this_process_start_token_is_refused(string note) {
        Claim("aaaa-1111");
        await File.WriteAllTextAsync(Config.Root.Path("agent-sessions", Pid.ToString(CultureInfo.InvariantCulture)), note);

        await Assert.That(PreviousAfterStamp(ClearStart, Pid)).IsNull();
    }

    [Test]
    [Arguments("""{"hook_event_name":"SessionEnd","session_id":"aaaa-1111","reason":"clear"}""")]
    [Arguments("""{"hook_event_name":"SessionStart","session_id":"bbbb-2222","source":"startup"}""")]
    [Arguments("""{"hook_event_name":"SessionStart","session_id":"bbbb-2222","source":"clear","previous_session_id":"cccc3333"}""")]
    public async Task Other_bodies_are_left_alone(string body) {
        Claim("aaaa-1111");

        var hook = JsonNode.Parse(body)!.AsObject();

        await Assert.That(PreviousSession.Stamp(hook, Config.Root, () => Pid)).IsFalse();
        await Assert.That(hook.ToJsonString()).IsEqualTo(body);
    }
}
