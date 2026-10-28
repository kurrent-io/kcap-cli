using System.Collections.Frozen;
using System.Text.Json;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountSnapshotBuilderTests {
    [TempHome] public required TempHome Home { get; init; }

    static VendorAccount Account(string id, HarnessId vendor, string directory, string label) =>
        new(id, vendor, AccountDirectory.Normalize(directory), label, DateTimeOffset.UnixEpoch);

    static readonly IReadOnlyDictionary<string, ReadingFile> NoReadings = FrozenDictionary<string, ReadingFile>.Empty;

    static AccountRegistry Registry(long revision, params VendorAccount[] accounts) => new() { Revision = revision, Accounts = accounts };

    string WiredClaude() {
        var dir = Home.CreateDir(".claude-work");
        Home.CreateFile(".claude-work/settings.json", """{ "enabledPlugins": { "kcap@kcap": true } }""");
        return dir;
    }

    [Test]
    public async Task Builds_one_view_per_account_with_key_label_and_recording_state() {
        var claude = WiredClaude();
        var codex  = Home.CreateDir(".codex-b");
        var registry = Registry(7,
            Account("c1", HarnessId.Claude, claude, "Work"),
            Account("x1", HarnessId.Codex, codex, "Spare"));

        var snapshot = AccountSnapshotBuilder.Build(registry, "host-1", new Dictionary<HarnessId, string>(), Home, NoReadings);

        await Assert.That(snapshot.HostId).IsEqualTo("host-1");
        await Assert.That(snapshot.Revision).IsEqualTo(7L);
        await Assert.That(snapshot.Accounts.Length).IsEqualTo(2);
        await Assert.That(snapshot.Accounts[0]).IsEqualTo(
            new AccountViewDto("c1", "claude", AccountDirectory.Normalize(claude), DirectoryKey.Of(claude), "Work", "installed"));
        await Assert.That(snapshot.Accounts[1]).IsEqualTo(
            new AccountViewDto("x1", "codex", AccountDirectory.Normalize(codex), DirectoryKey.Of(codex), "Spare", "not_wired"));
    }

    [Test]
    public async Task A_trusted_codex_account_publishes_recording() {
        var codex = Home.CreateDir(".codex-b");
        CodexHooksWriter.Install(Path.Combine(codex, "hooks.json"));
        var account = Account("x1", HarnessId.Codex, codex, "Spare");
        account = account with {
            HookTrust            = HookTrustStates.Trusted,
            HookTrustFingerprint = CodexTrustFingerprint.Of(AccountLayouts.Codex(Home, account.Directory)),
        };

        var snapshot = AccountSnapshotBuilder.Build(Registry(1, account), "host-1", new Dictionary<HarnessId, string>(), Home, NoReadings);

        await Assert.That(snapshot.Accounts.Single().RecordingState).IsEqualTo("recording");
    }

    [Test]
    public async Task Defaults_carry_directory_and_key_only() {
        var claude = WiredClaude();
        var codex  = Home.PathTo(".codex");
        var registry = Registry(1, Account("c1", HarnessId.Claude, claude, "Work"));

        var snapshot = AccountSnapshotBuilder.Build(registry, "host-1",
            new Dictionary<HarnessId, string> { [HarnessId.Codex] = codex, [HarnessId.Claude] = claude }, Home, NoReadings);

        await Assert.That(snapshot.Defaults.Length).IsEqualTo(2);
        await Assert.That(snapshot.Defaults[0]).IsEqualTo(new DefaultDirectoryDto("claude", claude, DirectoryKey.Of(claude)));
        await Assert.That(snapshot.Defaults[1]).IsEqualTo(new DefaultDirectoryDto("codex", codex, DirectoryKey.Of(codex)));
        await Assert.That(snapshot.Defaults[0].DirectoryKey).IsEqualTo(snapshot.Accounts[0].DirectoryKey);
    }

    [Test]
    public async Task Unregistered_default_matches_no_view_by_key() {
        var claude  = WiredClaude();
        var codex   = Home.CreateDir(".codex-b");
        var registry = Registry(1, Account("c1", HarnessId.Claude, claude, "Work"));

        var snapshot = AccountSnapshotBuilder.Build(registry, "host-1",
            new Dictionary<HarnessId, string> { [HarnessId.Codex] = codex }, Home, NoReadings);

        await Assert.That(snapshot.Accounts.Any(a => a.DirectoryKey == snapshot.Defaults[0].DirectoryKey)).IsFalse();
    }

    [Test]
    public async Task Keys_equal_across_casing_off_linux() {
        var work     = Home.CreateDir("Work");
        var registry = Registry(1, Account("c1", HarnessId.Claude, work, "Work"));

        var snapshot = AccountSnapshotBuilder.Build(registry, "host-1",
            new Dictionary<HarnessId, string> { [HarnessId.Claude] = Home.PathTo("work") }, Home, NoReadings);

        if (OperatingSystem.IsLinux())
            await Assert.That(snapshot.Defaults[0].DirectoryKey).IsNotEqualTo(snapshot.Accounts[0].DirectoryKey);
        else
            await Assert.That(snapshot.Defaults[0].DirectoryKey).IsEqualTo(snapshot.Accounts[0].DirectoryKey);
    }

    [Test]
    public async Task A_registry_entry_whose_directory_cannot_be_keyed_is_skipped() {
        var claude   = WiredClaude();
        var registry = Registry(1,
            Account("c1", HarnessId.Claude, claude, "Work"),
            new VendorAccount("bad", HarnessId.Codex, "/nul\0dir", "Bad", DateTimeOffset.UnixEpoch));

        var snapshot = AccountSnapshotBuilder.Build(registry, "host-1", new Dictionary<HarnessId, string>(), Home, NoReadings);

        await Assert.That(snapshot.Accounts.Select(a => a.Id).ToArray()).IsEquivalentTo(new[] { "c1" });
    }

    [Test]
    public async Task A_default_whose_directory_cannot_be_keyed_carries_the_raw_directory_as_its_key() {
        var snapshot = AccountSnapshotBuilder.Build(Registry(1), "host-1",
            new Dictionary<HarnessId, string> { [HarnessId.Codex] = "/nul\0dir" }, Home, NoReadings);

        await Assert.That(snapshot.Defaults[0]).IsEqualTo(new DefaultDirectoryDto("codex", "/nul\0dir", "/nul\0dir"));
    }

    static readonly DateTimeOffset Refreshed = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    static AccountReadingDto Reading(long generation) => new(generation,
        [new UsageWindowDto("codex", 300, 42.5, Refreshed.AddHours(5), Refreshed)], "pro", new CreditsDto(true, false, "10"), null);

    static IReadOnlyDictionary<string, ReadingFile> Readings(string id, AccountReadingDto reading) =>
        new Dictionary<string, ReadingFile> { [id] = new(reading, [new ReadingSession("s1", 3)]) };

    VendorAccount Identified(long generation) =>
        Account("x1", HarnessId.Codex, Home.CreateDir(".codex-b"), "Spare") with {
            Generation = generation, Email = "me@example.com", OrgId = "org-1", OrgName = "Acme", Plan = "pro",
            VendorAccountId = "acct-1", SignIn = "chatgpt", RefreshedAt = Refreshed, RefreshError = "late", LimitsError = "none",
        };

    [Test]
    public async Task View_carries_identity_and_the_reading_of_the_current_generation() {
        var reading  = Reading(2);
        var snapshot = AccountSnapshotBuilder.Build(Registry(1, Identified(2)), "host-1", new Dictionary<HarnessId, string>(), Home, Readings("x1", reading));

        var view = snapshot.Accounts.Single();
        await Assert.That(view with { Reading = null }).IsEqualTo(new AccountViewDto(
            "x1", "codex", view.Directory, view.DirectoryKey, "Spare", "not_wired",
            "me@example.com", "org-1", "Acme", "pro", "acct-1", "chatgpt", 2, Refreshed, "late", "none"));
        await Assert.That(view.Reading).IsSameReferenceAs(reading);
    }

    [Test]
    public async Task Reading_of_a_retired_generation_is_omitted() {
        var snapshot = AccountSnapshotBuilder.Build(Registry(1, Identified(3)), "host-1", new Dictionary<HarnessId, string>(), Home, Readings("x1", Reading(2)));

        await Assert.That(snapshot.Accounts.Single().Reading).IsNull();
        await Assert.That(snapshot.Accounts.Single().Generation).IsEqualTo(3L);
    }

    [Test]
    public async Task Retired_account_without_reading_publishes_its_new_generation() {
        var snapshot = AccountSnapshotBuilder.Build(Registry(1, Identified(4)), "host-1", new Dictionary<HarnessId, string>(), Home, NoReadings);

        var view = snapshot.Accounts.Single();
        await Assert.That(view.Generation).IsEqualTo(4L);
        await Assert.That(view.Reading).IsNull();
    }

    [Test]
    public async Task Published_view_never_contains_the_original_command() {
        var account = Identified(1) with {
            StatusLine      = new StatusLineBackup("/usr/local/bin/statusline --token SECRET-MARKER-123"),
            RetiredSessions = ["SECRET-MARKER-123-session"],
            ProbeSeq        = 9, CommittedProbeSeq = 8, LabelSetByUser = true,
        };
        var snapshot = AccountSnapshotBuilder.Build(Registry(1, account), "host-1", new Dictionary<HarnessId, string>(), Home, Readings("x1", Reading(1)));

        var local = JsonSerializer.Serialize(snapshot, AccountsSnapshotJsonContext.Default.AccountsSnapshotDto);
        var hub   = JsonSerializer.Serialize(snapshot, CapacitorJsonContext.Default.AccountsSnapshotDto);

        await Assert.That(local).DoesNotContain("SECRET-MARKER-123");
        await Assert.That(hub).DoesNotContain("SECRET-MARKER-123");
        await Assert.That(local).DoesNotContain("probe_seq");
        await Assert.That(local).DoesNotContain("label_set_by_user");
        await Assert.That(local).DoesNotContain("sessions");
    }
}
