using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class UseCommandTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Use_InRepo_SetsProfileBinding() {
        var configPath = AppConfig.GetConfigPath(Config.Root);

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await new UseCommand(Config.Root, new WorkingDirectory(AppContext.BaseDirectory), AuthFixtures.NewTokenStore(Config.Root)).SetProfile("contoso", repoPath: "/repos/my-project", global: false, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.ProfileBindings["/repos/my-project"]).IsEqualTo("contoso");
        await Assert.That(config.ActiveProfile).IsEqualTo("default");
    }

    [Test]
    public async Task Use_Global_SetsActiveProfile() {
        var configPath = AppConfig.GetConfigPath(Config.Root);

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await new UseCommand(Config.Root, new WorkingDirectory(AppContext.BaseDirectory), AuthFixtures.NewTokenStore(Config.Root)).SetProfile("contoso", repoPath: null, global: true, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.ActiveProfile).IsEqualTo("contoso");
    }

    [Test]
    public async Task Use_Save_WritesRepoConfig() {
        var configPath = AppConfig.GetConfigPath(Config.Root);
        var repoRoot   = Config.CreateDir("repo");

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await new UseCommand(Config.Root, new WorkingDirectory(AppContext.BaseDirectory), AuthFixtures.NewTokenStore(Config.Root)).SetProfile("contoso", repoPath: repoRoot, global: false, save: true, savePath: repoRoot);

        await Assert.That(result).IsEqualTo(0);

        var repoConfigPath = repoRoot.PathTo(".kcap.json");
        await Assert.That(File.Exists(repoConfigPath)).IsTrue();

        var repoConfigJson = await File.ReadAllTextAsync(repoConfigPath);
        var repoConfig = JsonSerializer.Deserialize(repoConfigJson, RepoConfigJsonContext.Default.RepoConfig)!;

        await Assert.That(repoConfig.Profile).IsEqualTo("contoso");
        await Assert.That(repoConfig.ServerUrl).IsEqualTo("https://contoso.com");
    }

    [Test]
    public async Task Use_UnknownProfile_ReturnsError() {
        var configPath = AppConfig.GetConfigPath(Config.Root);

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await new UseCommand(Config.Root, new WorkingDirectory(AppContext.BaseDirectory), AuthFixtures.NewTokenStore(Config.Root)).SetProfile("nonexistent", repoPath: "/repos/x", global: false, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(1);
    }

    UseCommand Command() => new(Config.Root, new WorkingDirectory(AppContext.BaseDirectory), AuthFixtures.NewTokenStore(Config.Root));

    async Task SeedTwo(string active) =>
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = active,
            Profiles = new Dictionary<string, Profile> { ["a"] = new() { ServerUrl = "https://a.example" }, ["b"] = new() { ServerUrl = "https://b.example" } }
        });

    [Test]
    public async Task Use_UnknownProfile_RefusesAndChangesNothing() {
        await SeedTwo("a");
        var before = await File.ReadAllTextAsync(AppConfig.GetConfigPath(Config.Root));

        var result = await Command().SetProfile("zzz", repoPath: null, global: true, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(AppConfig.GetConfigPath(Config.Root))).IsEqualTo(before);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Use_Global_MovesTheLegacyCredentialToTheOutgoingProfile(bool withGlobalFlag) {
        await SeedTwo("a");
        var legacy = Config.PathTo("tokens.json");
        await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(
            new StoredTokens { AccessToken = "at", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GitHubUsername = "legacy-a", Provider = AuthProvider.GitHubApp },
            CapacitorJsonContext.Default.StoredTokens));

        // Without --global, a null repo path is the global arm too.
        var result = await Command().SetProfile("b", repoPath: null, global: withGlobalFlag, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(File.Exists(legacy)).IsFalse();
        await Assert.That(File.Exists(Config.PathTo("tokens", "a.json"))).IsTrue();
        await Assert.That(ConfigMutator.LoadPure(AppConfig.GetConfigPath(Config.Root)).ActiveProfile).IsEqualTo("b");

        await Command().SetProfile("a", repoPath: null, global: true, save: false, savePath: null);
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadForProfileAsync("a"))!.GitHubUsername).IsEqualTo("legacy-a");
    }

    [Test]
    public async Task Use_Global_UnknownProfile_LeavesTheLegacyCredentialAlone() {
        await SeedTwo("a");
        var legacy = Config.PathTo("tokens.json");
        await File.WriteAllTextAsync(legacy, "{}");

        var result = await Command().SetProfile("zzz", repoPath: null, global: true, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(File.Exists(legacy)).IsTrue();
        await Assert.That(File.Exists(Config.PathTo("tokens", "a.json"))).IsFalse();
    }

    /// An active name config holds but the token layout rejects is a refusal with a line, not a crash.
    [Test]
    public async Task Use_Global_ReportsAnActiveNameTheTokenLayoutRejects() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = "bad/name",
            Profiles = new Dictionary<string, Profile> { ["bad/name"] = new(), ["b"] = new() { ServerUrl = "https://b.example" } }
        });
        await File.WriteAllTextAsync(Config.PathTo("tokens.json"), "{}");

        var result = await Command().SetProfile("b", repoPath: null, global: true, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(ConfigMutator.LoadPure(AppConfig.GetConfigPath(Config.Root)).ActiveProfile).IsEqualTo("bad/name");
    }

    [Test]
    public async Task Use_Global_RefusesWhenTheMigrationFails() {
        await SeedTwo("a");
        await File.WriteAllTextAsync(Config.PathTo("tokens.json"), "{}");
        await File.WriteAllTextAsync(Config.PathTo("tokens"), "a file where the directory should be");

        var result = await Command().SetProfile("b", repoPath: null, global: true, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(ConfigMutator.LoadPure(AppConfig.GetConfigPath(Config.Root)).ActiveProfile).IsEqualTo("a");
    }

    [Test]
    public async Task Use_RefusesAnUnreadableConfig() {
        var path = AppConfig.GetConfigPath(Config.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ not json");

        var result = await Command().SetProfile("b", repoPath: "/repos/x", global: false, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("{ not json");
    }
}
