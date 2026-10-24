using System.Collections.Frozen;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class AccountMemoryTests {
    const string Host = "0123456789abcdef0123456789abcdef";
    const string OtherHost = "fedcba9876543210fedcba9876543210";

    [Test]
    public async Task Lookup_matches_the_repo_part_under_the_platform_comparer_and_the_rest_ordinally() {
        var byRepo = AccountMemory.With(null, Host, "claude", "/home/a/kcap-cli", "a1");

        await Assert.That(AccountMemory.Lookup(byRepo, Host, "claude", "/home/a/kcap-cli")).IsEqualTo("a1");
        await Assert.That(AccountMemory.Lookup(byRepo, Host, "claude", "/home/a/kcap-cli/")).IsEqualTo("a1");
        await Assert.That(AccountMemory.Lookup(byRepo, Host, "claude", "/home/a/other")).IsNull();

        // The repo part follows the path rule of the platform; host and vendor never fold case.
        var cased = AccountMemory.Lookup(byRepo, Host, "claude", "/HOME/A/KCAP-CLI");
        await Assert.That(cased).IsEqualTo(OperatingSystem.IsLinux() ? null : "a1");
        await Assert.That(AccountMemory.Lookup(byRepo, Host.ToUpperInvariant(), "claude", "/home/a/kcap-cli")).IsNull();
        await Assert.That(AccountMemory.Lookup(byRepo, Host, "Claude", "/home/a/kcap-cli")).IsNull();
    }

    [Test]
    public async Task Lookup_without_a_store_finds_nothing() {
        await Assert.That(AccountMemory.Lookup(null, Host, "claude", "/home/a/kcap-cli")).IsNull();
    }

    [Test]
    public async Task With_replaces_a_matching_entry_and_null_removes_it() {
        var first = AccountMemory.With(null, Host, "claude", "/home/a/kcap-cli", "a1");
        var second = AccountMemory.With(first, Host, "claude", "/home/a/kcap-cli/", "a2");

        await Assert.That(second.Count).IsEqualTo(1);
        await Assert.That(AccountMemory.Lookup(second, Host, "claude", "/home/a/kcap-cli")).IsEqualTo("a2");

        var cleared = AccountMemory.With(second, Host, "claude", "/home/a/kcap-cli", null);

        await Assert.That(cleared.Count).IsEqualTo(0);
        await Assert.That(AccountMemory.Lookup(cleared, Host, "claude", "/home/a/kcap-cli")).IsNull();
        await Assert.That(cleared).IsSameReferenceAs(FrozenDictionary<string, string>.Empty);
    }

    [Test]
    public async Task Clearing_a_choice_nobody_made_adds_nothing() {
        var cleared = AccountMemory.With(null, Host, "claude", "/home/a/kcap-cli", null);

        await Assert.That(cleared.Count).IsEqualTo(0);
    }

    [Test]
    public async Task The_scratch_key_round_trips() {
        var byRepo = AccountMemory.With(null, Host, "codex", "", "a1");

        await Assert.That(AccountMemory.Lookup(byRepo, Host, "codex", "")).IsEqualTo("a1");
        await Assert.That(byRepo.Keys.Single()).IsEqualTo($"{Host}/codex:");
        await Assert.That(AccountMemory.Lookup(byRepo, Host, "codex", "/home/a/kcap-cli")).IsNull();
    }

    [Test]
    public async Task A_repo_path_that_holds_a_colon_survives_the_key_split() {
        var byRepo = AccountMemory.With(null, Host, "claude", "C:/work/kcap-cli", "a1");

        await Assert.That(AccountMemory.Lookup(byRepo, Host, "claude", "C:/work/kcap-cli")).IsEqualTo("a1");
    }

    [Test]
    public async Task Entries_for_another_host_or_vendor_are_untouched() {
        var seeded = AccountMemory.With(null, Host, "claude", "/home/a/kcap-cli", "a1");
        seeded = AccountMemory.With(seeded, OtherHost, "claude", "/home/a/kcap-cli", "b1");
        seeded = AccountMemory.With(seeded, Host, "codex", "/home/a/kcap-cli", "c1");

        var next = AccountMemory.With(seeded, Host, "claude", "/home/a/kcap-cli", "a2");

        await Assert.That(next.Count).IsEqualTo(3);
        await Assert.That(AccountMemory.Lookup(next, Host, "claude", "/home/a/kcap-cli")).IsEqualTo("a2");
        await Assert.That(AccountMemory.Lookup(next, OtherHost, "claude", "/home/a/kcap-cli")).IsEqualTo("b1");
        await Assert.That(AccountMemory.Lookup(next, Host, "codex", "/home/a/kcap-cli")).IsEqualTo("c1");

        var cleared = AccountMemory.With(next, Host, "claude", "/home/a/kcap-cli", null);

        await Assert.That(cleared.Count).IsEqualTo(2);
        await Assert.That(AccountMemory.Lookup(cleared, OtherHost, "claude", "/home/a/kcap-cli")).IsEqualTo("b1");
        await Assert.That(AccountMemory.Lookup(cleared, Host, "codex", "/home/a/kcap-cli")).IsEqualTo("c1");
    }
}
