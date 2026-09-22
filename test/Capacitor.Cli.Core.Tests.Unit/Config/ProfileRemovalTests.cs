using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

public class ProfileRemovalTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string TokensDir  => Config.PathTo("tokens");
    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

    static StoredTokens Tokens(string user) => new() {
        AccessToken = "at", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GitHubUsername = user, Provider = AuthProvider.GitHubApp
    };

    async Task Seed(string active = "keep") =>
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = active,
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new(),
                ["keep"]    = new() { ServerUrl = "https://keep.example" },
                ["gone"]    = new() { ServerUrl = "https://gone.example" }
            },
            ProfileBindings = new Dictionary<string, string> { ["/repo/a"] = "gone", ["/repo/b"] = "keep" }
        });

    [Test]
    public async Task Removes_the_profile_its_bindings_and_its_token() {
        await Seed();
        var store = AuthFixtures.NewTokenStore(Config.Root);
        await store.SaveAsync("gone", Tokens("g"));
        await store.SaveAsync("keep", Tokens("k"));

        var result = await ProfileRemoval.RemoveAsync(Config.Root, store, "gone");

        await Assert.That(result.Outcome).IsEqualTo(ProfileRemovalOutcome.Removed);
        var config = ConfigMutator.LoadPure(ConfigPath);
        await Assert.That(config.Profiles.ContainsKey("gone")).IsFalse();
        await Assert.That(config.Profiles.ContainsKey("keep")).IsTrue();
        await Assert.That(config.ProfileBindings.ContainsKey("/repo/a")).IsFalse();
        await Assert.That(config.ProfileBindings["/repo/b"]).IsEqualTo("keep");
        await Assert.That(File.Exists(Path.Combine(TokensDir, "gone.json"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "keep.json"))).IsTrue();
    }

    [Test]
    public async Task Refuses_default_a_missing_profile_and_the_active_profile() {
        await Seed(active: "gone");
        var store = AuthFixtures.NewTokenStore(Config.Root);
        await store.SaveAsync("gone", Tokens("g"));
        var before = await File.ReadAllTextAsync(ConfigPath);

        await Assert.That((await ProfileRemoval.RemoveAsync(Config.Root, store, "default")).Outcome).IsEqualTo(ProfileRemovalOutcome.IsDefault);
        await Assert.That((await ProfileRemoval.RemoveAsync(Config.Root, store, "nope")).Outcome).IsEqualTo(ProfileRemovalOutcome.NotFound);
        await Assert.That((await ProfileRemoval.RemoveAsync(Config.Root, store, "gone")).Outcome).IsEqualTo(ProfileRemovalOutcome.IsActive);

        await Assert.That(await File.ReadAllTextAsync(ConfigPath)).IsEqualTo(before);
        await Assert.That(File.Exists(Path.Combine(TokensDir, "gone.json"))).IsTrue();
    }

    [Test]
    public async Task An_unreadable_config_removes_nothing() {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, "{ not json");
        var store = AuthFixtures.NewTokenStore(Config.Root);
        await store.SaveAsync("gone", Tokens("g"));

        var result = await ProfileRemoval.RemoveAsync(Config.Root, store, "gone");

        await Assert.That(result.Outcome).IsEqualTo(ProfileRemovalOutcome.ConfigUnreadable);
        await Assert.That(await File.ReadAllTextAsync(ConfigPath)).IsEqualTo("{ not json");
        await Assert.That(File.Exists(Path.Combine(TokensDir, "gone.json"))).IsTrue();
    }

    [Test]
    public async Task A_case_alias_keeps_the_shared_token_file() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new(),
                ["acme"]    = new() { ServerUrl = "https://acme.example" },
                ["Acme"]    = new() { ServerUrl = "https://acme.example" }
            }
        });
        var store = AuthFixtures.NewTokenStore(Config.Root);
        await store.SaveAsync("acme", Tokens("a"));

        var result = await ProfileRemoval.RemoveAsync(Config.Root, store, "acme");

        await Assert.That(result.Outcome).IsEqualTo(ProfileRemovalOutcome.Removed);
        await Assert.That(ConfigMutator.LoadPure(ConfigPath).Profiles.ContainsKey("acme")).IsFalse();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "acme.json"))).IsTrue();
    }

    /// A name config accepts but the token layout rejects: the profile still goes, and the refusal
    /// to touch its credential is reported rather than thrown.
    [Test]
    public async Task A_profile_the_token_layout_rejects_is_removed_with_its_token_retained() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> { ["default"] = new(), ["bad/name"] = new() }
        });
        var store = AuthFixtures.NewTokenStore(Config.Root);

        var result = await ProfileRemoval.RemoveAsync(Config.Root, store, "bad/name");

        await Assert.That(result.Outcome).IsEqualTo(ProfileRemovalOutcome.RemovedTokenRetained);
        await Assert.That(ConfigMutator.LoadPure(ConfigPath).Profiles.ContainsKey("bad/name")).IsFalse();
    }

    [Test]
    public async Task A_token_that_cannot_be_deleted_is_reported_with_its_path() {
        await Seed();
        await File.WriteAllTextAsync(TokensDir, "a file where the directory should be");
        var store = AuthFixtures.NewTokenStore(Config.Root);

        var result = await ProfileRemoval.RemoveAsync(Config.Root, store, "gone");

        await Assert.That(result.Outcome).IsEqualTo(ProfileRemovalOutcome.RemovedTokenRetained);
        await Assert.That(result.Detail!).Contains(Path.Combine(TokensDir, "gone.json"));
        await Assert.That(ConfigMutator.LoadPure(ConfigPath).Profiles.ContainsKey("gone")).IsFalse();
    }
}
