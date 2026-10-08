using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

[NotInParallel("AuthProviderDiscoveryCache")]
public class ClaudeHookPlanAccountTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    sealed class RecordingHandler : HttpMessageHandler {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/hooks/session-start") Bodies.Add(body);

            return new HttpResponseMessage(request.Method == HttpMethod.Get ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        }
    }

    AccountStore StoreWith(params VendorAccount[] accounts) {
        var store = new AccountStore(Tmp.PathTo("accounts"));
        store.Mutate(r => (r with { Accounts = [.. accounts] }, 0));

        return store;
    }

    async Task<string?> SessionStartBodyAsync(AccountStore store, string transcriptPath) {
        var handler  = new RecordingHandler();
        var profiles = Resolutions.At("http://localhost", Config.Root);
        var spool    = new HookSpool(Tmp.PathTo("spool"), time: TimeProvider.System);
        var payload  = $$"""{"hook_event_name":"SessionStart","session_id":"s1","slug":"my-plan","cwd":"{{Tmp.Path.Replace("\\", "\\\\")}}","transcript_path":"{{transcriptPath.Replace("\\", "\\\\")}}"}""";

        await new ClaudeHookCommand(Config.Root, profiles, new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home), HostedAgent.Terminal,
                new FixedCapacitorHttpClient(), TestWatchers.For(Config.Root, profiles, new FixedCapacitorHttpClient()), FakeProcessStarter.Refusing(),
                router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), accounts: store)
            .HandleCore(new HttpClient(handler), AuthStatus.Ok, spool, new StringReader(payload));

        return handler.Bodies.SingleOrDefault();
    }

    [Test]
    public async Task Plan_is_read_from_the_account_that_wrote_the_transcript() {
        var work = AccountDirectory.Normalize(Home.PathTo(".claude-work"));
        Home.CreateFile([".claude-work", "plans", "my-plan.md"], "# Plan B");
        var transcript = Home.CreateFile([".claude-work", "projects", "-repo", "s1.jsonl"]);
        var store      = StoreWith(new VendorAccount("work", HarnessId.Claude, work, "work", DateTimeOffset.UnixEpoch));

        var body = await SessionStartBodyAsync(store, transcript);

        await Assert.That(body).Contains("\"plan_content\":\"# Plan B\"");
    }

    [Test]
    public async Task Plan_falls_back_to_the_environment_layout_when_the_registry_is_corrupt() {
        Home.CreateFile([".claude", "plans", "my-plan.md"], "# Plan Default");
        var transcript = Home.CreateFile([".claude-work", "projects", "-repo", "s1.jsonl"]);
        Tmp.CreateFile("accounts/accounts.json", "{not json");

        var body = await SessionStartBodyAsync(new AccountStore(Tmp.PathTo("accounts")), transcript);

        await Assert.That(body).Contains("\"plan_content\":\"# Plan Default\"");
    }
}
