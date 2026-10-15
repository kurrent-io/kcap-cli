using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class CaptureSettingsViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    SettingsProfileStore Seed() {
        ConfigMutator.Mutate(Config.Root, c => c with {
            ActiveProfile = "other",
            Profiles = new() {
                ["work"] = new Profile { ServerUrl = "https://work.example", DefaultVisibility = "private" },
                ["other"] = new Profile { ServerUrl = "https://other.example" },
            }
        });
        return new SettingsProfileStore(Config.Root, "work", "https://work.example");
    }

    [Test]
    public Task Save_targets_bound_profile_and_preserves_concurrent_unrelated_edits() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        using var vm = new CaptureSettingsViewModel(store) {
            Visibility = "project", AllowedRepos = " team/repo \nteam/repo\r\nteam/second", ExcludedRepos = "team/private",
            AllowedPaths = "~/work", ExcludedPaths = Config.PathTo("private"),
        };
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new(c.Profiles) {
            ["work"] = c.Profiles["work"] with { DisableMemoryIndex = true, Daemon = new DaemonSettings { MaxAgents = 9 } }
        } });
        await vm.SaveAsync();
        var saved = store.LoadProfile();
        await Assert.That(saved.DefaultVisibility).IsEqualTo("project");
        await Assert.That(saved.AllowedRepos).IsEquivalentTo(new[] { "team/repo", "team/second" });
        await Assert.That(saved.ExcludedRepos).IsEquivalentTo(new[] { "team/private" });
        await Assert.That(saved.AllowedPaths).IsEquivalentTo(new[] { "~/work" });
        await Assert.That(saved.ExcludedPaths).IsEquivalentTo(new[] { Config.PathTo("private") });
        await Assert.That(saved.DisableMemoryIndex).IsTrue();
        await Assert.That(saved.Daemon!.MaxAgents).IsEqualTo(9);
        await Assert.That(new SettingsProfileStore(Config.Root, "other", "https://other.example").LoadProfile().DefaultVisibility).IsEqualTo("org_public");
    });

    [Test]
    [Arguments("unknown", "team/repo", "~/work")]
    [Arguments("private", "https://github.com/team/repo", "~/work")]
    [Arguments("private", "team/repo", "relative/folder")]
    public Task Invalid_input_does_not_change_configuration(string visibility, string repos, string paths) => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        using var vm = new CaptureSettingsViewModel(store) { Visibility = visibility, AllowedRepos = repos, AllowedPaths = paths };
        await vm.SaveAsync();
        await Assert.That(store.LoadProfile().DefaultVisibility).IsEqualTo("private");
        await Assert.That(store.LoadProfile().AllowedRepos).IsEmpty();
        await Assert.That(vm.Message).IsNotNull();
    });

    [Test]
    public Task Repointed_profile_is_not_overwritten() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        using var vm = new CaptureSettingsViewModel(store) { Visibility = "public" };
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new(c.Profiles) {
            ["work"] = c.Profiles["work"] with { ServerUrl = "https://replacement.example" }
        } });
        await vm.SaveAsync();
        await Assert.That(vm.Message!).Contains("profile changed");
        await Assert.That(new SettingsProfileStore(Config.Root, "work", "https://replacement.example").LoadProfile().DefaultVisibility).IsEqualTo("private");
    });

    [Test]
    public Task Empty_lists_clear_restrictions() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        await store.SaveCaptureAsync(new Profile { AllowedRepos = ["team/repo"], ExcludedPaths = ["~/private"] }, CancellationToken.None);
        using var vm = new CaptureSettingsViewModel(store) { AllowedRepos = "\n ", ExcludedPaths = "" };
        await vm.SaveAsync();
        await Assert.That(store.LoadProfile().AllowedRepos).IsEmpty();
        await Assert.That(store.LoadProfile().ExcludedPaths).IsEmpty();
    });

    [Test]
    public Task Tenant_options_clear_an_unavailable_choice_and_refuse_to_save_it() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        using var vm = new CaptureSettingsViewModel(store, visibilityOptions: _ => Task.FromResult<VisibilityOptionsResponse?>(new() {
            Options = [new() { Key = "project", Label = "Project members" }], DefaultVisibility = "project",
        }));
        await vm.RefreshVisibilityOptionsAsync();
        await Assert.That(vm.Visibility).IsNull();
        await Assert.That(vm.VisibilityOptions.Select(o => o.Value)).IsEquivalentTo(new[] { "project" });
        vm.Visibility = "public";
        await vm.SaveAsync();
        await Assert.That(store.LoadProfile().DefaultVisibility).IsEqualTo("private");
        vm.Visibility = "project";
        await vm.SaveAsync();
        await Assert.That(store.LoadProfile().DefaultVisibility).IsEqualTo("project");
    });

    [Test]
    public Task Local_capture_keeps_sessions_private_without_requesting_an_audience() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        await store.SaveCaptureAsync(new Profile { DefaultVisibility = "public" }, CancellationToken.None);
        using var vm = new CaptureSettingsViewModel(store, isLocal: true,
            visibilityOptions: _ => throw new InvalidOperationException("Local settings must not ask for an audience"));
        await vm.RefreshVisibilityOptionsAsync();
        await Assert.That(vm.VisibilityVisible).IsFalse();
        vm.AllowedRepos = "team/repo";
        await vm.SaveAsync();
        await Assert.That(store.LoadProfile().DefaultVisibility).IsEqualTo("private");
        await Assert.That(store.LoadProfile().AllowedRepos).IsEquivalentTo(new[] { "team/repo" });
    });
}
