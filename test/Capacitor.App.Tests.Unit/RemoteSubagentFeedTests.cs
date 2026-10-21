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
        public int? FailRequest;

        public Task<SubagentEventsFetch> Read(string sessionId, string agentId, long from, CancellationToken ct) {
            Requests.Add(from);
            if (FailRequest == Requests.Count) return Task.FromResult(new SubagentEventsFetch(null));
            if (Missing) return Task.FromResult(new SubagentEventsFetch(null, NotFound: true));
            // As the server pages: `from` counts events already received, from the oldest one still kept.
            var page = Events.Skip((int)from).Take(PageSize).ToArray();
            int? next = from + page.Length < Events.Count ? (int)from + page.Length : null;
            return Task.FromResult(new SubagentEventsFetch(new SubagentEventsDto { Events = page, NextFrom = next, LastEventNumber = Events.Count == 0 ? -1 : Events[^1].EventNumber }));
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

        // Its last events can land after the parent hears it ended, so one more read follows.
        server.Events.Add(Text(5, "f"));
        clock.Advance(RemoteSubagentFeed.PollInterval);
        await Assert.That(Texts(feed.ReadAppended())).IsEqualTo("f");

        var requests = server.Requests.Count;
        clock.Advance(RemoteSubagentFeed.PollInterval);
        await Assert.That(feed.ReadAppended().Lines).IsEmpty();
        await Assert.That(server.Requests.Count).IsEqualTo(requests);

        // Nothing bounds how long its last events trail its end, so it still looks now and then.
        server.Events.Add(Text(6, "g"));
        clock.Advance(RemoteSubagentFeed.EndedPollInterval);
        await Assert.That(Texts(feed.ReadAppended())).IsEqualTo("g");
    }

    /// The server admits the route once its subagent row projects, which can trail the start record: a running
    /// subagent not there yet is still being waited for, an ended one is missing.
    /// A poll inside the interval repeats the last verdict rather than an empty success, which the chat
    /// would read as the refusal lifting.
    [Test]
    [Arguments(true, false, FeedStatus.Ok)]
    [Arguments(false, false, FeedStatus.Missing)]
    [Arguments(true, true, FeedStatus.Failed)]
    public async Task A_verdict_stands_until_the_next_fetch(bool running, bool fails, FeedStatus status) {
        var server = new Server { Missing = !fails, FailRequest = fails ? 1 : null };
        var clock = new FakeTimeProvider();
        using var feed = new RemoteSubagentFeed("s1", "a1", "claude", server.Read, () => running, clock, _ => { });

        var first = feed.ReadAppended();
        clock.Advance(RemoteSubagentFeed.PollInterval / 2);
        var throttled = feed.ReadAppended();

        await Assert.That((first.Status, throttled.Status, throttled.Failure, server.Requests.Count)).IsEqualTo((status, status, first.Failure, 1));
        server.Missing = false;
        server.Events.Add(Text(0, "a"));
        clock.Advance(RemoteSubagentFeed.PollInterval);
        await Assert.That(Texts(feed.ReadAppended())).IsEqualTo("a");
        clock.Advance(RemoteSubagentFeed.PollInterval / 2);
        var after = feed.ReadAppended();
        await Assert.That((after.Status, after.Lines.Count)).IsEqualTo((FeedStatus.Ok, 0));
    }

    /// An ended subagent the server still cannot find settles like one whose last page is in, so its polls slow down.
    [Test]
    public async Task An_ended_subagent_the_server_cannot_find_is_polled_at_the_ended_pace() {
        var server = new Server { Missing = true };
        var clock = new FakeTimeProvider();
        using var feed = new RemoteSubagentFeed("s1", "a1", "claude", server.Read, () => false, clock, _ => { });

        feed.ReadAppended();
        clock.Advance(RemoteSubagentFeed.PollInterval);
        feed.ReadAppended();
        clock.Advance(RemoteSubagentFeed.PollInterval);
        feed.ReadAppended();
        await Assert.That(server.Requests.Count).IsEqualTo(2);

        clock.Advance(RemoteSubagentFeed.EndedPollInterval);
        feed.ReadAppended();
        await Assert.That(server.Requests.Count).IsEqualTo(3);
    }

    /// A stream whose oldest events have aged out numbers its first kept event above zero; the feed still pages
    /// by how many it has received, so nothing after it is skipped.
    [Test]
    public async Task A_stream_that_starts_above_zero_is_read_whole() {
        var server = new Server { PageSize = 2 };
        server.Events.AddRange([Text(100, "a"), Text(101, "b"), Text(102, "c")]);
        var clock = new FakeTimeProvider();
        using var feed = new RemoteSubagentFeed("s1", "a1", "claude", server.Read, () => true, clock, _ => { });

        await Assert.That(Texts(feed.ReadAppended())).IsEqualTo("a,b,c");
        server.Events.Add(Text(103, "d"));
        clock.Advance(RemoteSubagentFeed.PollInterval);
        await Assert.That(Texts(feed.ReadAppended())).IsEqualTo("d");
    }

    /// The chat drops a failed read's lines, so a failure past a page already read keeps that page and
    /// the next poll resumes after it.
    [Test]
    public async Task A_failure_after_a_page_keeps_that_page() {
        var server = new Server { PageSize = 2, FailRequest = 2 };
        server.Events.AddRange([Text(0, "a"), Text(1, "b"), Text(2, "c")]);
        var clock = new FakeTimeProvider();
        using var feed = new RemoteSubagentFeed("s1", "a1", "claude", server.Read, () => true, clock, _ => { });

        var first = feed.ReadAppended();
        await Assert.That(first.Status).IsEqualTo(FeedStatus.Ok);
        await Assert.That(Texts(first)).IsEqualTo("a,b");

        clock.Advance(RemoteSubagentFeed.PollInterval);
        await Assert.That(Texts(feed.ReadAppended())).IsEqualTo("c");
    }
}
