using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountAdoptionTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task EnsureDefault_registers_the_environment_directory_once() {
        var store = new AccountStore(Tmp.PathTo("accounts"));
        var dir   = Tmp.CreateDir(".claude");

        var first  = AccountAdoption.EnsureDefault(store, HarnessId.Claude, dir, TimeProvider.System);
        var second = AccountAdoption.EnsureDefault(store, HarnessId.Claude, dir + Path.DirectorySeparatorChar, TimeProvider.System);

        await Assert.That(second.Id).IsEqualTo(first.Id);
        await Assert.That(AccountAdoption.Of(store, HarnessId.Claude).Count).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureDefault_labels_the_account_after_its_directory() {
        var store = new AccountStore(Tmp.PathTo("accounts"));

        var account = AccountAdoption.EnsureDefault(store, HarnessId.Codex, Tmp.CreateDir(".codex"), TimeProvider.System);

        await Assert.That(account.Label).IsEqualTo(".codex");
    }
}
