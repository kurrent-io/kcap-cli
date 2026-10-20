using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// A remote subagent's events are paged in order, polled while it runs, and fetched no more once it
/// has ended and its last page is in.
public class RemoteSubagentFeedTests {
    static SessionEventDto Text(long number, string text) => new() {
        EventType   = "AssistantTextGenerated",
        EventNumber = number,
        Payload     = JsonDocument.Parse($$"""{"content":"{{text}}"}""").RootElement,
    };

    sealed class Server {
        public readonly List<SessionEventDto> Events = [];
        public readonly List<long> Requests = [];
        public int PageSize = 100;
        public bool Missing;

        public Task<SubagentEventsFetch> Read(string sessionId, string agentId, long from, CancellationToken ct) {
            Requests.Add(from);
            if (Missing) return Task.FromResult(new SubagentEventsFetch(null, NotFound: true));
            var page = Events.Where(e => e.EventNumber >= from).Take(PageSize).ToArray();
            int? next = page.Length == PageSize && Events.Any(e => e.EventNumber > page[^1].EventNumber) ? (int)page[^1].EventNumber + 1 : null;
            return Task.FromResult(new SubagentEventsFetch(new SubagentEventsDto { Events = page, NextFrom = next, LastEventNumber = Events.Count - 1 }));
        }
    }

    static string Texts(FeedRead read) => string.Join(",", read.Lines.SelectMany(l => l.Projection.Envelopes).Select(e => e.Text));

    [Test]
    public async Task A_running_subagent_is_paged_then_polled_and_an_ended_one_stops_once_complete() {
        var server = new Server { PageSize = 2 };
        server.Events.AddRange([Text(0, "a"), Text(1, "b"), Text(2, "c")]);
        var clock = new FakeTimeProvider();
        var running = true;
        using var feed = new RemoteSubagentFeed("s1", "a1", "claude", server.Read, () => running, clock, _ => { });

        await Assert.That(Texts(feed.ReadAppended())).IsEqualTo("a,b,c");
        await Assert.That(feed.CurrentOffset).IsEqualTo(2);

        server.Events.Add(Text(3, "d"));
        await Assert.That(feed.ReadAppended().Lines).IsEmpty();
        clock.Advance(RemoteSubagentFeed.PollInterval);
        await Assert.That(Texts(feed.ReadAppended())).IsEqualTo("d");

        running = false;
        server.Events.Add(Text(4, "e"));
        clock.Advance(RemoteSubagentFeed.PollInterval);
        await Assert.That(Texts(feed.ReadAppended())).IsEqualTo("e");

        var requests = server.Requests.Count;
        clock.Advance(RemoteSubagentFeed.PollInterval);
        await Assert.That(feed.ReadAppended().Lines).IsEmpty();
        await Assert.That(server.Requests.Count).IsEqualTo(requests);
    }

    [Test]
    public async Task A_subagent_the_server_has_not_written_yet_reads_as_missing() {
        var server = new Server { Missing = true };
        using var feed = new RemoteSubagentFeed("s1", "a1", "claude", server.Read, () => true, new FakeTimeProvider(), _ => { });

        await Assert.That(feed.ReadAppended().Status).IsEqualTo(FeedStatus.Missing);
    }
}
