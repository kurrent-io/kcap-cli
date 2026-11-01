using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
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
        await Assert.That(vm.Saved).IsTrue();
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
        await Assert.That(vm.Saved).IsFalse();
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
        using var vm = new CaptureSettingsViewModel(store, visibilityOptions: _ => Task.FromResult<VisibilityOptionsRead?>(new(new() {
            Options = [new() { Key = "project", Label = "Project members" }], DefaultVisibility = "project",
        }, null)));
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
    public Task Local_capture_keeps_the_stored_audience_without_requesting_one() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        await store.SaveCaptureAsync(new Profile { DefaultVisibility = "public" }, CancellationToken.None);
        using var vm = new CaptureSettingsViewModel(store, isLocal: true,
            visibilityOptions: _ => throw new InvalidOperationException("Local settings must not ask for an audience"));
        await vm.RefreshVisibilityOptionsAsync();
        await Assert.That(vm.VisibilityVisible).IsFalse();
        vm.AllowedRepos = "team/repo";
        await vm.SaveAsync();
        await Assert.That(store.LoadProfile().DefaultVisibility).IsEqualTo("public");
        await Assert.That(store.LoadProfile().AllowedRepos).IsEquivalentTo(new[] { "team/repo" });
    });

    /// A failed read says nothing about the tenant: the page keeps the stored audience rather than
    /// offering every stop, and recording rules still save.
    [Test]
    public Task A_failed_options_read_keeps_the_stored_audience_and_still_saves_rules() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        await store.SaveCaptureAsync(new Profile { DefaultVisibility = "project" }, CancellationToken.None);
        using var vm = new CaptureSettingsViewModel(store,
            visibilityOptions: _ => Task.FromResult<VisibilityOptionsRead?>(VisibilityOptionsRead.Failed("HTTP 500")));
        await vm.RefreshVisibilityOptionsAsync();
        await Assert.That(vm.VisibilityEditable).IsFalse();
        vm.Visibility = "public";
        vm.AllowedRepos = "team/repo";
        await vm.SaveAsync();
        await Assert.That(store.LoadProfile().DefaultVisibility).IsEqualTo("project");
        await Assert.That(store.LoadProfile().AllowedRepos).IsEquivalentTo(new[] { "team/repo" });
    });

    [Test]
    public Task A_server_without_the_options_route_offers_every_audience() => AvaloniaSession.RunOnUiAsync(async () => {
        using var vm = new CaptureSettingsViewModel(Seed(),
            visibilityOptions: _ => Task.FromResult<VisibilityOptionsRead?>(VisibilityOptionsRead.NotServed));
        await vm.RefreshVisibilityOptionsAsync();
        await Assert.That(vm.VisibilityEditable).IsTrue();
        await Assert.That(vm.VisibilityOptions.Count).IsEqualTo(4);
    });

    /// Reading the options is not a save, so it holds nothing Settings would wait on to close.
    [Test]
    public Task Reading_options_is_loading_not_busy() => AvaloniaSession.RunOnUiAsync(async () => {
        var read = new TaskCompletionSource<VisibilityOptionsRead?>();
        using var vm = new CaptureSettingsViewModel(Seed(), visibilityOptions: _ => read.Task);
        var refresh = vm.RefreshVisibilityOptionsAsync();
        await Assert.That(vm.Loading).IsTrue();
        await Assert.That(vm.Busy).IsFalse();
        read.SetResult(VisibilityOptionsRead.NotServed);
        await refresh;
        await Assert.That(vm.Loading).IsFalse();
    });

    [Test]
    public Task A_hand_edited_audience_reads_as_the_cli_reads_it() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        await store.SaveCaptureAsync(new Profile { DefaultVisibility = "Private" }, CancellationToken.None);
        using var vm = new CaptureSettingsViewModel(store);
        await Assert.That(vm.Visibility).IsEqualTo("private");
        vm.AllowedRepos = "team/repo";
        await vm.SaveAsync();
        await Assert.That(store.LoadProfile().AllowedRepos).IsEquivalentTo(new[] { "team/repo" });
    });

    static Func<CancellationToken, Task<VisibilityOptionsRead?>> Offering(params string[] keys) =>
        _ => Task.FromResult<VisibilityOptionsRead?>(new(new() {
            Options = [.. keys.Select(k => new VisibilityOptionDto { Key = k, Label = k })], DefaultVisibility = keys[0],
        }, null));

    /// The three answers a tenant without an organization owner offers: the page must still show one selected.
    [Test]
    public Task A_saved_audience_the_server_offers_is_selected_on_entry() => AvaloniaSession.RunOnUiAsync(async () => {
        using var vm = new CaptureSettingsViewModel(Seed(), visibilityOptions: Offering("private", "project", "public"));
        await vm.RefreshVisibilityOptionsAsync();
        await Assert.That(vm.Visibility).IsEqualTo("private");
        await Assert.That(vm.VisibilityNote).IsNull();
    });

    [Test]
    public Task An_org_audience_on_a_server_without_one_selects_what_it_means_there_and_save_keeps_it() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        await store.SaveCaptureAsync(new Profile { DefaultVisibility = "org_public" }, CancellationToken.None);
        using (var vm = new CaptureSettingsViewModel(store, visibilityOptions: Offering("private", "project", "public"))) {
            await vm.RefreshVisibilityOptionsAsync();
            await Assert.That(vm.Visibility).IsEqualTo("project");
            await Assert.That(vm.VisibilityNote!).Contains("not offered by this server");
            await vm.SaveAsync();
            await Assert.That(vm.Saved).IsTrue();
            await Assert.That(vm.VisibilityNote).IsNull();
        }
        await Assert.That(store.LoadProfile().DefaultVisibility).IsEqualTo("project");

        using var reopened = new CaptureSettingsViewModel(store, visibilityOptions: Offering("private", "project", "public"));
        await reopened.RefreshVisibilityOptionsAsync();
        await Assert.That(reopened.Visibility).IsEqualTo("project");
        await Assert.That(reopened.VisibilityNote).IsNull();
    });

    [Test]
    [Arguments("org_public", new[] { "private", "project", "org_public", "public" }, "org_public")]
    [Arguments("org_public", new[] { "private", "project", "public" }, "project")]
    [Arguments("org_public", new[] { "private", "public" }, "private")]
    [Arguments("project", new[] { "private", "public" }, "private")]
    [Arguments("public", new[] { "private", "public" }, "public")]
    [Arguments("private", new[] { "project" }, null)]
    public async Task A_saved_audience_resolves_to_the_offered_answer_with_its_readers(string stored, string[] offered, string? expected) =>
        await Assert.That(CaptureSettingsViewModel.Effective(stored, [.. offered.Select(o => new VisibilityOption(o, o, ""))])).IsEqualTo(expected);

    [Test]
    public Task An_audience_saved_elsewhere_replaces_the_selection_on_reload() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        using var vm = new CaptureSettingsViewModel(store, visibilityOptions: Offering("private", "project", "public"));
        await vm.RefreshVisibilityOptionsAsync();
        await store.SaveCaptureAsync(new Profile { DefaultVisibility = "org_public" }, CancellationToken.None);
        await vm.ReloadSavedVisibilityAsync();
        await Assert.That(vm.Visibility).IsEqualTo("project");
        await Assert.That(vm.VisibilityNote).IsNotNull();
    });

    [Test]
    public Task A_reload_keeps_an_unsaved_choice() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        using var vm = new CaptureSettingsViewModel(store, visibilityOptions: Offering("private", "project", "public"));
        await vm.RefreshVisibilityOptionsAsync();
        vm.Visibility = "public";
        await store.SaveCaptureAsync(new Profile { DefaultVisibility = "project" }, CancellationToken.None);
        await vm.ReloadSavedVisibilityAsync();
        await Assert.That(vm.Visibility).IsEqualTo("public");
    });

    [Test]
    public Task Folder_rules_accept_the_home_forms_the_cli_expands() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        using var vm = new CaptureSettingsViewModel(store);
        var native = "~" + Path.DirectorySeparatorChar + "Projects";
        vm.AllowedPaths = $"~/work\n{native}";
        await vm.SaveAsync();
        await Assert.That(store.LoadProfile().AllowedPaths).IsEquivalentTo(new[] { "~/work", native });
    });

    /// With the options unreadable the page never writes an audience, so a change made elsewhere
    /// since it opened survives a save of the recording rules.
    [Test]
    public Task An_unreadable_offer_leaves_an_audience_changed_elsewhere() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        await store.SaveCaptureAsync(new Profile { DefaultVisibility = "public" }, CancellationToken.None);
        using var vm = new CaptureSettingsViewModel(store,
            visibilityOptions: _ => Task.FromResult<VisibilityOptionsRead?>(null));
        await vm.RefreshVisibilityOptionsAsync();
        await store.SaveCaptureAsync(new Profile { DefaultVisibility = "private" }, CancellationToken.None);
        vm.AllowedRepos = "team/repo";
        await vm.SaveAsync();
        await Assert.That(store.LoadProfile().DefaultVisibility).IsEqualTo("private");
        await Assert.That(store.LoadProfile().AllowedRepos).IsEquivalentTo(new[] { "team/repo" });
    });
}
