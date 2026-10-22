using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountResolutionTests {
    const string WorkId  = "c1a0de01-work";
    const string PersId  = "c1a0de02-personal";
    const string CodexId = "c0dec001-codex";

    [TempDir] public required TempDir Tmp { get; init; }

    string Work     => Tmp.CreateDir(".claude-work");
    string Personal => Tmp.CreateDir(".claude");
    string CodexDir => Tmp.CreateDir(".codex");

    AccountRegistry Registry() => new() {
        Accounts = [
            Entry(WorkId, HarnessId.Claude, Work, "Work"),
            Entry(PersId, HarnessId.Claude, Personal, "Personal"),
            Entry(CodexId, HarnessId.Codex, CodexDir, "Codex")
        ]
    };

    static VendorAccount Entry(string id, HarnessId vendor, string dir, string label) =>
        new(id, vendor, AccountDirectory.Normalize(dir), label, DateTimeOffset.UnixEpoch);

    [Test]
    public async Task Null_id_resolves_to_the_default_directory_with_its_registered_id() {
        var outcome = AccountResolution.Resolve(Registry(), HarnessId.Claude, null, Personal);

        await Assert.That(outcome.Refused).IsFalse();
        await Assert.That(outcome.Account!.Directory).IsEqualTo(Personal);
        await Assert.That(outcome.Account.AccountId).IsEqualTo(PersId);
        await Assert.That(outcome.Account.Label).IsEqualTo("Personal");
    }

    [Test]
    public async Task Empty_id_is_the_default_and_a_null_registry_is_an_empty_one() {
        var empty = AccountResolution.Resolve(Registry(), HarnessId.Claude, "", Work);
        var none  = AccountResolution.Resolve(null, HarnessId.Claude, null, Work);

        await Assert.That(empty.Account!.AccountId).IsEqualTo(WorkId);
        await Assert.That(none.Refused).IsFalse();
        await Assert.That(none.Account!.AccountId).IsNull();
    }

    [Test]
    public async Task Null_id_with_an_unregistered_default_has_no_id_and_the_directory_name_as_label() {
        var other   = Tmp.CreateDir("elsewhere");
        var outcome = AccountResolution.Resolve(Registry(), HarnessId.Claude, null, other);

        await Assert.That(outcome.Account!.Directory).IsEqualTo(other);
        await Assert.That(outcome.Account.AccountId).IsNull();
        await Assert.That(outcome.Account.Label).IsEqualTo("elsewhere");
    }

    [Test]
    public async Task A_default_registered_only_for_another_vendor_carries_no_id() {
        var outcome = AccountResolution.Resolve(Registry(), HarnessId.Claude, null, CodexDir);

        await Assert.That(outcome.Account!.AccountId).IsNull();
        await Assert.That(outcome.Account.Label).IsEqualTo(".codex");
    }

    [Test]
    public async Task Registered_id_resolves_to_its_own_directory_and_label() {
        var outcome = AccountResolution.Resolve(Registry(), HarnessId.Claude, WorkId, Personal);

        await Assert.That(outcome.Refused).IsFalse();
        await Assert.That(outcome.Account!.Directory).IsEqualTo(AccountDirectory.Normalize(Work));
        await Assert.That(outcome.Account.AccountId).IsEqualTo(WorkId);
        await Assert.That(outcome.Account.Label).IsEqualTo("Work");
    }

    [Test]
    public async Task Unknown_id_is_refused_with_account_unavailable() {
        var outcome = AccountResolution.Resolve(Registry(), HarnessId.Claude, "nope", Personal);

        await Assert.That(outcome.Account).IsNull();
        await Assert.That(outcome.Refusal!).StartsWith(AccountResolution.Unavailable);
        await Assert.That(outcome.Refusal!).IsEqualTo($"{AccountResolution.Unavailable}: no Claude account nope");
    }

    [Test]
    public async Task Id_registered_for_another_vendor_is_refused() {
        var outcome = AccountResolution.Resolve(Registry(), HarnessId.Claude, CodexId, Personal);

        await Assert.That(outcome.Account).IsNull();
        await Assert.That(outcome.Refusal!).StartsWith(AccountResolution.Unavailable);
    }

    [Test]
    public async Task Registered_id_whose_directory_is_gone_is_refused_naming_the_directory() {
        var gone     = Tmp.PathTo(".claude-gone");
        var registry = new AccountRegistry { Accounts = [Entry("c1a0de03-gone", HarnessId.Claude, gone, "Gone")] };

        var outcome = AccountResolution.Resolve(registry, HarnessId.Claude, "c1a0de03-gone", Personal);

        await Assert.That(outcome.Account).IsNull();
        await Assert.That(outcome.Refusal!).StartsWith(AccountResolution.Unavailable);
        await Assert.That(outcome.Refusal!).Contains(AccountDirectory.Normalize(gone));
    }

    [Test]
    public async Task Resume_with_null_directory_resolves_as_a_fresh_launch() {
        var outcome = AccountResolution.ResolveForResume(Registry(), HarnessId.Claude, "removed", null, Personal);

        await Assert.That(outcome.Refused).IsFalse();
        await Assert.That(outcome.Account!.Directory).IsEqualTo(Personal);
        await Assert.That(outcome.Account.AccountId).IsEqualTo(PersId);
    }

    [Test]
    public async Task Resume_with_a_registered_id_resumes_under_the_stored_directory() {
        var stored  = Tmp.CreateDir("moved");
        var outcome = AccountResolution.ResolveForResume(Registry(), HarnessId.Claude, WorkId, stored, Personal);

        await Assert.That(outcome.Refused).IsFalse();
        await Assert.That(outcome.Account!.Directory).IsEqualTo(stored);
        await Assert.That(outcome.Account.AccountId).IsEqualTo(WorkId);
        await Assert.That(outcome.Account.Label).IsEqualTo("Work");
    }

    [Test]
    public async Task Resume_with_a_registered_id_now_removed_is_refused() {
        var outcome = AccountResolution.ResolveForResume(Registry(), HarnessId.Claude, "removed", Work, Personal);

        await Assert.That(outcome.Account).IsNull();
        await Assert.That(outcome.Refusal!).StartsWith(AccountResolution.Unavailable);
        await Assert.That(AccountResolution.ResolveForResume(Registry(), HarnessId.Claude, CodexId, Work, Personal).Refused).IsTrue();
    }

    [Test]
    public async Task Resume_with_a_registered_id_whose_directory_is_gone_is_refused() {
        var gone    = Tmp.PathTo("vanished");
        var outcome = AccountResolution.ResolveForResume(Registry(), HarnessId.Claude, WorkId, gone, Personal);

        await Assert.That(outcome.Account).IsNull();
        await Assert.That(outcome.Refusal!).StartsWith(AccountResolution.Unavailable);
        await Assert.That(outcome.Refusal!).Contains(gone);
    }

    [Test]
    public async Task Resume_with_null_id_resumes_under_the_stored_directory_while_it_exists() {
        var stored  = Tmp.CreateDir("adhoc");
        var outcome = AccountResolution.ResolveForResume(Registry(), HarnessId.Claude, null, stored, Personal);

        await Assert.That(outcome.Refused).IsFalse();
        await Assert.That(outcome.Account!.Directory).IsEqualTo(stored);
        await Assert.That(outcome.Account.AccountId).IsNull();
        await Assert.That(outcome.Account.Label).IsEqualTo("adhoc");

        var gone = AccountResolution.ResolveForResume(Registry(), HarnessId.Claude, null, Tmp.PathTo("vanished"), Personal);

        await Assert.That(gone.Refusal!).StartsWith(AccountResolution.Unavailable);
    }

    [Test]
    public async Task Argument_prefix_matching_one_id_resolves() {
        var outcome = AccountResolution.ResolveArgument(Registry(), HarnessId.Claude, "C1A0DE01");

        await Assert.That(outcome.Refused).IsFalse();
        await Assert.That(outcome.Account!.AccountId).IsEqualTo(WorkId);
        await Assert.That(outcome.Account.Directory).IsEqualTo(AccountDirectory.Normalize(Work));
        await Assert.That(outcome.Account.Label).IsEqualTo("Work");
    }

    [Test]
    public async Task Argument_prefix_matching_two_ids_is_refused_listing_both() {
        var outcome = AccountResolution.ResolveArgument(Registry(), HarnessId.Claude, "c1a0");

        await Assert.That(outcome.Account).IsNull();
        await Assert.That(outcome.Refusal!).StartsWith(AccountResolution.Unavailable);
        await Assert.That(outcome.Refusal!).Contains(WorkId);
        await Assert.That(outcome.Refusal!).Contains(PersId);
    }

    [Test]
    public async Task Argument_prefix_only_matches_ids_of_the_requested_vendor() {
        var outcome = AccountResolution.ResolveArgument(Registry(), HarnessId.Claude, "c0de");

        await Assert.That(outcome.Account).IsNull();
        await Assert.That(outcome.Refusal!).StartsWith(AccountResolution.Unavailable);
    }

    [Test]
    public async Task Argument_directory_resolves_by_same_directory() {
        var outcome = AccountResolution.ResolveArgument(Registry(), HarnessId.Claude, Work + Path.DirectorySeparatorChar);

        await Assert.That(outcome.Refused).IsFalse();
        await Assert.That(outcome.Account!.AccountId).IsEqualTo(WorkId);
        await Assert.That(outcome.Account.Directory).IsEqualTo(AccountDirectory.Normalize(Work));
    }

    [Test]
    public async Task Argument_unregistered_directory_is_refused_with_the_add_remedy() {
        var other   = Tmp.CreateDir("unregistered");
        var outcome = AccountResolution.ResolveArgument(Registry(), HarnessId.Claude, other);

        await Assert.That(outcome.Account).IsNull();
        await Assert.That(outcome.Refusal!).IsEqualTo(
            $"{AccountResolution.Unavailable}: {other} is not a registered Claude account; run kcap accounts add");
    }

    [Test]
    public async Task Argument_that_is_not_a_path_is_refused_rather_than_thrown() {
        var outcome = AccountResolution.ResolveArgument(Registry(), HarnessId.Claude, "a\0b");

        await Assert.That(outcome.Refusal!).StartsWith(AccountResolution.Unavailable);
    }
}
