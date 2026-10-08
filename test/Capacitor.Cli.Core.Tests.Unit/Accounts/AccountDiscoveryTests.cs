using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountDiscoveryTests {
    [TempHome] public required TempHome Home { get; init; }

    static string? NoEnv(string _) => null;

    [Test]
    public async Task Finds_claude_and_codex_lookalikes_with_vendor_files() {
        Home.CreateFile(".claude-work/settings.json", "{}");
        Home.CreateFile(".codex-b/config.toml", "");
        Directory.CreateDirectory(Home.PathTo(".claude-empty"));

        var found = AccountDiscovery.Find(Home, new AccountRegistry(), NoEnv);

        await Assert.That(found.Select(c => (c.Vendor, Path.GetFileName(c.Directory))))
            .IsEquivalentTo(new[] { (HarnessId.Claude, ".claude-work"), (HarnessId.Codex, ".codex-b") });
    }

    [Test]
    public async Task Skips_registered_directories() {
        Home.CreateFile(".claude-work/settings.json", "{}");
        var registry = new AccountRegistry {
            Accounts = [new("x", HarnessId.Claude, AccountDirectory.Normalize(Home.PathTo(".claude-work")), "x", DateTimeOffset.UnixEpoch)]
        };

        await Assert.That(AccountDiscovery.Find(Home, registry, NoEnv).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Offers_the_environment_override() {
        var dir = Path.GetDirectoryName(Home.CreateFile("custom/claude/settings.json", "{}"))!;

        var found = AccountDiscovery.Find(Home, new AccountRegistry(), k => k == "CLAUDE_CONFIG_DIR" ? dir : null);

        await Assert.That(found.Single().Directory).IsEqualTo(AccountDirectory.Normalize(dir));
    }

    [Test]
    public async Task Finds_claude_swap_profiles() {
        var swapBase = OperatingSystem.IsLinux() ? ".local/share/claude-swap" : ".claude-swap-backup";
        Home.CreateFile($"{swapBase}/sessions/2-user_x.com/settings.json", "{}");

        var found = AccountDiscovery.Find(Home, new AccountRegistry(), NoEnv);

        await Assert.That(found.Any(c => c.Directory.EndsWith("2-user_x.com", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Never_reads_vendor_file_contents() {
        Home.CreateFile(".claude-work/settings.json", "{ not even json");

        await Assert.That(AccountDiscovery.Find(Home, new AccountRegistry(), NoEnv).Count).IsEqualTo(1);
    }
}
