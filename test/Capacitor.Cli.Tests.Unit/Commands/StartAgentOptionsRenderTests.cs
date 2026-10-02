using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class StartAgentOptionsRenderTests {
    const string Here = "m-here";

    static string Daemon(string name, string? machine, string vendors = """["claude","codex"]""", int active = 1, int max = 4, bool connected = true) =>
        $$"""{"name":"{{name}}","connected":{{(connected ? "true" : "false")}},"machine_id":{{(machine is null ? "null" : $"\"{machine}\"")}},"active_agents":{{active}},"max_agents":{{max}},"supported_vendors":{{vendors}}}""";

    static (string Text, bool IsError) Render(string body, string? machine = Here, string? driver = "claude") =>
        StartAgentOptionsTool.Render(200, body, machine, driver);

    [Test]
    public async Task A_daemon_on_this_machine_lists_its_room_and_harnesses_and_asks_for_a_choice() {
        var (text, isError) = Render($"[{Daemon("mac-studio", Here)}]");

        await Assert.That(isError).IsFalse();
        await Assert.That(text).StartsWith("harness running this session: claude\n");
        await Assert.That(text).Contains("- mac-studio: 1 of 4 agent slots in use; harnesses: claude, codex\n");
        await Assert.That(text).EndsWith(StartAgentOptionsTool.AskForHarness);
        await Assert.That(text).DoesNotContain("Several daemons");
    }

    [Test]
    public async Task Daemons_on_other_machines_are_left_out() {
        var (text, _) = Render($"[{Daemon("mac-studio", Here)},{Daemon("laptop", "m-other")}]");

        await Assert.That(text).Contains("mac-studio");
        await Assert.That(text).DoesNotContain("laptop");
    }

    [Test]
    public async Task No_daemon_here_says_start_agent_would_be_refused_and_how_to_start_one() {
        var (text, isError) = Render($"[{Daemon("laptop", "m-other")},{Daemon("old", null)}]");

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("No daemon of yours is connected on this machine");
        await Assert.That(text).Contains("`kcap daemon start -d`");
        await Assert.That(text).Contains("1 of the user's daemons run on other machines");
        await Assert.That(text).Contains("1 connected daemons do not report their machine");
        await Assert.That(text).DoesNotContain(StartAgentOptionsTool.AskForHarness);
    }

    [Test]
    public async Task A_disconnected_daemon_on_this_machine_does_not_count() {
        var (text, _) = Render($"[{Daemon("mac-studio", Here, connected: false)}]");

        await Assert.That(text).Contains("No daemon of yours is connected on this machine");
    }

    [Test]
    public async Task A_machine_where_no_daemon_ever_ran_says_so_without_reading_the_list() {
        var (text, isError) = Render($"[{Daemon("laptop", "m-other")}]", machine: null);

        await Assert.That(isError).IsFalse();
        await Assert.That(text).EndsWith(StartAgentOptionsTool.NoMachineId);
    }

    [Test]
    public async Task A_full_daemon_is_called_at_capacity() {
        var (text, _) = Render($"[{Daemon("mac-studio", Here, active: 4, max: 4)}]");

        await Assert.That(text).Contains("- mac-studio: at capacity (4 of 4 agent slots in use)");
    }

    [Test]
    public async Task Several_daemons_here_ask_for_a_daemon_too() {
        var (text, _) = Render($"[{Daemon("a", Here)},{Daemon("b", Here)}]");

        await Assert.That(text).Contains("Several daemons run here: ask the user which one, and pass it as daemon.");
    }

    /// <summary>A null list is a daemon older than the field, which is not the same as one that hosts nothing.</summary>
    [Test]
    public async Task Missing_and_empty_harness_lists_read_differently() {
        var missing = Render($"[{Daemon("mac-studio", Here, vendors: "null")}]").Text;
        var empty   = Render($"[{Daemon("mac-studio", Here, vendors: "[]")}]").Text;

        await Assert.That(missing).Contains("harnesses: not reported");
        await Assert.That(empty).Contains("harnesses: none");
    }

    [Test]
    public async Task An_unknown_driver_is_named_as_unknown() {
        await Assert.That(Render($"[{Daemon("mac-studio", Here)}]", driver: null).Text)
            .StartsWith("harness running this session: unknown\n");
    }

    [Test]
    [Arguments("not json")]
    [Arguments("{}")]
    public async Task A_body_that_is_not_a_list_is_unreadable(string body) {
        var (text, isError) = Render(body);

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo(StartAgentOptionsTool.UnreadableAnswer);
    }

    [Test]
    public async Task A_failed_status_is_an_error() {
        var (text, isError) = StartAgentOptionsTool.Render(500, "boom", Here, "claude");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo("Error: HTTP 500 — boom");
    }
}
