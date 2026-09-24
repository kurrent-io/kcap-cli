using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

/// <summary>A /clear's SessionStart carries the session its own Claude process just ended, and
/// nothing another process ended.</summary>
public class ClaudeClearLinkTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ClearEnd   = """{"hook_event_name":"SessionEnd","session_id":"aaaa-1111","reason":"clear"}""";
    const string ClearStart = """{"hook_event_name":"SessionStart","session_id":"bbbb-2222","source":"clear"}""";

    static string? PreviousOf(string body) => JsonNode.Parse(body)?["previous_session_id"]?.GetValue<string>();

    static readonly int Pid = Environment.ProcessId;

    [Test]
    public async Task A_clear_start_names_the_session_its_own_process_ended() {
        ClaudeClearLink.Apply(ClearEnd, Config.Root, () => Pid);

        var start = ClaudeClearLink.Apply(ClearStart, Config.Root, () => Pid);

        await Assert.That(PreviousOf(start)).IsEqualTo("aaaa1111");
        await Assert.That(PreviousOf(ClaudeClearLink.Apply(ClearStart, Config.Root, () => Pid))).IsNull(); // consumed
    }

    [Test]
    public async Task Another_process_never_takes_the_link() {
        ClaudeClearLink.Apply(ClearEnd, Config.Root, () => Pid);

        await Assert.That(PreviousOf(ClaudeClearLink.Apply(ClearStart, Config.Root, () => 9001))).IsNull();
        await Assert.That(PreviousOf(ClaudeClearLink.Apply(ClearStart, Config.Root, () => Pid))).IsEqualTo("aaaa1111");
    }

    /// <summary>A note from an earlier holder of the pid carries another start token, and one whose
    /// process could not be identified carries none.</summary>
    [Test]
    [Arguments("aaaa1111\nlx:another-boot:1")]
    [Arguments("aaaa1111\n")]
    [Arguments("aaaa1111")]
    public async Task A_note_without_this_process_start_token_is_refused(string note) {
        ClaudeClearLink.Apply(ClearEnd, Config.Root, () => Pid);
        File.WriteAllText(Config.Root.Path("clear-links", Pid.ToString(CultureInfo.InvariantCulture)), note);

        await Assert.That(PreviousOf(ClaudeClearLink.Apply(ClearStart, Config.Root, () => Pid))).IsNull();
    }

    [Test]
    [Arguments("""{"hook_event_name":"SessionEnd","session_id":"aaaa-1111","reason":"logout"}""")]
    [Arguments("""{"hook_event_name":"SessionStart","session_id":"bbbb-2222","source":"startup"}""")]
    [Arguments("not json")]
    public async Task Other_bodies_pass_through_and_record_nothing(string body) {
        await Assert.That(ClaudeClearLink.Apply(body, Config.Root, () => 4812)).IsEqualTo(body);
        await Assert.That(PreviousOf(ClaudeClearLink.Apply(ClearStart, Config.Root, () => 4812))).IsNull();
    }

    [Test]
    public async Task No_resolvable_process_leaves_the_start_unlinked() {
        ClaudeClearLink.Apply(ClearEnd, Config.Root, () => null);

        await Assert.That(ClaudeClearLink.Apply(ClearStart, Config.Root, () => null)).IsEqualTo(ClearStart);
    }
}
