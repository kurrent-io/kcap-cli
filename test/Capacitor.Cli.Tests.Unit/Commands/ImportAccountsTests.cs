using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Harness.Codex;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportAccountsTests : IDisposable {
    const string CodexSid = "0199a2b3c4d5e6f708192a3b4c5d6e7f";

    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    static VendorAccount Account(string id, HarnessId vendor, string dir) =>
        new(id, vendor, AccountDirectory.Normalize(Directory.CreateDirectory(dir).FullName), id, DateTimeOffset.UnixEpoch);

    [Test]
    public async Task Import_sources_cover_every_registered_account_once() {
        var registry = new AccountRegistry {
            Accounts = [
                Account("a", HarnessId.Claude, Home.PathTo(".claude")),
                Account("b", HarnessId.Claude, Home.PathTo(".claude-work")),
                Account("c", HarnessId.Codex, Home.PathTo(".codex-b"))
            ]
        };

        var sources = SetupCommand.BuildImportSources(
            Config.Root, TestHarnesses.Under(Home), new GitProviderRouter(), TimeProvider.System,
            vendors: [HarnessId.Claude, HarnessId.Codex], accounts: registry, home: Home);

        await Assert.That(sources.OfType<ClaudeImportSource>().Count()).IsEqualTo(2);
        await Assert.That(sources.OfType<CodexImportSource>().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task Import_loads_sessions_from_two_claude_accounts_in_one_run() {
        var registry = new AccountRegistry {
            Accounts = [
                Account("a", HarnessId.Claude, Home.PathTo(".claude-a")),
                Account("b", HarnessId.Claude, Home.PathTo(".claude-b"))
            ]
        };
        WriteClaudeSession(".claude-a", "11111111-1111-1111-1111-111111111111");
        WriteClaudeSession(".claude-b", "22222222-2222-2222-2222-222222222222");
        StubImportHooks();

        var sources = SetupCommand.BuildImportSources(
            Config.Root, TestHarnesses.Under(Home), new GitProviderRouter(), TimeProvider.System,
            vendors: [HarnessId.Claude], accounts: registry, home: Home);

        ImportCommand.ImportRunOutcome? outcome = null;
        var exit = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home,
                TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), router: new GitProviderRouter(),
                time: TimeProvider.System, accounts: TestAccounts.None)
            .HandleImport(filterCwd: null, minLines: 1, sources: sources, scope: new ImportScope.All(),
                          skipConfirmation: true, skipTitle: true, onFinished: o => outcome = o);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(outcome!.Counts.Loaded).IsEqualTo(2);
    }

    [Test]
    public async Task Discovery_counts_sessions_from_two_claude_accounts() {
        var registry = new AccountRegistry {
            Accounts = [
                Account("a", HarnessId.Claude, Home.PathTo(".claude-a")),
                Account("b", HarnessId.Claude, Home.PathTo(".claude-b"))
            ]
        };
        WriteClaudeSession(".claude-a", "11111111-1111-1111-1111-111111111111");
        WriteClaudeSession(".claude-b", "22222222-2222-2222-2222-222222222222");

        var sources = SetupCommand.BuildImportSources(
            Config.Root, TestHarnesses.Under(Home), new GitProviderRouter(), TimeProvider.System,
            vendors: [HarnessId.Claude], accounts: registry, home: Home);

        ImportCommand.ImportDiscoveryResult? found = null;
        var exit = await new ImportCommand(Config.Root, Resolutions.None(Config.Root), Home,
                TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), router: new GitProviderRouter(),
                time: TimeProvider.System, accounts: TestAccounts.None)
            .HandleImport(filterCwd: null, minLines: 1, sources: sources, discoverOnly: true, onDiscovered: r => found = r);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(found!.ScannedVendors).IsEquivalentTo([HarnessId.Claude]);
        await Assert.That(found.Summary.UnmatchedByWindow.Values.Max()).IsEqualTo(2);
    }

    [Test]
    public async Task Setup_lane_discovery_counts_sessions_from_two_claude_accounts() {
        var store = new AccountStore(Tmp.PathTo("accounts"));
        store.Mutate(r => (r with {
            Accounts = [
                Account("a", HarnessId.Claude, Home.PathTo(".claude-a")),
                Account("b", HarnessId.Claude, Home.PathTo(".claude-b"))
            ]
        }, 0));
        WriteClaudeSession(".claude-a", "11111111-1111-1111-1111-111111111111");
        WriteClaudeSession(".claude-b", "22222222-2222-2222-2222-222222222222");

        var lane = new SetupImportLane(Config.Root, Resolutions.None(Config.Root), Home, new FixedCapacitorHttpClient(),
            TestHarnesses.Under(Home), new GitProviderRouter(), TimeProvider.System, accounts: store);

        var report = await lane.DiscoverAsync([HarnessId.Claude], DateTimeOffset.UtcNow, CancellationToken.None);

        await Assert.That(report).IsNotNull();
        await Assert.That(report!.Vendors).IsEquivalentTo(["claude"]);
    }

    [Test]
    public async Task Codex_titles_across_two_homes_come_from_each_homes_index_in_parallel() {
        var store = new AccountStore(Tmp.PathTo("accounts"));
        store.Mutate(r => (r with {
            Accounts = [Account("a", HarnessId.Codex, Home.PathTo(".codex-a")), Account("b", HarnessId.Codex, Home.PathTo(".codex-b"))]
        }, 0));

        var sessions = new List<ImportCommand.SessionClassification>();
        foreach (var account in new[] { "a", "b" }) {
            var ids = Enumerable.Range(0, 12).Select(i => $"0199a2b3c4d5e6f708192a3b4c5d{account}{i:x2}0").ToList();
            File.WriteAllLines(Home.PathTo($".codex-{account}", "session_index.jsonl"), ids.Select(id =>
                $$"""{"id":"{{Guid.ParseExact(id, "N"):D}}","thread_name":"From {{account}}","updated_at":"2026-05-01T10:20:30Z"}"""));
            sessions.AddRange(ids.Select(id => AlreadyLoadedCodex(id, Home.CreateFile([$".codex-{account}", "sessions", $"{id}.jsonl"], "{}\n"))));
        }

        _server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();
        await CodexImport(store).PostAlreadyLoadedCodexTitlesAsync(client, _server.Url!, sessions, new Progress<ImportProgress>(), CancellationToken.None);

        var titles = _server.FindLogEntries(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .Select(e => JsonNode.Parse(e.RequestMessage.Body!)!)
            .ToDictionary(b => b["session_id"]!.GetValue<string>(), b => b["title"]!.GetValue<string>());

        await Assert.That(titles.Count).IsEqualTo(24);
        foreach (var s in sessions)
            await Assert.That(titles[s.SessionId]).IsEqualTo(s.FilePath.Contains(".codex-a") ? "From a" : "From b");
    }

    [Test]
    public async Task The_account_registry_is_read_once_per_import() {
        var codexB = Home.PathTo(".codex-b");
        var store  = new AccountStore(Tmp.PathTo("accounts"));
        store.Mutate(r => (r with { Accounts = [Account("b", HarnessId.Codex, codexB)] }, 0));
        File.WriteAllText(Path.Combine(codexB, "session_index.jsonl"),
            $$"""{"id":"{{Guid.ParseExact(CodexSid, "N"):D}}","thread_name":"From account b","updated_at":"2026-05-01T10:20:30Z"}""" + "\n");
        var session = AlreadyLoadedCodex(CodexSid, Home.CreateFile([".codex-b", "sessions", "rollout.jsonl"], "{}\n"));

        _server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var       import = CodexImport(store);
        using var client = new HttpClient();
        await import.PostAlreadyLoadedCodexTitlesAsync(client, _server.Url!, [session], new Progress<ImportProgress>(), CancellationToken.None);
        store.Mutate(r => (r with { Accounts = [] }, 0));
        await import.PostAlreadyLoadedCodexTitlesAsync(client, _server.Url!, [session], new Progress<ImportProgress>(), CancellationToken.None);

        var titles = _server.LogEntries.Select(e => JsonNode.Parse(e.RequestMessage.Body!)!["title"]!.GetValue<string>()).ToList();
        await Assert.That(titles).IsEquivalentTo(["From account b", "From account b"]);
    }

    ImportCommand CodexImport(AccountStore store) =>
        new(Config.Root, Resolutions.None(Config.Root), Home, TestHarnesses.Under(Home),
            new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System, accounts: store);

    static ImportCommand.SessionClassification AlreadyLoadedCodex(string sessionId, string rollout) => new() {
        SessionId  = sessionId,
        FilePath   = rollout,
        EncodedCwd = "",
        Meta       = new SessionMetadata(),
        Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
        Vendor     = HarnessId.Codex
    };

    void WriteClaudeSession(string account, string sessionId) =>
        Home.CreateDir(account, "projects", "-tmp-accounts-proj").CreateFile(
            $"{sessionId}.jsonl",
            [.. Enumerable.Range(0, 5).Select(i =>
                $$$"""{"type":"user","sessionId":"{{{sessionId}}}","timestamp":"2026-03-15T10:00:0{{{i}}}Z","cwd":"/tmp/accounts-proj","message":{"content":"step {{{i}}}"}}""")]);

    void StubImportHooks() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var path in new[] { "/hooks/transcript", "/hooks/session-start*", "/hooks/subagent-start", "/hooks/subagent-stop" })
            _server.Given(Request.Create().WithPath(path).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));

        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
    }

    [Test]
    public async Task A_vendor_filter_drops_every_source_of_an_unwanted_vendor() {
        var registry = new AccountRegistry { Accounts = [Account("b", HarnessId.Claude, Home.PathTo(".claude-work"))] };

        var sources = SetupCommand.BuildImportSources(
            Config.Root, TestHarnesses.Under(Home), new GitProviderRouter(), TimeProvider.System,
            vendors: [HarnessId.Codex], accounts: registry, home: Home);

        await Assert.That(sources.OfType<ClaudeImportSource>().Count()).IsEqualTo(0);
        await Assert.That(sources.OfType<CodexImportSource>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task A_codex_rollout_takes_its_title_from_its_own_accounts_index() {
        var codexB = Home.PathTo(".codex-b");
        var store  = new AccountStore(Tmp.PathTo("accounts"));
        store.Mutate(r => (r with { Accounts = [Account("c", HarnessId.Codex, codexB)] }, 0));

        File.WriteAllText(Path.Combine(codexB, "session_index.jsonl"),
            $$"""{"id":"{{Guid.ParseExact(CodexSid, "N"):D}}","thread_name":"From account b","updated_at":"2026-05-01T10:20:30Z"}""" + "\n");

        var envHome = TestHarnesses.Under(Home).Of<CodexHarness>().Paths.Home;
        Directory.CreateDirectory(envHome);
        File.WriteAllText(Path.Combine(envHome, "session_index.jsonl"),
            $$"""{"id":"{{Guid.ParseExact(CodexSid, "N"):D}}","thread_name":"From environment","updated_at":"2026-05-01T10:20:30Z"}""" + "\n");

        var rollout = Path.Combine(codexB, "sessions", "2026", "05", "01", "rollout.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        File.WriteAllText(rollout, "{}\n");

        _server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var import = new ImportCommand(Config.Root, Resolutions.None(Config.Root), Home, TestHarnesses.Under(Home),
            new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System, accounts: store);

        var session = new ImportCommand.SessionClassification {
            SessionId  = CodexSid,
            FilePath   = rollout,
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
            Vendor     = HarnessId.Codex
        };

        using var client = new HttpClient();
        await import.PostAlreadyLoadedCodexTitlesAsync(client, _server.Url!, [session], new Progress<ImportProgress>(), CancellationToken.None);

        var posts = _server.FindLogEntries(Request.Create().WithPath("/hooks/harness-title").UsingPost());
        await Assert.That(posts.Count).IsEqualTo(1);
        await Assert.That(JsonNode.Parse(posts[0].RequestMessage.Body!)!["title"]!.GetValue<string>()).IsEqualTo("From account b");
    }
}
