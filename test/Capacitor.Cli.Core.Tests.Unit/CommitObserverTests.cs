using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>A commit is placed in the repository git finds it in, never the folder its transcript
/// line was in; a failed one is never observed.</summary>
public class CommitObserverTests {
    const string Short = "abc1234";
    const string Full  = "abc1234def5678abc1234def5678abc1234def56";

    static readonly DateTimeOffset CalledAt = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [TempDir] public required TempDir Tmp { get; init; }

    string Root => Tmp.Path;
    string Sub  => Tmp.PathTo("src", "cli");

    readonly List<string> _gitCalls = [];

    CommitObserver Observer(string commitDir, string subject = "Fix watcher crash, fixes #45", DateTimeOffset? headAt = null, string? submodules = null) {
        Directory.CreateDirectory(Sub);

        return new CommitObserver(
            (arguments, dir, _) => {
                _gitCalls.Add(arguments);

                return Task.FromResult(dir == commitDir ? arguments switch {
                    $"rev-parse --verify --quiet {Short}^{{commit}}" => Full,
                    $"log -1 --format=%s {Full}"                    => subject,
                    $"log -1 --format=%B {Full}"                    => $"{subject}\n\nBody line",
                    "log -1 --format=%H%x20%ct HEAD"                => headAt is { } h ? $"{Full} {h.ToUnixTimeSeconds()}" : null,
                    "rev-parse --abbrev-ref HEAD"                   => "main",
                    "rev-parse --show-toplevel"                     => dir,
                    _                                               => null
                } : arguments switch {
                    "rev-parse --show-toplevel"    => Root,
                    "submodule status --recursive" => submodules,
                    _                              => null
                });
            },
            top => Task.FromResult<(string?, string?)>(top == Sub ? ("kurrent-io", "kcap-cli") : ("kurrent-io", "kcap-server")));
    }

    string ToolUse(string id, string command) => new JsonObject {
        ["type"] = "assistant", ["cwd"] = Root, ["timestamp"] = CalledAt.ToString("O"),
        ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject {
            ["type"] = "tool_use", ["id"] = id, ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = command } }) }
    }.ToJsonString();

    string ToolResults(params (string Id, string Output)[] results) => new JsonObject {
        ["type"] = "user", ["cwd"] = Root, ["timestamp"] = CalledAt.AddSeconds(3).ToString("O"),
        ["message"] = new JsonObject { ["content"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject {
            ["type"] = "tool_result", ["tool_use_id"] = r.Id, ["content"] = r.Output }).ToArray()) }
    }.ToJsonString();

    static async Task<IReadOnlyList<ObservedCommit>> Observe(CommitObserver observer, params string[] lines) {
        var all = new List<ObservedCommit>();
        foreach (var line in lines) all.AddRange(await observer.ObserveAsync(line));
        return all;
    }

    [Test]
    public async Task A_commit_after_cd_is_placed_in_the_folder_it_ran_in() {
        var commits = await Observe(Observer(Sub),
            ToolUse("t1", "cd src/cli && git commit -m \"Fix watcher crash, fixes #45\""),
            ToolResults(("t1", $"[main {Short}] Fix watcher crash, fixes #45\n 1 file changed")));

        await Assert.That(commits).HasSingleItem();
        await Assert.That(commits[0].RepoName).IsEqualTo("kcap-cli");
        await Assert.That(commits[0].Sha).IsEqualTo(Full);
        await Assert.That(commits[0].Branch).IsEqualTo("main");
        await Assert.That(commits[0].Message).IsEqualTo("Fix watcher crash, fixes #45\n\nBody line");
    }

    [Test]
    public async Task A_git_dash_C_commit_is_placed_in_its_folder() {
        var commits = await Observe(Observer(Sub),
            ToolUse("t1", "git -C src/cli commit -m 'Fix watcher crash, fixes #45'"),
            ToolResults(("t1", $"[main {Short}] Fix watcher crash, fixes #45")));

        await Assert.That(commits.Single().RepoName).IsEqualTo("kcap-cli");
    }

    [Test]
    public async Task A_failed_commit_is_never_observed() {
        var commits = await Observe(Observer(Sub),
            ToolUse("t1", "git commit -m 'Fix watcher crash, fixes #45'"),
            ToolResults(("t1", "error: pathspec did not match any files\nhusky - pre-commit hook exited with code 1")));

        await Assert.That(commits).IsEmpty();
    }

    [Test]
    public async Task Every_commit_among_parallel_commands_is_observed() {
        var observer = Observer(Root);
        var commits  = await Observe(observer,
            ToolUse("t1", "git commit -m 'Fix watcher crash, fixes #45'"),
            ToolUse("t2", "git commit -m 'Fix watcher crash, fixes #45'"),
            ToolResults(("t1", $"[main {Short}] Fix watcher crash, fixes #45"), ("t2", $"[main {Short}] Fix watcher crash, fixes #45")));

        await Assert.That(commits.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_commit_git_cannot_place_keeps_only_its_subject() {
        var commits = await Observe(Observer(commitDir: "/nowhere"),
            ToolUse("t1", "git commit -m '[WIP] Fix watcher crash'"),
            ToolResults(("t1", $"[main {Short}] [WIP] Fix watcher crash")));

        await Assert.That(commits.Single().RepoName).IsNull();
        await Assert.That(commits.Single().Sha).IsEqualTo(Short);
        await Assert.That(commits.Single().Message).IsEqualTo("[WIP] Fix watcher crash");
    }

    [Test]
    public async Task A_short_sha_naming_another_commit_is_not_placed_there() {
        var commits = await Observe(Observer(Sub, subject: "Unrelated commit"),
            ToolUse("t1", "cd src/cli && git commit -m 'Fix watcher crash, fixes #45'"),
            ToolResults(("t1", $"[main {Short}] Fix watcher crash, fixes #45")));

        await Assert.That(commits.Single().RepoName).IsNull();
    }

    /// <summary>After a restart, or for a background run read back later, the call is unknown: git's
    /// confirmation places the commit, and an unconfirmed summary is dropped.</summary>
    [Test]
    public async Task A_result_whose_call_was_never_seen_counts_only_when_git_confirms_it() {
        var confirmed = await Observe(Observer(Root), ToolResults(("t9", $"[main {Short}] Fix watcher crash, fixes #45")));
        var unknown   = await Observe(Observer("/nowhere"), ToolResults(("t9", $"[main {Short}] Fix watcher crash, fixes #45")));

        await Assert.That(confirmed.Single().Sha).IsEqualTo(Full);
        await Assert.That(unknown).IsEmpty();
    }

    [Test]
    public async Task A_quiet_commit_is_the_head_committed_during_the_call() {
        var during = await Observe(Observer(Root, headAt: CalledAt.AddSeconds(1)),
            ToolUse("t1", "git commit -q -m 'Fix watcher crash'"), ToolResults(("t1", "")));
        var before = await Observe(Observer(Root, headAt: CalledAt.AddMinutes(-5)),
            ToolUse("t1", "git commit -q -m 'Fix watcher crash'"), ToolResults(("t1", "")));

        await Assert.That(during.Single().Sha).IsEqualTo(Full);
        await Assert.That(during.Single().Branch).IsEqualTo("main");
        await Assert.That(before).IsEmpty();
    }

    [Test]
    public async Task A_commit_in_a_submodule_whose_path_has_a_space_is_placed_there() {
        var module = Tmp.CreateDir("vendor", "my lib");
        var commits = await Observe(Observer(module, submodules: $"{Full} vendor/my lib (heads/main)\n-{Full} vendor/other"),
            ToolUse("t1", "git commit -m 'Fix watcher crash, fixes #45'"),
            ToolResults(("t1", $"[main {Short}] Fix watcher crash, fixes #45")));

        await Assert.That(commits.Single().Sha).IsEqualTo(Full);
    }

    [Test]
    public async Task Submodules_are_listed_only_when_the_named_folders_miss() {
        await Observe(Observer(Root),
            ToolUse("t1", "git commit -m 'Fix watcher crash, fixes #45'"),
            ToolResults(("t1", $"[main {Short}] Fix watcher crash, fixes #45")));

        await Assert.That(_gitCalls).DoesNotContain("submodule status --recursive");
    }

    [Test]
    [Arguments("{\"message\":\"tool_use\"}")]
    [Arguments("[\"tool_use\"]")]
    [Arguments("{\"message\":{\"content\":[\"tool_use\"]}}")]
    public async Task A_line_of_an_unexpected_shape_yields_nothing(string line) =>
        await Assert.That(await Observer(Root).ObserveAsync(line)).IsEmpty();
}
