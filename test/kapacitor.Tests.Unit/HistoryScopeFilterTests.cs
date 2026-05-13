using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class HistoryScopeFilterTests {
    static (string SessionId, string FilePath, string EncodedCwd) T(string id) =>
        (id, $"/tmp/{id}.jsonl", $"-tmp-proj-{id}");

    static Func<(string SessionId, string FilePath, string EncodedCwd), CancellationToken, ValueTask<(string? Owner, string? Name)>>
        Resolver(Dictionary<string, (string Owner, string Name)?> map) =>
        (t, _) => new ValueTask<(string?, string?)>(
            map.TryGetValue(t.SessionId, out var v) && v is { } x ? (x.Owner, x.Name) : (null, null));

    [Test]
    public async Task Apply_All_returns_every_transcript_including_unresolved() {
        var transcripts = new[] { T("a"), T("b"), T("c") };
        var resolver = Resolver(new() { ["a"] = ("EventStore", "kapacitor"), ["b"] = null });

        var kept = await HistoryScopeFilter.Apply(transcripts, new ImportScope.All(), resolver);

        await Assert.That(kept.Select(x => x.SessionId).ToArray()).IsEquivalentTo(new[] { "a", "b", "c" });
    }

    [Test]
    public async Task Apply_Org_keeps_only_matching_owner() {
        var transcripts = new[] { T("a"), T("b"), T("c") };
        var resolver = Resolver(new() {
            ["a"] = ("EventStore", "kapacitor"),
            ["b"] = ("kurrent-io", "secret"),
            ["c"] = ("EventStore", "kurrentdb"),
        });

        var kept = await HistoryScopeFilter.Apply(transcripts, new ImportScope.Org("EventStore"), resolver);

        await Assert.That(kept.Select(x => x.SessionId).ToArray()).IsEquivalentTo(new[] { "a", "c" });
    }

    [Test]
    public async Task Apply_Org_matches_case_insensitively() {
        var transcripts = new[] { T("a") };
        var resolver = Resolver(new() { ["a"] = ("eventstore", "kapacitor") });

        var kept = await HistoryScopeFilter.Apply(transcripts, new ImportScope.Org("EventStore"), resolver);

        await Assert.That(kept).HasCount(1);
    }

    [Test]
    public async Task Apply_Org_drops_unresolved_repos() {
        var transcripts = new[] { T("a") };
        var resolver = Resolver(new() { ["a"] = null });

        var kept = await HistoryScopeFilter.Apply(transcripts, new ImportScope.Org("EventStore"), resolver);

        await Assert.That(kept).IsEmpty();
    }

    [Test]
    public async Task Apply_Repo_keeps_only_exact_match() {
        var transcripts = new[] { T("a"), T("b"), T("c") };
        var resolver = Resolver(new() {
            ["a"] = ("EventStore", "kapacitor"),
            ["b"] = ("EventStore", "kurrentdb"),
            ["c"] = ("EventStore", "kapacitor"),
        });

        var kept = await HistoryScopeFilter.Apply(
            transcripts, new ImportScope.Repo("EventStore", "kapacitor"), resolver);

        await Assert.That(kept.Select(x => x.SessionId).ToArray()).IsEquivalentTo(new[] { "a", "c" });
    }
}
