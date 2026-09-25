using Capacitor.Cli.Core.Telemetry;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.App.Tests.Unit;

/// The steady-state re-auth graph is the wizard's sign-in step alone, pinned to the server the
/// profile already targets — a re-auth must never repoint server_url at a different origin.
public class ReauthCompositionTests {
    const string ServerUrl = "https://acme.example";

    static AuthResult.Committed Committed() =>
        new("default", ServerUrl, AuthProvider.GitHubApp, "alice", []);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sign_in_runs_a_paste_intent_pinned_to_the_configured_server() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();
            ConnectIntent? seen = null;

            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient(), TimeProvider.System), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory(), TimeProvider.System),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), new(new HttpClient()), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults, TimeProvider.System),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                TimeProvider.System,
                _ => (intent, _) => {
                    seen = intent;
                    return Task.FromResult<AuthResult>(Committed());
                },
                refreshAppState: true);

            await graph.SignIn.SignInCommand.Execute().ToTask();

            await Assert.That(seen).IsEqualTo(new ConnectIntent.Paste(ServerUrl));
            await Assert.That(graph.SignIn.Satisfied).IsTrue();
            // The dialog's own promise: it refreshes the app and closes, which the wizard does not.
            await Assert.That(graph.SignIn.StatusDetail).IsEqualTo("You're signed in. Refreshing…");
        });
    }

    /// A dialog for a profile the app does not run on closes without a refresh, so its success
    /// line must not promise one.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_sign_in_that_does_not_refresh_the_app_promises_no_refresh() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();

            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient(), TimeProvider.System), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory(), TimeProvider.System),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), new(new HttpClient()), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults, TimeProvider.System),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                TimeProvider.System,
                _ => (_, _) => Task.FromResult<AuthResult>(Committed()),
                refreshAppState: false);

            await graph.SignIn.SignInCommand.Execute().ToTask();

            await Assert.That(graph.SignIn.Satisfied).IsTrue();
            await Assert.That(graph.SignIn.StatusDetail).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Closing_mid_attempt_cancels_it_and_quiesces() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient(), TimeProvider.System), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory(), TimeProvider.System),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), new(new HttpClient()), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults, TimeProvider.System),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                TimeProvider.System,
                _ => async (_, ct) => {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return Committed();
                },
                refreshAppState: true);

            var run = graph.SignIn.SignInCommand.Execute().ToTask();
            await started.Task;

            await graph.CloseAsync(CancellationToken.None);
            await run;

            await Assert.That(graph.SignIn.Satisfied).IsFalse();
            await graph.Auth.QuiescedAsync(); // completes only once no attempt is live
        });
    }

    /// The precondition is an optional null-defaulted parameter at every hop between here and
    /// LoginAsync, so a dropped one refuses nothing and no other test notices.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_commit_precondition_reaches_the_facade_spec() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();
            CommitPrecondition? captured = null;

            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient(), TimeProvider.System), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory(), TimeProvider.System),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), new(new HttpClient()), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults, TimeProvider.System),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                TimeProvider.System,
                spec => {
                    captured = spec.Precondition;
                    return (_, _) => Task.FromResult<AuthResult>(new AuthResult.Cancelled());
                },
                refreshAppState: true,
                new CommitPrecondition.ExpectServer(ServerUrl));

            await graph.SignIn.SignInCommand.Execute().ToTask();

            await Assert.That(captured).IsTypeOf<CommitPrecondition.ExpectServer>();
            await Assert.That(((CommitPrecondition.ExpectServer)captured!).Url).IsEqualTo(ServerUrl);
        });
    }

    /// The dialog renders the SAME sign-in view the wizard shows, and entering it announces the
    /// pinned server — the user must see where they are signing in to.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_dialog_hosts_the_wizard_sign_in_view_announcing_the_server() {
        var (buttonFound, status) = await AvaloniaSession.DispatchAsync(() => {
            using var config = new TempConfigRoot();
            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient(), TimeProvider.System), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory(), TimeProvider.System),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), new(new HttpClient()), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults, TimeProvider.System),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                TimeProvider.System,
                _ => (_, _) => Task.FromResult<AuthResult>(Committed()),
                refreshAppState: true);

            var window = new SignInWindow { DataContext = graph.SignIn };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var button = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SignInButton");
            var text = window.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "SignInStatusText")?.Text;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            return (button is not null, text);
        });

        await Assert.That(buttonFound).IsTrue();
        await Assert.That(status).IsEqualTo(ServerUrl);
    }
}
