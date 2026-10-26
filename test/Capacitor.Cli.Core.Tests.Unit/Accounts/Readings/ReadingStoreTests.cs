using System.Diagnostics;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using static Capacitor.Cli.Core.Tests.Unit.Accounts.Readings.ReadingFixtures;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts.Readings;

public class ReadingStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }

    AccountStore Store() => new(Tmp.PathTo("accounts"));

    static VendorAccount Codex(string id, long generation = 0, string[]? retired = null) =>
        new(id, HarnessId.Codex, $"/h/.codex-{id}", id, DateTimeOffset.UnixEpoch, Generation: generation, RetiredSessions: retired);

    static AccountStore Registered(AccountStore store, params VendorAccount[] accounts) {
        store.Mutate(r => (r with { Accounts = accounts }, 0));
        return store;
    }

    static Func<ReadingFile, ReadingFile> Observe(int at, double used = 10) =>
        file => file with {
            Reading = ReadingMerge.Apply(file.Reading, Obs(at, fresh: true, windows: [W("codex", 300, used, At(at + 300))])),
        };

    static string ReadingPath(AccountStore store, string id) => Path.Combine(store.Directory, "readings", $"{id}.json");

    [Test]
    public async Task TryRecord_writes_owner_only_file_and_directory() {
        if (OperatingSystem.IsWindows()) return;
        var store = Registered(Store(), Codex("a"));

        var written = store.Readings.TryRecord("a", 0, "s1", Observe(0));

        await Assert.That(written).IsTrue();
        await Assert.That(File.GetUnixFileMode(Path.Combine(store.Directory, "readings")))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Assert.That(File.GetUnixFileMode(ReadingPath(store, "a")))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(store.Readings.LoadAll()["a"].Reading.Windows.Single().UsedPercent).IsEqualTo(10.0);
    }

    [Test]
    public async Task TryRecord_skips_an_unregistered_account() {
        var store = Registered(Store(), Codex("a"));

        await Assert.That(store.Readings.TryRecord("b", 0, null, Observe(0))).IsFalse();
        await Assert.That(File.Exists(ReadingPath(store, "b"))).IsFalse();
    }

    [Test]
    public async Task TryRecord_skips_a_stale_generation() {
        var store = Registered(Store(), Codex("a", generation: 2));

        await Assert.That(store.Readings.TryRecord("a", 1, null, Observe(0))).IsFalse();
        await Assert.That(File.Exists(ReadingPath(store, "a"))).IsFalse();
    }

    [Test]
    public async Task TryRecord_skips_a_retired_session() {
        var store = Registered(Store(), Codex("a", retired: ["old"]));

        await Assert.That(store.Readings.TryRecord("a", 0, "old", Observe(0))).IsFalse();
        await Assert.That(store.Readings.TryRecord("a", 0, "new", Observe(0))).IsTrue();
    }

    [Test]
    public async Task TryRecord_skips_when_the_lock_is_held() {
        var store = Registered(Store(), Codex("a"));
        using var held = await Task.Run(() => store.Readings.TryLock(TimeSpan.FromSeconds(5)));
        await Assert.That(held).IsNotNull();

        var clock   = Stopwatch.StartNew();
        var written = store.Readings.TryRecord("a", 0, null, Observe(0));

        await Assert.That(written).IsFalse();
        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
        await Assert.That(File.Exists(ReadingPath(store, "a"))).IsFalse();
    }

    /// <summary>The removal is started while the writer holds <c>readings.lock</c> past its membership
    /// check; only a removal that waits for that lock before deleting leaves no file behind.</summary>
    [Test]
    public async Task Writer_paused_after_membership_while_removal_runs_does_not_recreate_the_file() {
        var  store   = Registered(Store(), Codex("a"));
        Task removal = Task.CompletedTask;

        store.Readings.AfterMembershipCheck = () => {
            removal = Task.Run(() => store.MutateWithReadings(
                (r, _) => (r with { Accounts = [.. r.Accounts.Where(a => a.Id != "a")] }, 0),
                (_, held) => held.Delete("a")));
            Thread.Sleep(300);
        };

        var written = store.Readings.TryRecord("a", 0, null, Observe(0));
        store.Readings.AfterMembershipCheck = null;
        await removal;

        await Assert.That(written).IsTrue();
        await Assert.That(store.Load().Accounts.Count).IsEqualTo(0);
        await Assert.That(File.Exists(ReadingPath(store, "a"))).IsFalse();
    }

    [Test]
    public async Task Unparseable_reading_file_is_replaced_by_the_next_observation() {
        var store = Registered(Store(), Codex("a"));
        Tmp.CreateFile("accounts/readings/a.json", "{not json");

        await Assert.That(store.Readings.LoadAll().ContainsKey("a")).IsFalse();
        await Assert.That(store.Readings.TryRecord("a", 0, null, Observe(0))).IsTrue();

        var file = store.Readings.LoadAll()["a"];
        await Assert.That(file.Reading.Windows.Single().UsedPercent).IsEqualTo(10.0);
        await Assert.That(file.Sessions.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"reading":{"generation":0,"windows":[],"blocked":{"blocked_at":null,"cleared_at":null,"constraints":[null]}},"sessions":[]}""")]
    [Arguments("""{"reading":{"generation":0,"windows":[null]},"sessions":[]}""")]
    [Arguments("""{"reading":{"generation":0,"windows":[]},"sessions":[null]}""")]
    public async Task Reading_file_with_a_null_element_is_skipped_and_replaced(string json) {
        var store = Registered(Store(), Codex("a"));
        Tmp.CreateFile("accounts/readings/a.json", json);

        await Assert.That(store.Readings.LoadAll().ContainsKey("a")).IsFalse();
        await Assert.That(store.Readings.TryRecord("a", 0, null, Observe(0))).IsTrue();
        await Assert.That(store.Readings.LoadAll()["a"].Reading.Windows.Single().UsedPercent).IsEqualTo(10.0);
    }

    [Test]
    public async Task Reading_file_without_metadataAt_binds_and_the_next_metadata_lands() {
        var store = Registered(Store(), Codex("a"));
        Tmp.CreateFile("accounts/readings/a.json",
            """{"reading":{"generation":0,"windows":[],"plan":"pro","credits":null,"blocked":null},"sessions":[]}""");

        var loaded = store.Readings.LoadAll()["a"].Reading;
        await Assert.That(loaded.Plan).IsEqualTo("pro");
        await Assert.That(loaded.MetadataAt).IsNull();

        await Assert.That(store.Readings.TryRecord("a", 0, null,
            f => f with { Reading = ReadingMerge.Apply(f.Reading, Obs(0, fresh: false, plan: "plus")) })).IsTrue();
        await Assert.That(store.Readings.LoadAll()["a"].Reading.Plan).IsEqualTo("plus");
        await Assert.That(store.Readings.LoadAll()["a"].Reading.MetadataAt).IsEqualTo(At(0));
    }

    /// <summary>The generation-1 file is what a crash between the registry save and the reading delete
    /// leaves behind.</summary>
    [Test]
    public async Task Reading_of_an_older_generation_is_reset_by_the_next_observation() {
        var store = Registered(Store(), Codex("a", generation: 1));
        await Assert.That(store.Readings.TryRecord("a", 1, "s1", f => Observe(0, used: 90)(f) with { Sessions = [new ReadingSession("s1", 5)] })).IsTrue();
        store.Mutate(r => (r with { Accounts = [Codex("a", generation: 2)] }, 0));

        await Assert.That(store.Readings.TryRecord("a", 2, null, Observe(10, used: 5))).IsTrue();

        var file = store.Readings.LoadAll()["a"];
        await Assert.That(file.Reading.Generation).IsEqualTo(2L);
        await Assert.That(file.Reading.Windows.Single().UsedPercent).IsEqualTo(5.0);
        await Assert.That(file.Sessions.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Held_read_normalizes_like_TryRecord_and_writes_land_after_the_save() {
        var store = Registered(Store(), Codex("a", generation: 3));
        Tmp.CreateFile("accounts/readings/a.json", "[]");

        var (read, _) = store.MutateWithReadings(
            (r, held) => (r, held.Read("a", 3)),
            (_, held) => held.Write("a", new ReadingFile(ReadingMerge.Empty(3), [new ReadingSession("s", null)], [At(0)])));

        await Assert.That(read.Reading.Generation).IsEqualTo(3L);
        await Assert.That(read.Sessions.Length).IsEqualTo(0);
        var stored = store.Readings.LoadAll()["a"];
        await Assert.That(stored.Sessions.Single().SessionId).IsEqualTo("s");
        await Assert.That(stored.RefreshOrigins!.Single()).IsEqualTo(At(0));
        await Assert.That(store.Readings.Stat().Length).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_registry_save_skips_the_reading_writes() {
        var store = Registered(Store(), Codex("a"));
        store.Readings.TryRecord("a", 0, null, Observe(0));
        var before   = File.ReadAllText(ReadingPath(store, "a"));
        var deleted  = false;

        File.Delete(store.RegistryPath);
        Tmp.CreateDir("accounts", "accounts.json");

        // Replacing a directory fails as IOException on Unix and UnauthorizedAccessException on Windows.
        Exception? thrown = null;
        try {
            store.MutateWithReadings(
                (r, _) => (r with { Revision = 9 }, 0),
                (_, held) => { deleted = true; held.Delete("a"); });
        } catch (Exception ex) {
            thrown = ex;
        }

        await Assert.That(thrown is IOException or UnauthorizedAccessException).IsTrue();
        await Assert.That(deleted).IsFalse();
        await Assert.That(File.ReadAllText(ReadingPath(store, "a"))).IsEqualTo(before);
    }
}
