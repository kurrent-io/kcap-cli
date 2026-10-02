using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

public class SessionIdLineEmitterTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    const string Line =
        "Kurrent Capacitor session id: `s1`. Pass it as `session_id` to a kcap MCP tool that cannot resolve the session by itself.";

    HarnessRegistry Harnesses => TestHarnesses.Under(Home);

    string CodexConfigWith(string server) =>
        Tmp.CreateFile("config.toml", $"[mcp_servers.kcap-{server}]\ncommand = \"kcap\"\nargs = [\"mcp\", \"{server}\"]\n");

    [Test]
    public async Task Build_states_the_id_in_one_line() {
        var line = SessionIdLineEmitter.Build("s1");

        await Assert.That(line).IsEqualTo(Line);
        await Assert.That(line!).DoesNotContain("\n");
    }

    [Test]
    public async Task Build_returns_null_for_a_missing_oversized_or_unsafe_session_id() {
        await Assert.That(SessionIdLineEmitter.Build("s1")).IsNotNull();
        await Assert.That(SessionIdLineEmitter.Build(null)).IsNull();
        await Assert.That(SessionIdLineEmitter.Build("  ")).IsNull();
        await Assert.That(SessionIdLineEmitter.Build(new string('a', 257))).IsNull();
        await Assert.That(SessionIdLineEmitter.Build("abc`x`")).IsNull();
        await Assert.That(SessionIdLineEmitter.Build("abc\ndef")).IsNull();
    }

    [Test]
    public async Task Build_accepts_a_file_path_session_id() {
        await Assert.That(SessionIdLineEmitter.Build("/home/u/.pi/sessions/2026-08-12T10:00.jsonl"))
            .Contains("`/home/u/.pi/sessions/2026-08-12T10:00.jsonl`");
    }

    [Test]
    [Arguments("flows")]
    [Arguments("review")]
    [Arguments("workitems")]
    public async Task Resolve_states_the_id_when_any_kcap_server_is_registered(string server) {
        var line = SessionIdLineEmitter.Resolve(
            HarnessId.Codex, "s1", Harnesses, workItemsNudge: null, plansNudge: null, CodexConfigWith(server));

        await Assert.That(line).IsEqualTo(Line);
    }

    [Test]
    public async Task Resolve_reads_the_registration_of_the_harness_it_is_asked_about() {
        var path = Harnesses.Of<CursorHarness>().Paths.UserMcpJson;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"mcpServers":{"kcap-flows":{"command":"kcap","args":["mcp","flows"]}}}""");

        await Assert.That(SessionIdLineEmitter.Resolve(HarnessId.Cursor, "s1", Harnesses, null, null)).IsEqualTo(Line);
        await Assert.That(SessionIdLineEmitter.Resolve(HarnessId.Gemini, "s1", Harnesses, null, null)).IsNull();
    }

    [Test]
    public async Task Resolve_returns_null_when_no_kcap_server_is_registered() {
        var config = Tmp.CreateFile("config.toml", "model = \"gpt-5-codex\"\n");

        await Assert.That(SessionIdLineEmitter.Resolve(HarnessId.Codex, "s1", Harnesses, null, null, config)).IsNull();
    }

    /// <summary>The registration is the same in both halves, so only the nudge decides.</summary>
    [Test]
    public async Task Resolve_returns_null_when_a_nudge_already_carries_the_id() {
        var config = CodexConfigWith("flows");

        await Assert.That(SessionIdLineEmitter.Resolve(HarnessId.Codex, "s1", Harnesses, null, null, config)).IsEqualTo(Line);
        await Assert.That(SessionIdLineEmitter.Resolve(HarnessId.Codex, "s1", Harnesses, "## Work items\nx", null, config)).IsNull();
        await Assert.That(SessionIdLineEmitter.Resolve(HarnessId.Codex, "s1", Harnesses, null, "## Plans\nx", config)).IsNull();
    }
}
