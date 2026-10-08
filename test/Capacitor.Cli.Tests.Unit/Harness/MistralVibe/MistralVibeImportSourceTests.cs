using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Harness.MistralVibe;

namespace Capacitor.Cli.Tests.Unit.Harness.MistralVibe;

public class MistralVibeImportSourceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    MistralVibeImportSource Source() => new(new MistralVibePaths(new UserHome(Tmp.PathTo("home")), Tmp.Path), TimeProvider.System);

    static DiscoveryFilters NoFilters => new(FilterCwd: null, FilterSession: null, Since: null, MinLines: 1);

    [Test]
    public async Task Discovers_both_unified_and_legacy_sessions() {
        const string unifiedId = "11111111-1111-1111-1111-111111111111";
        Tmp.CreateFile($"logs/session/unified/{unifiedId}/meta.json", """{"cwd":"/w"}""");
        Tmp.CreateFile($"logs/session/unified/{unifiedId}/chunks/a.json",
            """[{"type":"message","role":"user","content":"hi","createdAt":"2026-10-07T00:00:01Z"}]""");

        const string legacyId = "22222222-2222-2222-2222-222222222222";
        Tmp.CreateFile("logs/session/session_20261007/meta.json", $$"""{"session_id":"{{legacyId}}","cwd":"/w2"}""");
        Tmp.CreateFile("logs/session/session_20261007/messages.jsonl", """{"role":"user","content":"hey"}""");

        var found = await Source().DiscoverAsync(NoFilters, CancellationToken.None);
        var ids   = found.Select(f => f.SessionId).ToHashSet();

        await Assert.That(found.Count).IsEqualTo(2);
        await Assert.That(ids).Contains(unifiedId.Replace("-", ""));
        await Assert.That(ids).Contains(legacyId.Replace("-", ""));
        await Assert.That(found.All(f => f.Vendor == HarnessId.MistralVibe)).IsTrue();
    }

    [Test]
    public async Task A_session_filter_narrows_discovery() {
        const string unifiedId = "33333333-3333-3333-3333-333333333333";
        Tmp.CreateFile($"logs/session/unified/{unifiedId}/meta.json", "{}");
        Tmp.CreateFile($"logs/session/unified/{unifiedId}/chunks/a.json",
            """[{"type":"message","role":"user","content":"hi"}]""");

        var filters = NoFilters with { FilterSession = unifiedId.Replace("-", "") };
        var found   = await Source().DiscoverAsync(filters, CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].SessionId).IsEqualTo(unifiedId.Replace("-", ""));
    }

    [Test]
    public async Task Is_available_only_when_the_log_dir_exists() {
        await Assert.That(Source().IsAvailable).IsFalse();
        Tmp.CreateFile("logs/session/unified/x/meta.json", "{}");
        await Assert.That(Source().IsAvailable).IsTrue();
    }
}
