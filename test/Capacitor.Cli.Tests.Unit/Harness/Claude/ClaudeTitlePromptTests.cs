using System.Text.Json.Nodes;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudeTitlePromptTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    const string Sid = "9dc27753-7645-4e46-91ec-c2d69973c152";

    WatcherPaths Watchers => new(Tmp.PathTo("watchers"));

    static string Prompt(string sessionId) =>
        new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = sessionId, ["prompt"] = "hi" }.ToJsonString();

    string Run(string body, TextWriter? stderr = null) {
        using var stdout = new StringWriter();
        var exit = ClaudeTitlePrompt.Handle(body, Config.Root, Watchers, stdout, stderr ?? TextWriter.Null);
        if (exit != 0) throw new InvalidOperationException($"exit {exit}");
        return stdout.ToString();
    }

    [Test]
    public async Task Asks_for_a_title_on_the_first_prompt_only() {
        var first  = Run(Prompt(Sid));
        var second = Run(Prompt(Sid));

        var output = JsonNode.Parse(first)!["hookSpecificOutput"]!;
        await Assert.That(output["hookEventName"]!.GetValue<string>()).IsEqualTo("UserPromptSubmit");
        await Assert.That(output["additionalContext"]!.GetValue<string>()).Contains("kcap set-title");
        await Assert.That(second).IsEqualTo("");
    }

    /// <summary>The plugin's shell hook wrote this same marker, and a session it already prompted can
    /// still be running: honouring the name keeps that session from being asked twice.</summary>
    [Test]
    public async Task Leaves_the_marker_in_the_watcher_directory() {
        Run(Prompt(Sid));

        await Assert.That(File.Exists(Tmp.PathTo("watchers", $"{Sid}.title-requested"))).IsTrue();
    }

    [Test]
    public async Task Reports_a_marker_it_cannot_write_and_asks_nothing() {
        Tmp.CreateFile("watchers");
        using var stderr = new StringWriter();

        await Assert.That(Run(Prompt(Sid), stderr)).IsEqualTo("");
        await Assert.That(stderr.ToString()).Contains(Sid);
    }

    [Test]
    public async Task Says_nothing_for_a_disabled_session() {
        DisabledSessions.Mark(Sid.Replace("-", ""), Config.Root);

        await Assert.That(Run(Prompt(Sid))).IsEqualTo("");
    }

    [Test]
    [Arguments("../escape")]
    [Arguments("")]
    public async Task Ignores_a_session_id_that_is_not_a_plain_name(string sessionId) {
        await Assert.That(Run(Prompt(sessionId))).IsEqualTo("");
        await Assert.That(Directory.Exists(Watchers.Directory)).IsFalse();
    }

    [Test]
    public async Task Survives_a_payload_that_is_not_json() {
        using var stderr = new StringWriter();

        await Assert.That(Run("not json", stderr)).IsEqualTo("");
        await Assert.That(stderr.ToString()).IsEqualTo("");
    }
}
