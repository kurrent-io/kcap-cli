using System.Runtime.Versioning;
using System.Text.Json;
using Capacitor.Cli.Core.Auth;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>Each test first grants every local user read, on the config root or on the token itself, so a
/// token that kept that ACL would be readable by them.</summary>
[RunOn(OS.Windows)]
[SupportedOSPlatform("windows")]
public class TokenStoreDirectoryAclTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string TokensDir => Config.PathTo("tokens");

    [Test]
    public async Task A_saved_token_admits_only_the_user_and_SYSTEM() {
        WindowsAcl.GrantUsersRead(Config.Directory);

        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("a", MakeTokens("alice"));

        await Assert.That(WindowsAcl.IsProtected(TokensDir)).IsTrue();
        await Assert.That(WindowsAcl.Identities(Path.Combine(TokensDir, "a.json"))).IsEquivalentTo(WindowsAcl.OwnerOnly);
    }

    [Test]
    public async Task A_migrated_legacy_token_loses_the_grants_it_inherited_from_the_config_directory() {
        WindowsAcl.GrantUsersRead(Config.Directory);
        var legacy = Config.CreateFile("tokens.json", JsonSerializer.Serialize(MakeTokens("legacy"), CapacitorJsonContext.Default.StoredTokens));
        await Assert.That(WindowsAcl.Identities(legacy)).Contains(WindowsAcl.Users.Value);

        await AuthFixtures.NewTokenStore(Config.Root).MigrateLegacyAsync("a");

        await Assert.That(WindowsAcl.Identities(Path.Combine(TokensDir, "a.json"))).IsEquivalentTo(WindowsAcl.OwnerOnly);
    }

    [Test]
    public async Task A_migrated_legacy_token_loses_an_explicit_grant_of_its_own() {
        var legacy = Config.CreateFile("tokens.json", JsonSerializer.Serialize(MakeTokens("legacy"), CapacitorJsonContext.Default.StoredTokens));
        WindowsAcl.GrantUsersReadOnFile(legacy);
        await Assert.That(WindowsAcl.Identities(legacy)).Contains(WindowsAcl.Users.Value);

        await AuthFixtures.NewTokenStore(Config.Root).MigrateLegacyAsync("a");

        await Assert.That(WindowsAcl.Identities(Path.Combine(TokensDir, "a.json"))).IsEquivalentTo(WindowsAcl.OwnerOnly);
    }

    static StoredTokens MakeTokens(string username) => new() {
        AccessToken    = "t",
        ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
        GitHubUsername = username,
        Provider       = AuthProvider.GitHubApp,
    };
}
