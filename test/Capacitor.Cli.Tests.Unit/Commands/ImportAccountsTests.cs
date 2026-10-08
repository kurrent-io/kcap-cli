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
