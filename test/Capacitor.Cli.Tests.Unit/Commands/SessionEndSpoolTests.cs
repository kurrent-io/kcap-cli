using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// A session whose agent process is gone is ended on the server it was recorded against, through the
/// lifecycle spool, and left alone on any other.
public class SessionEndSpoolTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static ExitedSession Exited(string id, string? vendor = "claude", string? server = "https://kcap.example") =>
        new(SessionId.Parse(id)!, new ClaimOrigin(vendor, server, "/t/x.jsonl", "/repo"));

    [Test]
    public async Task Only_a_session_recorded_against_this_server_is_queued() {
        var spool = new HookSpool(Config.Root, TimeProvider.System);

        var queued = AgentHookPoster.SpoolEnds(
            [Exited("here"), Exited("slash", server: "https://KCAP.example/"), Exited("elsewhere", server: "https://other.example"),
             Exited("novendor", vendor: null), Exited("noserver", server: null)],
            "https://kcap.example", spool, TimeProvider.System);

        await Assert.That(queued).IsEqualTo(2);
        await Assert.That(spool.HasBacklog("here")).IsTrue();
        await Assert.That(spool.HasBacklog("slash")).IsTrue();
        await Assert.That(spool.HasBacklog("elsewhere")).IsFalse();
        await Assert.That(spool.HasBacklog("novendor")).IsFalse();
        await Assert.That(spool.HasBacklog("noserver")).IsFalse();
    }

    [Test]
    public async Task The_end_is_a_parent_exited_session_end_for_the_vendor() {
        var spool = new HookSpool(Config.Root, TimeProvider.System);
        AgentHookPoster.SpoolEnds([Exited("here", vendor: "codex")], "https://kcap.example", spool, TimeProvider.System);

        string? route = null, body = null;
        await spool.DrainAllAsync("here", (r, b) => { route = r; body = b; return Task.FromResult(DrainOutcome.Delivered); }, TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(route).IsEqualTo("session-end/codex");
        var node = JsonNode.Parse(body!)!;
        await Assert.That(node["session_id"]!.GetValue<string>()).IsEqualTo("here");
        await Assert.That(node["transcript_path"]!.GetValue<string>()).IsEqualTo("/t/x.jsonl");
        await Assert.That(node["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
        await Assert.That(node["hook_event_name"]!.GetValue<string>()).IsEqualTo("SessionEnd");
        await Assert.That(node["reason"]!.GetValue<string>()).IsEqualTo("parent_exited");
        await Assert.That(node["ended_at"]).IsNotNull();
    }

    /// A session ended once and then resumed holds the spool's ended marker, which would discard its
    /// next end unseen.
    [Test]
    public async Task A_resumed_sessions_earlier_end_does_not_swallow_this_one() {
        var spool = new HookSpool(Config.Root, TimeProvider.System);
        spool.MarkEnded("here");

        AgentHookPoster.SpoolEnds([Exited("here")], "https://kcap.example", spool, TimeProvider.System);

        await Assert.That(spool.IsMarkedEnded("here")).IsFalse();
        await Assert.That(spool.HasBacklog("here")).IsTrue();
    }

    /// What the earlier end left behind is a straggler of the run it closed, discarded as the drain
    /// would have, so only the new end is delivered.
    [Test]
    public async Task A_straggler_of_the_earlier_end_is_discarded_not_delivered() {
        var spool = new HookSpool(Config.Root, TimeProvider.System);
        spool.MarkEnded("here");
        spool.Append("here", "subagent-stop/claude", "{}");

        AgentHookPoster.SpoolEnds([Exited("here")], "https://kcap.example", spool, TimeProvider.System);

        var routes = new List<string>();
        await spool.DrainAllAsync("here", (r, _) => { routes.Add(r); return Task.FromResult(DrainOutcome.Delivered); }, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Assert.That(routes).IsEquivalentTo(new[] { "session-end/claude" });
    }

    [Test]
    public async Task Nothing_is_queued_without_a_server_to_post_to() =>
        await Assert.That(AgentHookPoster.SpoolEnds([Exited("here")], null, new HookSpool(Config.Root, TimeProvider.System), TimeProvider.System))
            .IsEqualTo(0);

    [Test]
    public async Task A_session_whose_end_the_fence_holds_is_not_queued() {
        var spool  = new HookSpool(Config.Root, TimeProvider.System);
        var origin = new ClaimOrigin("claude", "https://kcap.example", "/t/x.jsonl", "/repo", AgentId: "agent-1", DaemonEpoch: "epoch-1");

        var queued = AgentHookPoster.SpoolEnds(
            [new ExitedSession(SessionId.Parse("held")!, origin), Exited("here")], "https://kcap.example", spool, TimeProvider.System,
            hold: exited => exited.Origin?.AgentId == "agent-1");

        await Assert.That(queued).IsEqualTo(1);
        await Assert.That(spool.HasBacklog("held")).IsFalse();
        await Assert.That(spool.HasBacklog("here")).IsTrue();
    }
}
