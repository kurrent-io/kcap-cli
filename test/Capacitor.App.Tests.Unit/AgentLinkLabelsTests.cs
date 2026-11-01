using System.Reactive.Concurrency;
using Capacitor.App.Services;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// An agent link on the app's server reads as the title of a row the app holds, else a short-id
/// stand-in; a title arriving for an agent is announced once, and a rename is not.
public class AgentLinkLabelsTests {
    const string Server = "https://kcap.example";

    internal static AgentRow Row(AgentOrigin origin, string id, string? title, string? sessionId = null) => new(
        Key: $"{origin switch { AgentOrigin.Local => "local", AgentOrigin.Pending => "pending", AgentOrigin.Remote => "remote", _ => "server" }}:{id}",
        Origin: origin, Id: id, SessionId: sessionId, Kind: "hosted", Vendor: "claude", Status: "Running",
        CreatedAt: DateTime.UnixEpoch, RepoPath: null, Title: title, Model: null, RequesterDisplay: null,
        WorktreePath: null, WorkLocation: null, BorrowedFrom: null, MachineBadge: null,
        RepoGroupKey: "", RepoGroupLabel: "", CheckoutKey: "", CheckoutLabel: "");

    static (SourceCache<AgentRow, string> Rows, AgentLinkLabels Labels) Build() {
        var rows = new SourceCache<AgentRow, string>(r => r.Key);
        return (rows, new AgentLinkLabels(Server, rows.AsObservableCache(), ImmediateScheduler.Instance));
    }

    [Test]
    [Arguments(AgentOrigin.Local)]
    [Arguments(AgentOrigin.Pending)]
    [Arguments(AgentOrigin.Remote)]
    public async Task A_held_agent_reads_as_its_title(AgentOrigin origin) {
        var (rows, labels) = Build();
        using var _ = labels;
        rows.AddOrUpdate(Row(origin, "990c76dd", "Build the deep-link endpoint"));

        await Assert.That(labels.For($"{Server}/agents/990c76dd")).IsEqualTo(new AgentLinkLabel("990c76dd", "Build the deep-link endpoint", true));
    }

    [Test]
    public async Task An_unknown_agent_reads_as_its_short_id() {
        var (_, labels) = Build();
        using var __ = labels;

        await Assert.That(labels.For($"{Server}/agents/990c76dd")?.Text).IsEqualTo("agent 990c76dd");
        await Assert.That(labels.For($"{Server}/agents/0f8fad5b-d9cb-469f-a165-70867728950e"))
            .IsEqualTo(new AgentLinkLabel("0f8fad5bd9cb469fa16570867728950e", "agent 0f8fad5b", false));
    }

    [Test]
    [Arguments("0f8fad5b-d9cb-469f-a165-70867728950e", "0f8fad5bd9cb469fa16570867728950e")]
    [Arguments("0f8fad5bd9cb469fa16570867728950e", "0F8FAD5B-D9CB-469F-A165-70867728950E")]
    public async Task A_guid_reads_as_its_title_whichever_way_the_row_and_link_spell_it(string rowId, string linkId) {
        var (rows, labels) = Build();
        using var _ = labels;
        rows.AddOrUpdate(Row(AgentOrigin.Remote, rowId, "Remote agent"));

        await Assert.That(labels.For($"{Server}/agents/{linkId}")?.Text).IsEqualTo("Remote agent");
    }

    [Test]
    [Arguments("0f8fad5b-d9cb-469f-a165-70867728950e", "0f8fad5bd9cb469fa16570867728950e")]
    [Arguments("0f8fad5bd9cb469fa16570867728950e", "0F8FAD5B-D9CB-469F-A165-70867728950E")]
    [Arguments("990c76dd", "990c76dd")]
    public async Task A_link_opens_the_agent_under_the_id_its_row_stores(string rowId, string linkId) {
        var (rows, labels) = Build();
        using var _ = labels;
        rows.AddOrUpdate(Row(AgentOrigin.Local, rowId, null));

        await Assert.That(AgentLinkLabels.HeldIdOf(rows.AsObservableCache(), linkId)).IsEqualTo(rowId);
    }

    [Test]
    public async Task An_agent_the_app_does_not_hold_has_no_stored_id() {
        var (rows, labels) = Build();
        using var _ = labels;
        rows.AddOrUpdate(Row(AgentOrigin.Server, "990c76dd", "Some session"));

        await Assert.That(AgentLinkLabels.HeldIdOf(rows.AsObservableCache(), "990c76dd")).IsNull();
    }

    [Test]
    public async Task The_label_reads_the_row_a_click_opens_not_a_titled_twin() {
        var (rows, labels) = Build();
        using var _ = labels;
        rows.AddOrUpdate(Row(AgentOrigin.Local, "990c76dd", null));
        rows.AddOrUpdate(Row(AgentOrigin.Remote, "990c76dd", "Remote twin"));

        await Assert.That(labels.For($"{Server}/agents/990c76dd")).IsEqualTo(new AgentLinkLabel("990c76dd", "agent 990c76dd", false));
    }

    [Test]
    public async Task A_row_without_a_title_still_reads_as_the_short_id() {
        var (rows, labels) = Build();
        using var _ = labels;
        rows.AddOrUpdate(Row(AgentOrigin.Local, "990c76dd", " "));

        await Assert.That(labels.For($"{Server}/agents/990c76dd")!.IsTitle).IsFalse();
    }

    [Test]
    public async Task A_server_row_keyed_by_the_same_string_is_not_read_as_the_agent() {
        var (rows, labels) = Build();
        using var _ = labels;
        rows.AddOrUpdate(Row(AgentOrigin.Server, "990c76dd", "Some session"));

        await Assert.That(labels.For($"{Server}/agents/990c76dd")!.IsTitle).IsFalse();
    }

    [Test]
    [Arguments("https://elsewhere.example/agents/990c76dd")]
    [Arguments(Server + "/sessions/990c76dd")]
    [Arguments(Server + "/agents/")]
    public async Task Any_other_link_has_no_label(string url) {
        var (_, labels) = Build();
        using var __ = labels;

        await Assert.That(labels.For(url)).IsNull();
    }

    [Test]
    public async Task A_title_arriving_is_announced_and_a_rename_is_not() {
        var (rows, labels) = Build();
        using var _ = labels;
        var titled = new List<string>();
        using var watch = labels.Titled.Subscribe(titled.Add);

        rows.AddOrUpdate(Row(AgentOrigin.Remote, "990c76dd", null));
        rows.AddOrUpdate(Row(AgentOrigin.Remote, "990c76dd", "First title"));
        rows.AddOrUpdate(Row(AgentOrigin.Remote, "990c76dd", "Renamed"));
        rows.AddOrUpdate(Row(AgentOrigin.Local, "0F8FAD5B-D9CB-469F-A165-70867728950E", "Local agent"));
        rows.AddOrUpdate(Row(AgentOrigin.Server, "session-1", "A session"));

        await Assert.That(titled).IsEquivalentTo(new[] { "990c76dd", "0f8fad5bd9cb469fa16570867728950e" });
    }
}
