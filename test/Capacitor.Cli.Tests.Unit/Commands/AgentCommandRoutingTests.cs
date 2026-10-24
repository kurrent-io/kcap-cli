using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class AgentCommandRoutingTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempHome]        public required TempHome        Home    { get; init; }

    StringWriter Out { get; } = new() { NewLine = "\n" };
    StringWriter Err { get; } = new() { NewLine = "\n" };

    AgentCommand Command() =>
        new(Daemons.Store, AccountStore.Beside(Daemons.Store), Config.Root, Resolutions.None(Config.Root), Home, TestHarnesses.All(),
            TestBinaries.None, new WorkingDirectory(Daemons.Root), TimeProvider.System, new StandardStreams(Out, Err, TextReader.Null));

    [Test]
    public async Task Bare_agent_routes_to_ls() {
        var (sub, rest) = AgentCommand.SplitSubcommand(["agent"]);
        await Assert.That(sub).IsEqualTo("ls");
        await Assert.That(rest).IsEmpty();
    }

    [Test]
    public async Task A_leading_flag_is_an_ls_option_not_a_subcommand() {
        // `kcap agent --daemon dev` lists that daemon's agents; it is not a subcommand
        // named `--daemon`, which is what a naive argv[1] split would report.
        var (sub, rest) = AgentCommand.SplitSubcommand(["agent", "--daemon", "dev"]);
        await Assert.That(sub).IsEqualTo("ls");
        await Assert.That(rest).IsEquivalentTo(new[] { "--daemon", "dev" });
    }

    [Test]
    public async Task Subcommand_and_its_arguments_are_split() {
        var (sub, rest) = AgentCommand.SplitSubcommand(["agent", "stop", "ab12", "--daemon", "dev"]);
        await Assert.That(sub).IsEqualTo("stop");
        await Assert.That(rest).IsEquivalentTo(new[] { "ab12", "--daemon", "dev" });
    }

    [Test]
    public async Task Start_passthrough_survives_the_split_intact() {
        var (sub, rest) = AgentCommand.SplitSubcommand(["agent", "start", "claude", "--", "--model", "opus"]);
        await Assert.That(sub).IsEqualTo("start");
        await Assert.That(rest).IsEquivalentTo(new[] { "claude", "--", "--model", "opus" });
    }

    [Test]
    public async Task Known_subcommands_are_exactly_the_documented_verbs() {
        await Assert.That(AgentCommand.KnownSubcommands).IsEquivalentTo(new[] { "start", "ls", "stop", "attach", "resume" });
    }

    [Test]
    public async Task Unknown_subcommand_is_not_routable() {
        var (sub, _) = AgentCommand.SplitSubcommand(["agent", "frobnicate"]);
        await Assert.That(AgentCommand.KnownSubcommands).DoesNotContain(sub);
    }

    [Test]
    public async Task DaemonNameFrom_absent_flag_resolves_to_the_default() {
        var (name, error) = AgentCommand.DaemonNameFrom(["ab12"]);
        await Assert.That(name).IsNull();
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task DaemonNameFrom_returns_the_value() {
        var (name, error) = AgentCommand.DaemonNameFrom(["ab12", "--daemon", "dev"]);
        await Assert.That(name).IsEqualTo("dev");
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task DaemonNameFrom_as_the_final_token_is_an_error_not_a_silent_default() {
        var (name, error) = AgentCommand.DaemonNameFrom(["--all", "-y", "--daemon"]);
        await Assert.That(name).IsNull();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task DaemonNameFrom_followed_by_a_flag_is_an_error_not_passed_through() {
        var (name, error) = AgentCommand.DaemonNameFrom(["ab12", "--daemon", "-y"]);
        await Assert.That(name).IsNull();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task DaemonNameFrom_resolves_the_value_amid_other_flags() {
        var (name, error) = AgentCommand.DaemonNameFrom(["--all", "-y", "--daemon", "dev"]);
        await Assert.That(name).IsEqualTo("dev");
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task DaemonNameFrom_empty_string_value_is_an_error_not_a_silent_default() {
        // An explicitly empty "--daemon" value used to escape validation (it's neither missing
        // nor flag-shaped) and throw ArgumentException out of DaemonNameResolver.Resolve instead
        // of printing this clean error.
        var (name, error) = AgentCommand.DaemonNameFrom(["ab12", "--daemon", ""]);
        await Assert.That(name).IsNull();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Protected_agents_are_partitioned_out_for_the_confirmation_prompt() {
        AgentRow[] agents = [
            new("a1", "Running", "/r1", "agent", "", ""),
            new("f1", "Running", "/r2", "review-flow", "flow-7f3a", "reviewer"),
            new("v1", "Running", "/r3", "review", "", ""),
        ];

        var (stoppable, prot) = AgentCommand.PartitionByProtection(agents);

        await Assert.That(stoppable).IsEquivalentTo(new[] { "a1" });
        await Assert.That(prot).IsEquivalentTo(new[] { "f1", "v1" });
    }

    [Test]
    public async Task Ls_accepts_eight_columns_and_shows_state() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        var table = string.Join('\n',
            "aaaa1111\tRunning\t/r1\tagent\t\t\tLive one\trunning",
            "bbbb2222\tResuming\t/r2\tagent\t\t\tComing back\tresuming",
            "cccc3333\tInterrupted\t/r3\tagent\t\t\tGone\tinterrupted (worktree_missing)");
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, "ls", new LocalFrame(FrameType.AgentList) { Text = table });

        var exit = await Command().HandleAsync(["agent", "ls", "--daemon", "ls"], baseUrl: null);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That((await received.WaitAsync(TimeSpan.FromSeconds(30)))?.Text).IsEqualTo(LocalFrame.ListStateColumn);
        var lines = Out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines[0]).Contains("STATE");
        await Assert.That(lines.Single(l => l.StartsWith("aaaa1111", StringComparison.Ordinal))).Contains("running");
        await Assert.That(lines.Single(l => l.StartsWith("bbbb2222", StringComparison.Ordinal))).Contains("resuming");
        await Assert.That(lines.Single(l => l.StartsWith("cccc3333", StringComparison.Ordinal))).Contains("interrupted (worktree_missing)");
    }

    [Test]
    public async Task A_table_without_the_state_column_renders_no_state_header() {
        AgentRow[] agents = [new("a1", "Running", "/r1", "agent", "", "", "Title")];

        await Assert.That(AgentCommand.FormatAgentTable(agents).First()).DoesNotContain("STATE");
    }

    [Test]
    public async Task Resume_routes_to_the_agent_resume_frame() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        const string id = "0123456789abcdef0123456789abcdef";
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, "rs", LocalFrame.AgentResumeAck("resuming"));

        var exit = await Command().HandleAsync(["agent", "resume", id, "--daemon", "rs"], baseUrl: null);

        var frame = await received.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(frame?.Type).IsEqualTo(FrameType.AgentResume);
        await Assert.That(frame?.Text).IsEqualTo(id);
        await Assert.That(Out.ToString().Trim()).IsEqualTo($"Resuming {id}.");
    }

    [Test]
    [Arguments("not_interrupted", "0123456789abcdef0123456789abcdef is not interrupted.")]
    [Arguments("unknown", "No agent 0123456789abcdef0123456789abcdef.")]
    [Arguments("blocked:predecessor_process_unresolved",
        "0123456789abcdef0123456789abcdef cannot be resumed: predecessor_process_unresolved.")]
    public async Task Resume_that_is_refused_says_why_and_exits_1(string ack, string expected) {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        _ = FakeControlSocket.AnswerOnce(Daemons.Store, "rs", LocalFrame.AgentResumeAck(ack));

        var exit = await Command().HandleAsync(["agent", "resume", "0123456789abcdef0123456789abcdef", "--daemon", "rs"], baseUrl: null);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That((Out.ToString() + Err.ToString()).Trim()).IsEqualTo(expected);
    }

    /// A daemon that predates the state column answers its request as a bare List; the titles it
    /// can still give come from asking again in the older form.
    [Test]
    public async Task Ls_against_a_daemon_without_the_state_column_asks_again_for_titles() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        var received = FakeControlSocket.AnswerInOrder(Daemons.Store, "old", [
            new LocalFrame(FrameType.AgentList) { Text = "aaaa1111\tRunning\t/r1\tagent\t\t" },
            new LocalFrame(FrameType.AgentList) { Text = "aaaa1111\tRunning\t/r1\tagent\t\t\tFix the login" },
        ]);

        var exit = await Command().HandleAsync(["agent", "ls", "--daemon", "old"], baseUrl: null);

        var requests = await received.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(requests.Select(r => r?.Text ?? "")).IsEquivalentTo(new[] { LocalFrame.ListStateColumn, LocalFrame.ListTitleColumn });
        var lines = Out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines[0]).Contains("TITLE");
        await Assert.That(lines[0]).DoesNotContain("STATE");
        await Assert.That(lines[1]).Contains("Fix the login");
    }
}
