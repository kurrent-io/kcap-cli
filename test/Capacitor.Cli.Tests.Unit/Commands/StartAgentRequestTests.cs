using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class StartAgentRequestTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>A directory two levels inside a repository. A <c>.git</c> directory is all the root
    /// lookup reads, so no git process runs.</summary>
    string Cwd() {
        Tmp.CreateDir("repo", ".git");

        return Tmp.CreateDir("repo", "src").Path;
    }

    static JsonObject Args(string cwd, string prompt = "Fix the retry.", string workItem = "none") => new() {
        ["cwd"] = cwd, ["prompt"] = prompt, ["work_item"] = workItem
    };

    static StartAgentDto Build(
            JsonObject? arguments, string? session = "s1", string? driver = "claude", string? agent = null, string? machine = null) =>
        StartAgentTool.BuildRequest(arguments, session, driver, agent, machine);

    [Test]
    public async Task A_complete_call_carries_every_field() {
        var cwd = Cwd();

        var request = Build(Args(cwd, workItem: "wi:9d35573ceee554d58c1bbc909fe7d099"), agent: "a1b2c3d4", machine: "m-1");

        await Assert.That(request.SessionId).IsEqualTo("s1");
        await Assert.That(request.Cwd).IsEqualTo(cwd);
        await Assert.That(request.RepoPath).IsEqualTo(Tmp.PathTo("repo"));
        await Assert.That(request.Prompt).IsEqualTo("Fix the retry.");
        await Assert.That(request.WorkItem).IsEqualTo("wi:9d35573ceee554d58c1bbc909fe7d099");
        await Assert.That(request.Vendor).IsEqualTo("claude");
        await Assert.That(request.CallerAgentId).IsEqualTo("a1b2c3d4");
        await Assert.That(request.MachineId).IsEqualTo("m-1");
        await Assert.That(request.Model).IsNull();
        await Assert.That(request.Daemon).IsNull();
    }

    [Test]
    public async Task The_session_id_argument_wins_over_the_harness() {
        var arguments = Args(Cwd());
        arguments["session_id"] = "11111111-2222-3333-4444-555555555555";

        await Assert.That(Build(arguments, session: "s1").SessionId).IsEqualTo("11111111222233334444555555555555");
    }

    [Test]
    public async Task A_call_with_no_session_from_either_source_is_refused() {
        var arguments = Args(Cwd());

        var ex = await Assert.That(() => Build(arguments, session: null)).Throws<ArgumentException>();

        await Assert.That(ex!.Message).IsEqualTo(McpSessionId.NoSessionIdMessage);
    }

    [Test]
    public async Task A_relative_cwd_is_refused() {
        var ex = await Assert.That(() => Build(Args("src"))).Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("'cwd' must be an absolute path");
    }

    [Test]
    public async Task A_cwd_that_does_not_exist_is_refused() {
        var missing = Tmp.PathTo("missing");

        var ex = await Assert.That(() => Build(Args(missing))).Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("'cwd' is not an existing directory");
    }

    [Test]
    public async Task A_cwd_that_is_a_file_is_refused() {
        var file = Tmp.CreateFile("note.txt", "x");

        var ex = await Assert.That(() => Build(Args(file))).Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("'cwd' is not an existing directory");
    }

    [Test]
    public async Task A_cwd_outside_a_git_repository_is_refused() {
        var plain = Tmp.CreateDir("plain").Path;

        var ex = await Assert.That(() => Build(Args(plain))).Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("'cwd' is not inside a git repository");
    }

    /// <summary>A hosted agent runs in a linked worktree of the repository it was started in. A start
    /// sent with the worktree as its repository would have the daemon nest one worktree in another.</summary>
    [Test]
    public async Task A_linked_worktree_cwd_sends_the_repository_it_belongs_to() {
        using var main = GitRepo.CreateWithCommit("main");
        var worktree   = main.AddWorktree(Path.Combine(".capacitor", "worktrees", "agent-1"), "capacitor/agent-1");
        var cwd        = worktree.CreateDir("src").Path;

        var request = Build(Args(cwd));

        await Assert.That(request.Cwd).IsEqualTo(cwd);
        // By directory name: git records the worktree's main repository with symlinks resolved.
        await Assert.That(Path.GetFileName(request.RepoPath)).IsEqualTo(Path.GetFileName(main.Path));
        await Assert.That(request.RepoPath).DoesNotContain("agent-1");
    }

    [Test]
    public async Task A_prompt_is_measured_in_utf8_bytes() {
        var cwd = Cwd();

        var ex = await Assert.That(() => Build(Args(cwd, prompt: new string('é', 8_193)))).Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("16386 bytes");
        await Assert.That(ex.Message).Contains("16384");
        await Assert.That(Build(Args(cwd, prompt: new string('a', 16_384))).Prompt.Length).IsEqualTo(16_384);
    }

    [Test]
    public async Task A_blank_prompt_or_work_item_is_refused() {
        var cwd = Cwd();

        var prompt   = await Assert.That(() => Build(Args(cwd, prompt: "   "))).Throws<ArgumentException>();
        var workItem = await Assert.That(() => Build(Args(cwd, workItem: " "))).Throws<ArgumentException>();

        await Assert.That(prompt!.Message).IsEqualTo("'prompt' must not be blank.");
        await Assert.That(workItem!.Message).IsEqualTo("'work_item' must not be blank.");
    }

    [Test]
    public async Task A_missing_work_item_is_refused() {
        var arguments = new JsonObject { ["cwd"] = Cwd(), ["prompt"] = "Fix the retry." };

        var ex = await Assert.That(() => Build(arguments)).Throws<ArgumentException>();

        await Assert.That(ex!.Message).IsEqualTo("'work_item' is required.");
    }

    /// <summary>What a work item value means is the server's to decide, so a key of another kind is
    /// sent and refused there.</summary>
    [Test]
    [Arguments("  wi:abc  ", "wi:abc")]
    [Arguments("le:42", "le:42")]
    [Arguments("requester", "requester")]
    [Arguments("session:abc", "session:abc")]
    [Arguments("not a key", "not a key")]
    public async Task A_work_item_is_trimmed_and_otherwise_sent_as_given(string given, string sent) {
        await Assert.That(Build(Args(Cwd(), workItem: given)).WorkItem).IsEqualTo(sent);
    }

    [Test]
    public async Task The_vendor_argument_wins_and_is_lower_cased() {
        var arguments = Args(Cwd());
        arguments["vendor"] = " Codex ";

        await Assert.That(Build(arguments, driver: "claude").Vendor).IsEqualTo("codex");
    }

    [Test]
    public async Task A_call_with_no_vendor_from_either_source_is_refused() {
        var arguments = Args(Cwd());

        var ex = await Assert.That(() => Build(arguments, driver: null)).Throws<ArgumentException>();

        await Assert.That(ex!.Message).IsEqualTo(StartAgentTool.NoVendorMessage);
    }

    [Test]
    public async Task A_model_and_a_daemon_are_trimmed_and_a_blank_one_is_absent() {
        var arguments = Args(Cwd());
        arguments["model"]  = " gpt-5-codex ";
        arguments["daemon"] = "   ";

        var request = Build(arguments);

        await Assert.That(request.Model).IsEqualTo("gpt-5-codex");
        await Assert.That(request.Daemon).IsNull();
    }

    [Test]
    public async Task A_wrongly_typed_argument_is_a_clean_refusal() {
        var arguments = new JsonObject { ["cwd"] = 42, ["prompt"] = "Fix the retry.", ["work_item"] = "none" };

        var ex = await Assert.That(() => Build(arguments)).Throws<ArgumentException>();

        await Assert.That(ex!.Message).IsEqualTo("'cwd' must be a string.");
    }
}
