using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountPathsTests {
    [TempHome] public required TempHome Home { get; init; }

    static AccountRegistry Registry(params (HarnessId Vendor, string Dir)[] accounts) => new() {
        Accounts = [.. accounts.Select(a => new VendorAccount(a.Dir, a.Vendor, AccountDirectory.Normalize(a.Dir), a.Dir, DateTimeOffset.UnixEpoch))]
    };

    [Test]
    public async Task Claude_transcript_resolves_to_its_account() {
        var work       = Home.CreateDir(".claude-work");
        var transcript = Home.CreateFile(".claude-work/projects/-repo/abc.jsonl", "");

        var paths = AccountPaths.ClaudeForTranscript(transcript, Registry((HarnessId.Claude, work)), Home);

        await Assert.That(paths!.Plans).IsEqualTo(Home.PathTo(".claude-work", "plans"));
    }

    [Test]
    public async Task Claude_transcript_outside_every_account_is_null() {
        var work = Home.CreateDir(".claude-work");

        var paths = AccountPaths.ClaudeForTranscript("/tmp/elsewhere/x.jsonl", Registry((HarnessId.Claude, work)), Home);

        await Assert.That(paths).IsNull();
    }

    [Test]
    public async Task Claude_transcript_in_a_sibling_with_a_shared_prefix_is_not_contained() {
        var claude     = Home.CreateDir(".claude");
        var transcript = Home.CreateFile(".claude-work/projects/-repo/abc.jsonl", "");

        var paths = AccountPaths.ClaudeForTranscript(transcript, Registry((HarnessId.Claude, claude)), Home);

        await Assert.That(paths).IsNull();
    }

    [Test]
    public async Task Codex_rollout_resolves_to_its_home() {
        var b       = Home.CreateDir(".codex-b");
        var rollout = Home.CreateFile(".codex-b/sessions/2026/10/08/rollout-x.jsonl", "");

        var paths = AccountPaths.CodexForRollout(rollout, Registry((HarnessId.Codex, b)), Home);

        await Assert.That(paths!.Home).IsEqualTo(AccountDirectory.Normalize(b));
    }

    [Test]
    public async Task A_vendor_mismatch_does_not_resolve() {
        var work       = Home.CreateDir(".claude-work");
        var transcript = Home.CreateFile(".claude-work/projects/-repo/abc.jsonl", "");

        await Assert.That(AccountPaths.CodexForRollout(transcript, Registry((HarnessId.Claude, work)), Home)).IsNull();
    }

    [Test]
    public async Task A_blank_or_malformed_path_resolves_to_null() {
        var registry = Registry((HarnessId.Claude, Home.CreateDir(".claude-work")), (HarnessId.Codex, Home.CreateDir(".codex-b")));

        foreach (var bad in new[] { "", "  ", "a\0b" }) {
            await Assert.That(AccountPaths.ClaudeForTranscript(bad, registry, Home)).IsNull();
            await Assert.That(AccountPaths.CodexForRollout(bad, registry, Home)).IsNull();
        }
    }

    [Test]
    public async Task A_relative_path_does_not_throw() {
        var registry = Registry((HarnessId.Claude, Home.CreateDir(".claude-work")));

        await Assert.That(AccountPaths.ClaudeForTranscript("projects/x.jsonl", registry, Home)).IsNull();
    }
}
