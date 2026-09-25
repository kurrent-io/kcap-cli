using System.Reactive.Threading.Tasks;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class ProfilesSettingsViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string TokensDir => Config.PathTo("tokens");

    static StoredTokens Token(string provider, DateTimeOffset expires, string? server = null, string? refresh = null) => new() {
        AccessToken = "at", ExpiresAt = expires, Provider = provider, ServerUrl = server, RefreshToken = refresh, ClientId = refresh is null ? null : "cid",
        GitHubUsername = "user"
    };

    async Task<TokenStore> Seed() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = "work",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new(),
                ["work"]    = new() { ServerUrl = "https://work.example" },
                ["other"]   = new() { ServerUrl = "https://other.example" },
                ["stale"]   = new() { ServerUrl = "https://stale.example" },
                ["moved"]   = new() { ServerUrl = "https://moved.example" },
                ["open"]    = new() { ServerUrl = "https://open.example", AuthProvider = new AuthProviderStamp(AuthProvider.None, "https://open.example") },
                ["bad"]     = new() { ServerUrl = "https://bad.example" }
            }
        });
        var tokens = AuthFixtures.NewTokenStore(Config.Root);
        await tokens.SaveAsync("work",  Token(AuthProvider.GitHubApp, DateTimeOffset.UtcNow.AddHours(1), "https://work.example"));
        await tokens.SaveAsync("stale", Token(AuthProvider.WorkOS, DateTimeOffset.UtcNow.AddHours(-1), "https://stale.example"));
        await tokens.SaveAsync("moved", Token(AuthProvider.GitHubApp, DateTimeOffset.UtcNow.AddHours(1), "https://elsewhere.example"));
        Directory.CreateDirectory(Path.Combine(TokensDir, "bad.json")); // a directory where the file should be: the read throws
        return tokens;
    }

    ProfilesSettingsViewModel Make(TokenStore tokens, string bound = "work",
            Func<string, string, CancellationToken, Task>? openSignIn = null,
            Func<string, CancellationToken, Task<bool>>? confirm = null) =>
        new(Config.Root, tokens, new OnboardingGate(Config.Root, tokens, ProfileOverrides.None, TimeProvider.System), bound,
            openSignIn ?? ((_, _, _) => Task.CompletedTask), confirm ?? ((_, _) => Task.FromResult(true)));

    [Test]
    public Task Rows_reflect_config_marks_and_credential_status() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed());
        await vm.RefreshAsync();

        var rows = vm.Rows.ToDictionary(r => r.Name);
        await Assert.That(rows.Count).IsEqualTo(7);
        await Assert.That(rows["work"].Status).IsEqualTo(ProfileCredentialStatus.SignedIn);
        await Assert.That(rows["work"].IsActive).IsTrue();
        await Assert.That(rows["work"].IsBound).IsTrue();
        await Assert.That(rows["work"].CanRemove).IsFalse();
        await Assert.That(rows["other"].Status).IsEqualTo(ProfileCredentialStatus.SignedOut);
        await Assert.That(rows["other"].CanRemove).IsTrue();
        await Assert.That(rows["other"].CanSignIn).IsTrue();
        await Assert.That(rows["stale"].Status).IsEqualTo(ProfileCredentialStatus.Expired);
        await Assert.That(rows["moved"].Status).IsEqualTo(ProfileCredentialStatus.OtherServer);
        await Assert.That(rows["open"].Status).IsEqualTo(ProfileCredentialStatus.NoSignInNeeded);
        await Assert.That(rows["open"].CanSignIn).IsFalse();
        await Assert.That(rows["default"].Status).IsEqualTo(ProfileCredentialStatus.NoServer);
        await Assert.That(rows["default"].CanSignIn).IsFalse();
        await Assert.That(rows["default"].CanRemove).IsFalse();
        await Assert.That(rows["bad"].Status).IsEqualTo(ProfileCredentialStatus.Unreadable);
        await Assert.That(rows["bad"].CanSignIn).IsTrue();
    });

    [Test]
    public Task Active_and_bound_are_marked_separately() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed(), bound: "other");
        await vm.RefreshAsync();

        var rows = vm.Rows.ToDictionary(r => r.Name);
        await Assert.That(rows["work"].IsActive).IsTrue();
        await Assert.That(rows["work"].IsBound).IsFalse();
        await Assert.That(rows["work"].CanRemove).IsFalse();
        await Assert.That(rows["other"].IsBound).IsTrue();
        await Assert.That(rows["other"].CanRemove).IsFalse();
    });

    [Test]
    public Task Remove_deletes_the_profile_and_its_token_after_confirmation() => AvaloniaSession.RunOnUiAsync(async () => {
        var tokens = await Seed();
        string? asked = null;
        var vm = Make(tokens, confirm: (name, _) => { asked = name; return Task.FromResult(true); });
        await vm.RefreshAsync();

        await vm.RemoveCommand.Execute(vm.Rows.Single(r => r.Name == "stale")).ToTask();

        await Assert.That(asked).IsEqualTo("stale");
        await Assert.That(vm.Rows.Any(r => r.Name == "stale")).IsFalse();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "stale.json"))).IsFalse();
        await Assert.That(vm.Message!).Contains("removed");
    });

    [Test]
    public Task Remove_does_nothing_when_declined() => AvaloniaSession.RunOnUiAsync(async () => {
        var tokens = await Seed();
        var vm = Make(tokens, confirm: (_, _) => Task.FromResult(false));
        await vm.RefreshAsync();

        await vm.RemoveCommand.Execute(vm.Rows.Single(r => r.Name == "stale")).ToTask();

        await Assert.That(vm.Rows.Any(r => r.Name == "stale")).IsTrue();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "stale.json"))).IsTrue();
    });

    [Test]
    public Task Remove_of_a_row_changed_since_the_last_read_is_refused() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed());
        await vm.RefreshAsync();
        var row = vm.Rows.Single(r => r.Name == "other");
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles = new Dictionary<string, Profile>(c.Profiles) { ["other"] = new() { ServerUrl = "https://repointed.example" } }
        });

        await vm.RemoveCommand.Execute(row).ToTask();

        await Assert.That(vm.Message).IsEqualTo("This profile changed; the list was refreshed.");
        await Assert.That(vm.Rows.Single(r => r.Name == "other").ServerUrl).IsEqualTo("https://repointed.example");
    });

    [Test]
    public Task SignIn_opens_the_dialog_for_the_row() => AvaloniaSession.RunOnUiAsync(async () => {
        (string Profile, string Server)? opened = null;
        var vm = Make(await Seed(), openSignIn: (p, s, _) => { opened = (p, s); return Task.CompletedTask; });
        await vm.RefreshAsync();

        await vm.SignInCommand.Execute(vm.Rows.Single(r => r.Name == "other")).ToTask();

        await Assert.That(opened).IsEqualTo(("other", "https://other.example"));
    });

    [Test]
    public Task SignIn_of_a_row_with_no_server_is_refused() => AvaloniaSession.RunOnUiAsync(async () => {
        var opened = false;
        var vm = Make(await Seed(), openSignIn: (_, _, _) => { opened = true; return Task.CompletedTask; });
        await vm.RefreshAsync();

        await vm.SignInCommand.Execute(vm.Rows.Single(r => r.Name == "default")).ToTask();

        await Assert.That(opened).IsFalse();
        await Assert.That(vm.Message).IsEqualTo("This profile has no server to sign in to.");
    });

    [Test]
    public Task Refresh_reports_an_unreadable_config_and_keeps_the_rows() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed());
        await vm.RefreshAsync();
        var before = vm.Rows.Count;
        await File.WriteAllTextAsync(AppConfig.GetConfigPath(Config.Root), "{ not json");

        await Assert.That(await vm.RefreshAsync()).IsFalse();

        await Assert.That(vm.Message).IsEqualTo("Could not read the profile configuration.");
        await Assert.That(vm.Rows.Count).IsEqualTo(before);
    });

    /// The rows kept after a failed read are not a match: an action on one of them stops at the read.
    [Test]
    public Task Actions_are_refused_while_the_config_is_unreadable() => AvaloniaSession.RunOnUiAsync(async () => {
        var opened = false;
        var vm = Make(await Seed(), openSignIn: (_, _, _) => { opened = true; return Task.CompletedTask; });
        await vm.RefreshAsync();
        var row = vm.Rows.Single(r => r.Name == "other");
        await File.WriteAllTextAsync(AppConfig.GetConfigPath(Config.Root), "{ not json");

        await vm.SignInCommand.Execute(row).ToTask();

        await Assert.That(opened).IsFalse();
        await Assert.That(vm.Message).IsEqualTo("Could not read the profile configuration.");
    });

    /// A row that already holds a credential for its server offers "Sign in again": beside a
    /// "Signed in" status a bare "Sign in" reads as a contradiction, not as re-authentication.
    [Test]
    public Task Sign_in_reads_as_again_on_a_row_that_holds_a_credential() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed());
        await vm.RefreshAsync();

        var rows = vm.Rows.ToDictionary(r => r.Name);
        await Assert.That(rows["work"].SignInLabel).IsEqualTo("Sign in again");
        await Assert.That(rows["stale"].SignInLabel).IsEqualTo("Sign in again");
        await Assert.That(rows["other"].SignInLabel).IsEqualTo("Sign in");
        await Assert.That(rows["moved"].SignInLabel).IsEqualTo("Sign in");
        await Assert.That(rows["bad"].SignInLabel).IsEqualTo("Sign in");
    });
}
