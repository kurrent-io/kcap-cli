using Capacitor.Cli.Daemon.Harness.Muse;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

public class MuseLaunchArgsTests {
    static RuntimeStartContext Ctx(string? model = null) => new(
        AgentId: "agent-1", Vendor: "muse", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: "hi",
        Model: model, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: false, Review: null,
        Cols: 80, Rows: 24,
        ServerUrl: "http://kcap.test", DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap");

    static string Argv(DaemonConfig config, RuntimeStartContext ctx, bool reviewer) =>
        string.Join(" ", MuseLaunchArgs.For(config, ctx, reviewer));

    [Test]
    public async Task Interactive_argv_is_exact() =>
        await Assert.That(Argv(new DaemonConfig(), Ctx(), reviewer: false))
            .IsEqualTo("serve --provider meta --trust-workspace --sandbox-network proxy-only");

    [Test]
    public async Task Reviewer_argv_is_exact() =>
        await Assert.That(Argv(new DaemonConfig(), Ctx(), reviewer: true))
            .IsEqualTo("serve --provider meta --sandbox-network restricted --disable-write");

    [Test]
    public async Task A_launch_model_goes_right_after_the_provider() =>
        await Assert.That(Argv(new DaemonConfig(), Ctx("muse-spark-1.3"), reviewer: false))
            .IsEqualTo("serve --provider meta --model muse-spark-1.3 --trust-workspace --sandbox-network proxy-only");

    [Test]
    public async Task The_daemon_default_model_applies_when_the_launch_names_none() =>
        await Assert.That(Argv(new DaemonConfig { MuseModel = "muse-wonder-1.0" }, Ctx(), reviewer: true))
            .IsEqualTo("serve --provider meta --model muse-wonder-1.0 --sandbox-network restricted --disable-write");

    [Test]
    [Arguments("default")]
    [Arguments("DEFAULT")]
    [Arguments("")]
    public async Task The_default_sentinel_yields_no_model_flag(string model) =>
        await Assert.That(Argv(new DaemonConfig(), Ctx(model), reviewer: false))
            .IsEqualTo("serve --provider meta --trust-workspace --sandbox-network proxy-only");

    [Test]
    public async Task The_default_sentinel_falls_back_to_the_daemon_model() =>
        await Assert.That(MuseLaunchArgs.ResolveModel(new DaemonConfig { MuseModel = "muse-wonder-1.0" }, Ctx("default")))
            .IsEqualTo("muse-wonder-1.0");
}
