using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Recording;

namespace Capacitor.Cli.Core.Tests.Unit.Recording;

public class RecordingStoreTests {
    [TempHome] public required TempHome Home { get; init; }

    static AccountRegistry Registry(params (HarnessId Vendor, string Dir)[] accounts) => new() {
        Accounts = [.. accounts.Select(a => new VendorAccount($"id-{Path.GetFileName(a.Dir)}", a.Vendor, AccountDirectory.Normalize(a.Dir), a.Dir, DateTimeOffset.UnixEpoch))]
    };

    /// <summary>The registry resolves Claude to the default directory, as a hook with a scrubbed
    /// <c>CLAUDE_CONFIG_DIR</c> would; the transcript names another store, and that one wins.</summary>
    [Test]
    public async Task Claude_store_is_read_off_the_transcript_path() {
        var work       = Home.CreateDir(".claude-work");
        var transcript = Home.CreateFile(".claude-work/projects/-repo/abc.jsonl", "");

        var store = RecordingStore.Of(HarnessId.Claude, transcript, TestHarnesses.Under(Home));

        await Assert.That(store).IsEqualTo(AccountDirectory.Normalize(work));
    }

    [Test]
    public async Task Codex_store_is_read_off_the_rollout_path() {
        var codex   = Home.CreateDir("codex-b");
        var rollout = Home.CreateFile("codex-b/sessions/2026/10/30/rollout-2026-10-30T10-00-00-abc.jsonl", "");

        var store = RecordingStore.Of(HarnessId.Codex, rollout, TestHarnesses.Under(Home));

        await Assert.That(store).IsEqualTo(AccountDirectory.Normalize(codex));
    }

    [Test]
    [Arguments("elsewhere/abc.jsonl")]
    [Arguments(".claude/abc.jsonl")]
    [Arguments(".claude/history/-repo/abc.jsonl")]
    public async Task An_unrecognised_claude_path_is_unknown(string relative) {
        Home.CreateDir(".claude");
        var transcript = Home.CreateFile(relative, "");

        await Assert.That(RecordingStore.Of(HarnessId.Claude, transcript, TestHarnesses.Under(Home))).IsNull();
    }

    [Test]
    public async Task An_unrecognised_codex_path_is_unknown() {
        var rollout = Home.CreateFile(".codex/sessions/rollout-abc.jsonl", "");

        await Assert.That(RecordingStore.Of(HarnessId.Codex, rollout, TestHarnesses.Under(Home))).IsNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task No_transcript_is_unknown_for_a_vendor_with_stores(string? transcript) {
        Home.CreateDir(".claude");

        await Assert.That(RecordingStore.Of(HarnessId.Claude, transcript, TestHarnesses.Under(Home))).IsNull();
        await Assert.That(RecordingStore.Of(HarnessId.Codex, transcript, TestHarnesses.Under(Home))).IsNull();
    }

    [Test]
    public async Task A_symlinked_store_is_recorded_physically() {
        var real       = Home.CreateDir("real-claude");
        var link       = Home.PathTo("linked-claude");
        Directory.CreateSymbolicLink(link, real);
        var transcript = Home.CreateFile("real-claude/projects/-repo/abc.jsonl", "");
        var viaLink    = Path.Combine(link, "projects", "-repo", "abc.jsonl");

        var store = RecordingStore.Of(HarnessId.Claude, viaLink, TestHarnesses.Under(Home));

        await Assert.That(store).IsEqualTo(AccountDirectory.Normalize(real));
        await Assert.That(File.Exists(transcript)).IsTrue();
    }

    [Test]
    public async Task Acp_vendor_store_is_its_root() {
        var gemini = Home.CreateDir(".gemini");

        var store = RecordingStore.Of(HarnessId.Gemini, null, TestHarnesses.Under(Home));

        await Assert.That(store).IsEqualTo(AccountDirectory.Normalize(gemini));
    }

    [Test]
    public async Task Acp_vendor_transcript_inside_its_root_agrees() {
        var copilot    = Home.CreateDir(".copilot");
        var transcript = Home.CreateFile(".copilot/session-state/abc/events.jsonl", "");

        var store = RecordingStore.Of(HarnessId.Copilot, transcript, TestHarnesses.Under(Home));

        await Assert.That(store).IsEqualTo(AccountDirectory.Normalize(copilot));
    }

    [Test]
    public async Task Acp_vendor_transcript_outside_its_root_is_unknown() {
        Home.CreateDir(".copilot");
        var transcript = Home.CreateFile("other-copilot/session-state/abc/events.jsonl", "");

        await Assert.That(RecordingStore.Of(HarnessId.Copilot, transcript, TestHarnesses.Under(Home))).IsNull();
    }

    [Test]
    [Arguments(HarnessId.Pi)]
    [Arguments(HarnessId.Muse)]
    [Arguments(HarnessId.Antigravity)]
    [Arguments(HarnessId.MistralVibe)]
    public async Task Vendors_without_native_resume_record_no_store(HarnessId vendor) =>
        await Assert.That(RecordingStore.Of(vendor, null, TestHarnesses.Under(Home))).IsNull();

    [Test]
    public async Task Account_is_the_registered_entry_for_the_store() {
        var work     = Home.CreateDir(".claude-work");
        var registry = Registry((HarnessId.Claude, work), (HarnessId.Codex, Home.CreateDir(".codex")));

        await Assert.That(RecordingStore.AccountFor(HarnessId.Claude, AccountDirectory.Normalize(work), registry)).IsEqualTo("id-.claude-work");
        await Assert.That(RecordingStore.AccountFor(HarnessId.Codex, AccountDirectory.Normalize(work), registry)).IsNull();
        await Assert.That(RecordingStore.AccountFor(HarnessId.Claude, Home.CreateDir("unregistered"), registry)).IsNull();
        await Assert.That(RecordingStore.AccountFor(HarnessId.Claude, null, registry)).IsNull();
        await Assert.That(RecordingStore.AccountFor(HarnessId.Claude, AccountDirectory.Normalize(work), null)).IsNull();
    }
}
