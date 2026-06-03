using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using ProfileConfigJsonContext = Capacitor.Cli.Core.Config.ProfileConfigJsonContext;
using ProfileConfigJsonContextIndented = Capacitor.Cli.Core.Config.ProfileConfigJsonContextIndented;

namespace Capacitor.Cli.Tests.Unit;

public class ProfileCommandTests {
    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "kcap-test-" + Guid.NewGuid().ToString("N")[..8]
        );

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task AddProfile_CreatesNewProfile() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await ProfileCommand.AddProfile(
            configPath, "contoso", "https://contoso.kcap.io",
            ["github.com/contoso/*"]
        );

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.Profiles).ContainsKey("contoso");
        await Assert.That(config.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kcap.io");
        await Assert.That(config.Profiles["contoso"].Remotes).Contains("github.com/contoso/*");
    }

    [Test]
    public async Task RemoveProfile_DeletesProfile() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await ProfileCommand.RemoveProfile(configPath, "contoso");

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.Profiles).DoesNotContainKey("contoso");
    }

    [Test]
    public async Task AddProfile_SchemeLessInput_AddsHttpsAndStoresNormalizedUrl() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        // skipProbe defaults to true → no network, falls back to loopback heuristic.
        var result = await ProfileCommand.AddProfile(
            configPath, "contoso", "contoso.kcap.io", remotes: []);

        await Assert.That(result).IsEqualTo(0);

        var saved = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(configPath),
            ProfileConfigJsonContextIndented.Default.ProfileConfig)!;

        await Assert.That(saved.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kcap.io");
    }

    [Test]
    public async Task RemoveProfile_CannotRemoveDefault() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await ProfileCommand.RemoveProfile(configPath, "default");

        await Assert.That(result).IsEqualTo(1);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;
        await Assert.That(config.Profiles).ContainsKey("default");
    }
}
