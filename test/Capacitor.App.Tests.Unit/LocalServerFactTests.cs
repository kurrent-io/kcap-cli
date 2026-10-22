using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Tests.Unit;

public class LocalServerFactTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_root_with_nothing_installed_has_no_local_server() =>
        await Assert.That(LocalServerFact.Read(new LocalRoot(Tmp.PathTo("local")))).IsNull();

    [Test]
    public async Task A_pending_journal_counts_as_a_local_server_named_by_its_root() {
        var root = new LocalRoot(Tmp.CreateDir("local"));
        LocalStateFiles.WriteJournal(root, new(LocalSetupState.Operation, "pending", ServerStart.Deny, null, DateTimeOffset.UtcNow, new Dictionary<string, string>()));

        await Assert.That(LocalServerFact.Read(root)).IsEqualTo(root.Directory);
    }
}
