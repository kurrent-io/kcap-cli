using Capacitor.App.Services;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// An open session's PR links come from its listing row, and the server's PR route is read only
/// for a session whose row carried none.
public class ListedPullRequestSourceTests {
    [Test]
    public async Task A_listed_session_is_served_from_its_row_without_a_server_read() {
        using var directory = new FakeAgentDirectory();
        var server = new FakePullRequestSource(new FakeTimeProvider());
        var source = new ListedPullRequestSource(server, directory);
        directory.Rows.AddOrUpdate(ServerRow("s1", new([Listed(7, "draft"), Listed(7, "draft"), Listed(8, "merged")])));

        var read = await source.ListAsync("s1", default);

        await Assert.That(server.Lists).IsEqualTo(0);
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(read.Data!.Items.Select(link => (link.Provider, link.Host, link.Owner, link.Number, link.Lifecycle)).ToArray())
            .IsEquivalentTo(new (string, string, string, int, string?)[] { ("github", "github.com", "kurrent-io", 7, "draft"), ("github", "github.com", "kurrent-io", 8, "merged") });
    }

    /// An empty list is the server's record of none, so it is served too; a withheld one is not a
    /// record at all, and neither is a row whose listing never carried the field.
    [Test]
    [Arguments("empty", 0)]
    [Arguments("withheld", 1)]
    [Arguments("absent", 1)]
    public async Task Only_a_carried_list_spares_the_server_read(string carried, int serverReads) {
        using var directory = new FakeAgentDirectory();
        var server = new FakePullRequestSource(new FakeTimeProvider());
        var source = new ListedPullRequestSource(server, directory);
        directory.Rows.AddOrUpdate(ServerRow("s1", carried switch {
            "empty" => new([]),
            "withheld" => RepoSessionPullRequests.Withheld,
            _ => null,
        }));

        var read = await source.ListAsync("s1", default);

        await Assert.That(server.Lists).IsEqualTo(serverReads);
        await Assert.That(read.Data!.Items.Length).IsEqualTo(serverReads == 0 ? 0 : server.Links.Length);
    }

    [Test]
    public async Task A_row_that_drops_its_list_falls_back_to_the_server() {
        using var directory = new FakeAgentDirectory();
        var server = new FakePullRequestSource(new FakeTimeProvider());
        var source = new ListedPullRequestSource(server, directory);
        directory.Rows.AddOrUpdate(ServerRow("s1", new([Listed(7, "open")])));
        directory.Rows.AddOrUpdate(ServerRow("s1", null));

        await source.ListAsync("s1", default);

        await Assert.That(server.Lists).IsEqualTo(1);
    }

    [Test]
    public async Task A_listed_PR_without_a_url_sends_the_session_to_the_server() {
        using var directory = new FakeAgentDirectory();
        var server = new FakePullRequestSource(new FakeTimeProvider());
        var source = new ListedPullRequestSource(server, directory);
        directory.Rows.AddOrUpdate(ServerRow("s1", new([Listed(7, "open"), Listed(8, "open") with { Url = null }])));

        var read = await source.ListAsync("s1", default);

        await Assert.That(server.Lists).IsEqualTo(1);
        await Assert.That(read.Data!.Items.Length).IsEqualTo(server.Links.Length);
    }

    static RepoSessionPullRequestDto Listed(int number, string state) => new() {
        RepoHash = "h", Owner = "Kurrent-IO", RepoName = "kcap-cli", Number = number,
        Url = $"https://github.com/kurrent-io/kcap-cli/pull/{number}", Title = $"PR {number}", State = state };

    static AgentRow ServerRow(string sessionId, RepoSessionPullRequests? listed) => AgentRow.FromServer(new RepoSessionDto {
        SessionId = sessionId, Status = "active", AccessLevel = "full", Vendor = "claude", Title = "Session",
        StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
        Repo = new() { Hash = "h", Owner = "kurrent-io", Name = "kcap-cli" },
    }, hostedPresentation: false) with { ListedPullRequests = listed };
}
