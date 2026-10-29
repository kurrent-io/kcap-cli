using System.Text.Json.Nodes;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudeSessionEnvTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static string Hook(string eventName, string sessionId) =>
        new JsonObject { ["hook_event_name"] = eventName, ["session_id"] = sessionId }.ToJsonString();

    [Test]
    public async Task Session_start_appends_a_dashless_export_ending_in_a_bare_lf() {
        var envFile = Tmp.CreateFile("env.sh", "export EXISTING=1\n");

        ClaudeSessionEnv.Persist(Hook("SessionStart", "9dc27753-7645-4e46-91ec-c2d69973c152"), envFile, TextWriter.Null);

        await Assert.That(File.ReadAllText(envFile))
            .IsEqualTo("export EXISTING=1\nexport KCAP_SESSION_ID=9dc2775376454e4691ecc2d69973c152\n");
    }

    /// <summary>Another hook can leave the file without a final LF; the export must not join its line.</summary>
    [Test]
    public async Task Starts_the_export_on_its_own_line_after_an_unterminated_one() {
        var envFile = Tmp.CreateFile("env.sh", "export EXISTING=1");

        ClaudeSessionEnv.Persist(Hook("SessionStart", "abc"), envFile, TextWriter.Null);

        await Assert.That(File.ReadAllText(envFile)).IsEqualTo("export EXISTING=1\nexport KCAP_SESSION_ID=abc\n");
    }

    [Test]
    public async Task Other_events_leave_the_file_alone() {
        var envFile = Tmp.PathTo("env.sh");

        ClaudeSessionEnv.Persist(Hook("UserPromptSubmit", "abc"), envFile, TextWriter.Null);

        await Assert.That(File.Exists(envFile)).IsFalse();
    }

    /// <summary>The file is sourced by bash, so an id that is not a plain name never reaches it.</summary>
    [Test]
    public async Task A_session_id_carrying_shell_syntax_is_not_written() {
        var envFile = Tmp.PathTo("env.sh");

        ClaudeSessionEnv.Persist(Hook("SessionStart", "abc; rm -rf ~"), envFile, TextWriter.Null);

        await Assert.That(File.Exists(envFile)).IsFalse();
    }

    [Test]
    public async Task Without_an_env_file_nothing_happens() {
        using var stderr = new StringWriter();

        ClaudeSessionEnv.Persist(Hook("SessionStart", "abc"), null, stderr);
        ClaudeSessionEnv.Persist("not json", Tmp.PathTo("env.sh"), stderr);

        await Assert.That(File.Exists(Tmp.PathTo("env.sh"))).IsFalse();
        await Assert.That(stderr.ToString()).IsEqualTo("");
    }

    [Test]
    public async Task An_env_file_it_cannot_append_to_is_reported() {
        var envFile = Tmp.PathTo("missing", "env.sh");
        using var stderr = new StringWriter();

        ClaudeSessionEnv.Persist(Hook("SessionStart", "abc"), envFile, stderr);

        await Assert.That(stderr.ToString()).Contains(envFile);
    }
}
