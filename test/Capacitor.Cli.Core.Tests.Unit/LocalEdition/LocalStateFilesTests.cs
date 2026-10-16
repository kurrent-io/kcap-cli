using System.Text.Json;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalStateFilesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    LocalRoot Root => field ??= CreateRoot();

    LocalRoot CreateRoot() {
        var root = new LocalRoot(Tmp.PathTo("local"));
        root.EnsureCreated();
        return root;
    }

    static readonly LocalInstallation Installation = new("inst-1", 51234, "http://localhost:51234", "1.4.0");

    [Test]
    public async Task A_missing_registry_reads_as_absent() {
        await Assert.That(LocalStateFiles.ReadRegistry(Root)).IsTypeOf<StateRead<InstallRegistry>.Absent>();
    }

    [Test]
    public async Task A_missing_root_reads_as_absent() {
        var root = new LocalRoot(Tmp.PathTo("never-created"));

        await Assert.That(LocalStateFiles.ReadRegistry(root)).IsTypeOf<StateRead<InstallRegistry>.Absent>();
    }

    [Test]
    public async Task The_registry_round_trips_active_and_retired() {
        var registry = new InstallRegistry(Installation, [new RetiredInstallation("inst-0", 50000, "http://localhost:50000")]);

        LocalStateFiles.PublishRegistry(Root, registry);

        var read = (StateRead<InstallRegistry>.Present)LocalStateFiles.ReadRegistry(Root);
        await Assert.That(read.Value.Active).IsEqualTo(Installation);
        await Assert.That(read.Value.Retired.Single()).IsEqualTo(registry.Retired[0]);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("{\"schema\":1,\"active\":{\"id\":\"x\",\"server_url\":\"http://localhost:1\",\"version\":\"1\"}}")]
    [Arguments("{\"schema\":0}")]
    public async Task A_malformed_registry_is_unreadable_not_absent(string content) {
        File.WriteAllText(Root.RegistryPath, content);

        await Assert.That(LocalStateFiles.ReadRegistry(Root)).IsTypeOf<StateRead<InstallRegistry>.Unreadable>();
    }

    [Test]
    public async Task A_registry_from_a_newer_schema_says_to_update() {
        File.WriteAllText(Root.RegistryPath, "{\"schema\":2}");

        var read = (StateRead<InstallRegistry>.Unreadable)LocalStateFiles.ReadRegistry(Root);

        await Assert.That(read.Reason).Contains("newer kcap");
    }

    [Test]
    public async Task Published_files_are_owner_only() {
        if (OperatingSystem.IsWindows()) return;

        LocalStateFiles.PublishRegistry(Root, InstallRegistry.Empty);

        await Assert.That(File.GetUnixFileMode(Root.RegistryPath)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Publishing_replaces_a_link_at_the_path_instead_of_writing_through_it() {
        var victim = Tmp.CreateFile("victim.txt", "untouched");
        File.CreateSymbolicLink(Root.RegistryPath, victim);

        LocalStateFiles.PublishRegistry(Root, InstallRegistry.Empty);

        await Assert.That(File.ReadAllText(victim)).IsEqualTo("untouched");
        await Assert.That(new FileInfo(Root.RegistryPath).LinkTarget).IsNull();
    }

    /// <summary>The server's bootstrap reads <c>phase</c> and starts only on <c>server_start</c> equal to
    /// <c>"allow"</c>; these names and values are a contract with it.</summary>
    [Test]
    [Arguments(true, "allow")]
    [Arguments(false, "deny")]
    public async Task The_journal_carries_what_the_server_reads(bool allowed, string expected) {
        LocalStateFiles.WriteJournal(Root, Entry("staged", allowed));

        using var doc = JsonDocument.Parse(File.ReadAllText(Root.JournalPath));

        await Assert.That(doc.RootElement.GetProperty("phase").GetString()).IsEqualTo("staged");
        await Assert.That(doc.RootElement.GetProperty("server_start").GetString()).IsEqualTo(expected);
    }

    [Test]
    public async Task The_journal_round_trips() {
        var entry = Entry("started", allowed: true);

        LocalStateFiles.WriteJournal(Root, entry);

        var read = (StateRead<LifecycleJournalEntry>.Present)LocalStateFiles.ReadJournal(Root);
        await Assert.That(read.Value.Operation).IsEqualTo("setup");
        await Assert.That(read.Value.Phase).IsEqualTo("started");
        await Assert.That(read.Value.ServerStartAllowed).IsTrue();
        await Assert.That(read.Value.InstallationId).IsEqualTo("inst-1");
        await Assert.That(read.Value.StartedAt).IsEqualTo(entry.StartedAt);
        await Assert.That(read.Value.Details["port"]).IsEqualTo("51234");
    }

    [Test]
    public async Task Clearing_the_journal_leaves_it_absent() {
        LocalStateFiles.WriteJournal(Root, Entry("staged", allowed: false));

        LocalStateFiles.ClearJournal(Root);

        await Assert.That(LocalStateFiles.ReadJournal(Root)).IsTypeOf<StateRead<LifecycleJournalEntry>.Absent>();
    }

    static LifecycleJournalEntry Entry(string phase, bool allowed) =>
        new("setup", phase, allowed, "inst-1", new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero),
            new Dictionary<string, string> { ["port"] = "51234" });
}
