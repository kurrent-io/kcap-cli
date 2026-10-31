using Capacitor.Cli.Core.Telemetry;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// Sign-in runs one façade operation. The picker and progress sink are the real bridges; the
/// service is driven by a scripted operation (the façade itself is covered in Core).
public class SignInStepViewModelTests {
    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    static async Task WaitUntil(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow + Bounded;

        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for {what}");

            await Task.Delay(5);
        }
    }

    static DiscoveredTenant Tenant(string login) => new() { OrgLogin = login, Origin = $"https://{login}.kcap.ai" };

    static AuthResult.Committed Committed(string provider = AuthProvider.GitHubApp, string? username = "sam") =>
        new("acme", "https://acme.kcap.ai:443", provider, username, [new AuthIdentity("acme", "https://acme.kcap.ai:443")]);

    sealed class FakeAppState : IAppStateStore {
        public AppState State = new();
        public int Updates;

        public Task<AppState> LoadAsync() => Task.FromResult(State);

        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
            Updates++;
            State = mutate(State);

            return Task.FromResult(true);
        }
    }

    sealed class Harness : IDisposable {
        readonly TempConfigRoot                 _config = new();
        public readonly ConnectChoiceViewModel    Connect = new();
        public readonly WizardTenantPicker      Picker;
        public readonly RecordingOpener         Opener  = new();
        public readonly FakeAppState            AppState = new();
        public readonly FakeTimeProvider        Time    = new();
        public readonly ConsentFlipClaims       Claims;
        public readonly UiAuthProgress          Progress;
        public readonly WizardAuthService       Service;
        public readonly SignInStepViewModel     Vm;

        public int Runs;

        // The edition screen's cloud choice, read the way the composition reads it.
        public bool SigningUp;

        public Func<ConnectIntent, CancellationToken, Task<AuthResult>> Operation =
            (_, _) => Task.FromResult<AuthResult>(Committed());

        public Harness() {
            Claims = new ConsentFlipClaims(_config.Root);

            var bridges = new WizardBridges(action => action(), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults);

            Picker   = bridges.Picker;
            Progress = bridges.Progress;
            Service = new WizardAuthService((intent, ct) => {
                Runs++;

                return Operation(intent, ct);
            });
            Vm = new SignInStepViewModel(
                Service, Connect, bridges, Claims, AppState, Opener, time: Time, signingUp: () => SigningUp);
        }

        public string ClaimsPath => _config.PathTo("consent-flip-claims.json");

        public Task<System.Reactive.Unit> SignIn() => Vm.SignInCommand.Execute().ToTask();

        public void Dispose() => _config.Dispose();
    }

    // ── committed outcomes ───────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(AuthProvider.GitHubApp)]
    [Arguments(AuthProvider.WorkOS)]
    public async Task A_committed_discovery_satisfies_the_step_and_names_the_signed_in_user(string provider) {
        var (satisfied, status, isError, detail, showPrimary) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            h.Operation = (_, _) => Task.FromResult<AuthResult>(Committed(provider));

            await h.Vm.OnEnterAsync(CancellationToken.None);
            await h.SignIn();

            return (h.Vm.Satisfied, h.Vm.Status, h.Vm.StatusIsError, h.Vm.StatusDetail, h.Vm.ShowPrimaryAction);
        });

        await Assert.That(satisfied).IsTrue();
        await Assert.That(status).IsEqualTo("Signed in as sam");
        await Assert.That(isError).IsFalse();
        await Assert.That(detail).IsNull(); // what happens next is the host's to say
        await Assert.That(showPrimary).IsFalse();
    }

    /// Completed fires only for a committed sign-in. A cancel or a failure still has this page to answer.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Only_a_committed_sign_in_announces_completion() {
        var (committed, cancelled, failed) = await AvaloniaSession.DispatchAsync(async () => {
            async Task<int> CompletionsFor(AuthResult result) {
                using var h = new Harness();
                h.Connect.Choice = ConnectChoice.Discover;
                h.Operation = (_, _) => Task.FromResult(result);
                var raised = 0;
                h.Vm.Completed += () => raised++;

                await h.SignIn();

                return raised;
            }

            return (
                await CompletionsFor(Committed()),
                await CompletionsFor(new AuthResult.Cancelled()),
                await CompletionsFor(new AuthResult.Failed("nope")));
        });

        await Assert.That(committed).IsEqualTo(1);
        await Assert.That(cancelled).IsEqualTo(0);
        await Assert.That(failed).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_none_provider_join_auto_satisfies_with_the_no_sign_in_copy() {
        var (satisfied, status, intent) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Paste;
            h.Connect.ServerInputText = "http://localhost:5108";
            ConnectIntent? seen = null;
            h.Operation = (i, _) => {
                seen = i;

                return Task.FromResult<AuthResult>(Committed(AuthProvider.None, username: null));
            };

            await h.SignIn();

            return (h.Vm.Satisfied, h.Vm.Status, seen);
        });

        await Assert.That(satisfied).IsTrue();
        await Assert.That(status).IsEqualTo("No sign-in required for this server.");
        await Assert.That(intent).IsEqualTo(new ConnectIntent.Paste("http://localhost:5108"));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Re_entry_after_a_commit_still_shows_the_step_satisfied() {
        var (satisfied, status, runs) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;

            await h.SignIn();
            await h.Vm.OnEnterAsync(CancellationToken.None); // Back then forward again

            return (h.Vm.Satisfied, h.Vm.Status, h.Runs);
        });

        await Assert.That(satisfied).IsTrue();
        await Assert.That(status).IsEqualTo("Signed in as sam");
        await Assert.That(runs).IsEqualTo(1); // entering the step starts nothing
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_commit_whose_credential_was_not_saved_does_not_satisfy_the_step() {
        var (satisfied, isError, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Prefill("https://acme.example");
            h.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Committed("acme", "https://acme.example:443", AuthProvider.GitHubApp, "sam", [], CredentialSaved: false));

            await h.Vm.OnEnterAsync(CancellationToken.None);
            await h.SignIn();

            return (h.Vm.Satisfied, h.Vm.StatusIsError, h.Vm.Status);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(isError).IsTrue();
        await Assert.That(status).IsEqualTo("Signed in, but the credential could not be saved.");
    }

    // ── cancellation and failure ─────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cancelling_is_not_a_failure_and_leaves_the_step_retryable() {
        var (satisfied, status, isError, detail) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            h.Operation = (_, _) => Task.FromResult<AuthResult>(new AuthResult.Cancelled());

            await h.SignIn();

            return (h.Vm.Satisfied, h.Vm.Status, h.Vm.StatusIsError, h.Vm.StatusDetail);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(status).IsEqualTo("Sign-in cancelled.");
        await Assert.That(isError).IsFalse();
        await Assert.That(detail).IsNull();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failure_shows_one_generic_headline_and_never_re_logs_the_facade_message() {
        var (status, isError, detail, log, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            h.Operation = (_, _) => {
                h.Progress.Error("Error: the auth service is unreachable.");

                return Task.FromResult<AuthResult>(new AuthResult.Failed("the auth service is unreachable"));
            };

            await h.SignIn();

            return (h.Vm.Status, h.Vm.StatusIsError, h.Vm.StatusDetail, h.Vm.Log.ToList(), h.Vm.Satisfied);
        });

        await Assert.That(status).IsEqualTo("Sign-in failed.");
        await Assert.That(isError).IsTrue();
        await Assert.That(satisfied).IsFalse();
        await Assert.That(detail).IsEqualTo("The auth service is unreachable.");
        await Assert.That(log).IsEmpty();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unexpected_operation_throw_lands_as_a_failure_not_a_crash() {
        var (status, isError) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            h.Operation = (_, _) => throw new InvalidOperationException("claim_arm_failed");

            await h.SignIn();

            return (h.Vm.Status, h.Vm.StatusIsError);
        });

        await Assert.That(status).IsEqualTo("Sign-in failed.");
        await Assert.That(isError).IsTrue();
    }

    // ── progress rendering ───────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_device_code_renders_without_the_clipboard_note_and_the_raw_line_stays_in_the_log() {
        var (code, uri, log) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            var gate = new TaskCompletionSource<AuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Operation = (_, _) => {
                h.Progress.DeviceCode("ABCD-1234  (copied to clipboard)", "https://github.com/login/device", "GitHub", prefilled: false);

                return gate.Task;
            };

            var exec = h.SignIn();
            // Read while the flow is live: a settled attempt clears the code it no longer polls.
            var rendered = (h.Vm.DeviceCode, h.Vm.VerificationUri, h.Vm.Log.ToList());

            gate.SetResult(Committed());
            await exec;

            return rendered;
        });

        await Assert.That(code).IsEqualTo("ABCD-1234");
        await Assert.That(uri).IsEqualTo("https://github.com/login/device"); // the clickable link is the device URI
        await Assert.That(string.Join("\n", log)).Contains("(copied to clipboard)");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_browser_fallback_url_renders_and_opens_through_the_url_opener() {
        var (fallback, opened) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            var gate = new TaskCompletionSource<AuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Operation = (_, _) => {
                h.Progress.BrowserOpening("https://auth.example/authorize?x=1");

                return gate.Task;
            };

            var exec = h.SignIn();
            var url  = h.Vm.BrowserUrl; // the fallback link only exists while the browser wait is live
            await h.Vm.OpenSignInUrlCommand.Execute().ToTask();

            gate.SetResult(Committed());
            await exec;

            return (url, h.Opener.Opened.ToList());
        });

        await Assert.That(fallback).IsEqualTo("https://auth.example/authorize?x=1");
        await Assert.That(opened).IsEquivalentTo(["https://auth.example/authorize?x=1"]);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_progress_log_stays_bounded() {
        var (count, last) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();

            for (var i = 0; i < SignInStepViewModel.LogLimit + 50; i++) h.Progress.Notice($"line {i}");

            await Task.Yield();

            return (h.Vm.Log.Count, h.Vm.Log[^1]);
        });

        await Assert.That(count).IsEqualTo(SignInStepViewModel.LogLimit);
        await Assert.That(last).IsEqualTo($"line {SignInStepViewModel.LogLimit + 49}");
    }

    // ── tenant picker ────────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Confirming_a_tenant_resolves_the_awaited_pick() {
        var (offered, satisfied, status, stillVisible) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            h.Operation = async (_, ct) => {
                var picked = await h.Picker.PickAsync([Tenant("acme"), Tenant("globex")], TenantPickContext.None, ct);

                return picked is null
                    ? new AuthResult.Failed("No tenant selected.")
                    : Committed(username: picked.OrgLogin);
            };

            var exec = h.SignIn();
            await WaitUntil(() => h.Vm.TenantPickerVisible, "the tenant list");
            var listed = h.Vm.Tenants.Select(t => t.OrgLogin).ToList();

            h.Vm.SelectedTenant = h.Vm.Tenants[1];
            await h.Vm.ConfirmTenantCommand.Execute().ToTask();
            await exec;

            return (listed, h.Vm.Satisfied, h.Vm.Status, h.Vm.TenantPickerVisible);
        });

        await Assert.That(offered).IsEquivalentTo(["acme", "globex"]);
        await Assert.That(satisfied).IsTrue();
        await Assert.That(status).IsEqualTo("Signed in as globex");
        await Assert.That(stillVisible).IsTrue();
    }

    /// The list stays after Continue. A dedicated success page would replace the choice.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_workspace_list_stays_until_sign_in_replaces_it() {
        var (heldList, heldStart, heldPending, heldPhase, phase, startAfter, listAfter) =
            await AvaloniaSession.DispatchAsync(async () => {
                using var h = new Harness();
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                h.Connect.Choice = ConnectChoice.Discover;
                h.Operation = async (_, ct) => {
                    var picked = await h.Picker.PickAsync([Tenant("acme"), Tenant("globex")], TenantPickContext.None, ct);
                    await release.Task.WaitAsync(ct);

                    return Committed(username: picked!.OrgLogin);
                };

                var exec = h.SignIn();
                await WaitUntil(() => h.Vm.TenantPickerVisible, "the tenant list");
                await h.Vm.ConfirmTenantCommand.Execute().ToTask();

                var during = (h.Vm.TenantPickerVisible, h.Vm.StartPanelVisible, h.Vm.TenantChoicePending, h.Vm.Phase);
                release.TrySetResult();
                await exec;

                return (during.Item1, during.Item2, during.Item3, during.Item4,
                    h.Vm.Phase, h.Vm.StartPanelVisible, h.Vm.TenantPickerVisible);
            });

        await Assert.That(heldList).IsTrue();
        await Assert.That(heldStart).IsFalse();
        await Assert.That(heldPending).IsFalse();
        await Assert.That(heldPhase).IsEqualTo(SignInPhase.PickWorkspace);
        await Assert.That(phase).IsEqualTo(SignInPhase.PickWorkspace);
        await Assert.That(startAfter).IsFalse();
        await Assert.That(listAfter).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cancelling_the_tenant_list_restores_a_retryable_sign_in_without_an_error() {
        var (satisfied, status, retry, picker, error) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            h.Operation = async (_, ct) => {
                var picked = await h.Picker.PickAsync([Tenant("acme"), Tenant("globex")], TenantPickContext.None, ct);

                return picked is null ? new AuthResult.Failed("No tenant selected.") : Committed();
            };

            var exec = h.SignIn();
            await WaitUntil(() => h.Vm.TenantPickerVisible, "the tenant list");

            await h.Vm.CancelTenantCommand.Execute().ToTask();
            await exec;

            return (h.Vm.Satisfied, h.Vm.Status, h.Vm.ShowPrimaryAction, h.Vm.TenantPickerVisible, h.Vm.StatusIsError);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(status).IsEqualTo("Sign-in cancelled.");
        await Assert.That(retry).IsTrue();
        await Assert.That(picker).IsFalse();
        await Assert.That(error).IsFalse();
    }

    /// The page's headline follows the operation: the start, then one workspace list.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_headline_follows_the_sign_in_phase() {
        var (start, picker) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            var startTitle = h.Vm.Title;
            var pick = new TaskCompletionSource<string>();
            h.Operation = async (_, ct) => {
                var picking = h.Picker.PickAsync([new DiscoveredTenant { Origin = "https://acme.kcap.ai" }, new DiscoveredTenant { Origin = "https://beta.kcap.ai" }],
                    TenantPickContext.None, ct);
                await WaitUntil(() => h.Vm.TenantPickerVisible, "the picker");
                pick.SetResult(h.Vm.Title);
                h.Picker.Select(null);
                await picking;

                return new AuthResult.Cancelled();
            };

            var exec = h.SignIn();
            var pickerTitle = await pick.Task.WaitAsync(Bounded);
            await exec;

            return (startTitle, pickerTitle);
        });

        await Assert.That(start).IsEqualTo("Sign in to kcap");
        await Assert.That(picker).IsEqualTo("Choose a workspace");
    }

    // ── cloud sign-up in the browser ─────────────────────────────────────────

    // The delay's continuation runs off Advance's own call, so a "nothing fired" check waits this long first.
    static readonly TimeSpan SettleGap = TimeSpan.FromMilliseconds(100);

    const string DesktopSignUp = AuthEndpoints.DefaultSignupUrl + "/get-started?tier=team&via=desktop";

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Entering_on_the_cloud_path_opens_the_desktop_sign_up_and_waits_on_the_browser() {
        var (opened, phase, title, satisfied, runs) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };

            await h.Vm.OnEnterAsync(CancellationToken.None);

            return (h.Opener.Opened.ToList(), h.Vm.Phase, h.Vm.Title, h.Vm.Satisfied, h.Runs);
        });

        await Assert.That(opened).IsEquivalentTo([DesktopSignUp]);
        await Assert.That(phase).IsEqualTo(SignInPhase.SignUp);
        await Assert.That(title).IsEqualTo("Finish in your browser, then continue here");
        await Assert.That(satisfied).IsFalse();
        await Assert.That(runs).IsEqualTo(0); // nothing is discovered until the user says the browser is done
    }

    /// The sign-up page is served from the sign-up host, not a workspace's, and the browser may not
    /// have appeared yet when the page is read: the copy names the page, not a host, and not a past event.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_sign_up_copy_names_the_page_rather_than_a_host() {
        var lede = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };

            await h.Vm.OnEnterAsync(CancellationToken.None);

            return h.Vm.Lede;
        });

        string[] lines = [lede!, SignInStepViewModel.WorkspaceNotCreated, SignInStepViewModel.WorkspaceNotCreatedDetail("Acme"),
            SignInStepViewModel.WorkspaceStillSettingUpDetail, SignInStepViewModel.WorkspaceSettingUpDetail("Acme")];
        foreach (var line in lines) await Assert.That(line).DoesNotContain("kcap.ai");
        await Assert.That(lede).Contains("sign-up page");
        await Assert.That(lede).DoesNotContain("just opened");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_existing_workspace_path_takes_a_single_workspace_without_confirming() {
        var intent = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            ConnectIntent? seen = null;
            h.Operation = (i, _) => {
                seen = i;
                return Task.FromResult<AuthResult>(Committed(AuthProvider.WorkOS));
            };

            await h.Vm.ContinueWithWorkAccountCommand.Execute().ToTask();

            return seen;
        });

        await Assert.That(intent).IsEqualTo(new ConnectIntent.Discover());
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_existing_workspace_path_opens_no_sign_up() {
        var (opened, phase) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();

            await h.Vm.OnEnterAsync(CancellationToken.None);

            return (h.Opener.Opened.ToList(), h.Vm.Phase);
        });

        await Assert.That(opened).IsEmpty();
        await Assert.That(phase).IsEqualTo(SignInPhase.Start);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Continue_after_the_browser_runs_discovery_and_commits() {
        var (intent, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };
            ConnectIntent? seen = null;
            h.Operation = (i, _) => {
                seen = i;
                return Task.FromResult<AuthResult>(Committed(AuthProvider.WorkOS));
            };
            await h.Vm.OnEnterAsync(CancellationToken.None);

            await h.Vm.ContinueAfterSignUpCommand.Execute().ToTask();

            return (seen, h.Vm.Satisfied);
        });

        // The new workspace is not listed until it is live, so the one found must be confirmed, not taken.
        await Assert.That(intent).IsEqualTo(new ConnectIntent.Discover(ConfirmPick: true));
        await Assert.That(satisfied).IsTrue();
    }

    /// Provisioning takes minutes, so a Continue that finds no workspace yet is the wait going on,
    /// not a failed sign-in.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_continue_that_finds_no_workspace_yet_keeps_waiting_without_an_error() {
        var (phase, status, isError) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };
            h.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Failed("No Capacitor tenants are linked to your account.", AuthFailureReason.NoTenantsFound));
            await h.Vm.OnEnterAsync(CancellationToken.None);

            await h.Vm.ContinueAfterSignUpCommand.Execute().ToTask();

            return (h.Vm.Phase, h.Vm.Status, h.Vm.StatusIsError);
        });

        await Assert.That(phase).IsEqualTo(SignInPhase.SignUp);
        await Assert.That(status).IsEqualTo(SignInStepViewModel.WorkspaceNotReady);
        await Assert.That(isError).IsFalse();
    }

    /// A workspace kcap.ai is still creating is waited on inside the sign-in, saying so, never as a failure.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_workspace_still_being_created_is_a_wait_that_finishes_by_itself() {
        var (waiting, status, isError, detail, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            var live = new TaskCompletionSource<AuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Connect.Choice = ConnectChoice.Discover;
            h.Operation = (_, _) => {
                h.Progress.ProvisioningWait("Acme");
                return live.Task;
            };
            await h.Vm.OnEnterAsync(CancellationToken.None);

            var run = h.SignIn();
            var during = (h.Vm.Busy, h.Vm.Status, h.Vm.StatusIsError, h.Vm.StatusDetail);
            live.SetResult(Committed(AuthProvider.WorkOS));
            await run;

            return (during.Busy, during.Status, during.StatusIsError, during.StatusDetail, h.Vm.Satisfied);
        });

        await Assert.That(waiting).IsTrue();
        await Assert.That(status).IsEqualTo(SignInStepViewModel.WorkspaceSettingUp);
        await Assert.That(isError).IsFalse();
        await Assert.That(detail).IsEqualTo(SignInStepViewModel.WorkspaceSettingUpDetail("Acme"));
        await Assert.That(satisfied).IsTrue();
    }

    /// A wait that ends before the workspace is live is still not a failed sign-in; a workspace kcap.ai
    /// gave up on is, and says so rather than that the account has none.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_provisioning_wait_ends_as_a_retry_or_as_the_failure_it_was(bool failed) {
        var (status, isError, detail) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            h.Operation = (_, _) => {
                h.Progress.ProvisioningWait("Acme");
                if (failed) h.Progress.ProvisioningFailed("Acme");
                return Task.FromResult<AuthResult>(failed
                    ? new AuthResult.Failed("No kcap tenants are linked to your account.", AuthFailureReason.NoTenantsFound)
                    : new AuthResult.Failed("Acme is still being created.", AuthFailureReason.ProvisioningInProgress));
            };
            await h.Vm.OnEnterAsync(CancellationToken.None);

            await h.SignIn();

            return (h.Vm.Status, h.Vm.StatusIsError, h.Vm.StatusDetail);
        });

        await Assert.That(status).IsEqualTo(failed ? SignInStepViewModel.WorkspaceNotCreated : SignInStepViewModel.WorkspaceSettingUp);
        await Assert.That(isError).IsEqualTo(failed);
        await Assert.That(detail).IsEqualTo(failed
            ? SignInStepViewModel.WorkspaceNotCreatedDetail("Acme")
            : SignInStepViewModel.WorkspaceStillSettingUpDetail);
    }

    /// Opening kcap.ai's sign-up is remembered, so a relaunch can open on sign-in; a sign-in clears it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_the_sign_up_is_remembered_until_a_sign_in_commits() {
        var (opened, now, afterCommit) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };
            await h.Vm.OnEnterAsync(CancellationToken.None);
            var opened = h.AppState.State.WebSignUpOpenedAt;

            await h.Vm.ContinueAfterSignUpCommand.Execute().ToTask();

            return (opened, h.Time.GetUtcNow(), h.AppState.State.WebSignUpOpenedAt);
        });

        await Assert.That(opened).IsEqualTo(now);
        await Assert.That(afterCommit).IsNull();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Reopening_the_sign_up_opens_the_same_page() {
        var opened = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };
            await h.Vm.OnEnterAsync(CancellationToken.None);

            await h.Vm.ReopenSignUpCommand.Execute().ToTask();

            return h.Opener.Opened.ToList();
        });

        await Assert.That(opened).IsEquivalentTo([DesktopSignUp, DesktopSignUp]);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_wait_that_outlives_its_limit_expires_once() {
        var (early, late) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };
            var expired = 0;
            h.Vm.SignUpExpired += () => expired++;
            await h.Vm.OnEnterAsync(CancellationToken.None);

            h.Time.Advance(SignInStepViewModel.SignUpWait - TimeSpan.FromSeconds(1));
            await Task.Delay(SettleGap);
            var early = expired;
            h.Time.Advance(TimeSpan.FromHours(2));
            await WaitUntil(() => expired > 0, "the wait to expire");
            await Task.Delay(SettleGap);

            return (early, expired);
        });

        await Assert.That(early).IsEqualTo(0);
        await Assert.That(late).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Leaving_the_wait_stops_its_clock() {
        var expired = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };
            var expired = 0;
            h.Vm.SignUpExpired += () => expired++;
            await h.Vm.OnEnterAsync(CancellationToken.None);

            await h.Vm.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None);
            h.Time.Advance(SignInStepViewModel.SignUpWait * 2);
            await Task.Delay(SettleGap);

            return expired;
        });

        await Assert.That(expired).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_refused_forward_move_keeps_the_wait_running() {
        var (left, expired) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };
            var expired = 0;
            h.Vm.SignUpExpired += () => expired++;
            await h.Vm.OnEnterAsync(CancellationToken.None);

            var left = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            h.Time.Advance(SignInStepViewModel.SignUpWait);
            await WaitUntil(() => expired > 0, "the wait to expire");

            return (left, expired);
        });

        await Assert.That(left).IsFalse();
        await Assert.That(expired).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_committed_sign_up_never_expires() {
        var expired = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };
            var expired = 0;
            h.Vm.SignUpExpired += () => expired++;
            await h.Vm.OnEnterAsync(CancellationToken.None);
            await h.Vm.ContinueAfterSignUpCommand.Execute().ToTask();

            h.Time.Advance(SignInStepViewModel.SignUpWait * 2);
            await Task.Delay(SettleGap);

            return expired;
        });

        await Assert.That(expired).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_sign_up_page_that_cannot_open_shows_its_address() {
        var (visible, isError, detail) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };
            h.Opener.ThrowOnOpen = new InvalidOperationException("no browser");

            await h.Vm.OnEnterAsync(CancellationToken.None);

            return (h.Vm.StatusLineVisible, h.Vm.StatusIsError, h.Vm.StatusDetail);
        });

        await Assert.That(visible).IsTrue();
        await Assert.That(isError).IsTrue();
        await Assert.That(detail).Contains(DesktopSignUp);
    }

    /// A Continue whose browser authorization is abandoned would otherwise hold the wait open for as
    /// long as the attempt runs.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_expired_wait_cancels_a_stalled_continue_and_expires() {
        var (expired, busy) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };
            var expired = 0;
            h.Vm.SignUpExpired += () => expired++;
            h.Operation = async (_, ct) => {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Committed();
            };
            await h.Vm.OnEnterAsync(CancellationToken.None);
            var run = h.Vm.ContinueAfterSignUpCommand.Execute().ToTask();
            await WaitUntil(() => h.Vm.Busy, "the continue to start");

            h.Time.Advance(SignInStepViewModel.SignUpWait);
            await WaitUntil(() => expired > 0, "the stalled wait to expire");
            await run.WaitAsync(Bounded);

            return (expired, h.Vm.Busy);
        });

        await Assert.That(expired).IsEqualTo(1);
        await Assert.That(busy).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_pane_shows_the_browser_wait_with_its_continue_and_fallbacks() {
        var (continueShown, workAccountShown, codeShown, urlShown) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness { SigningUp = true };
            var edition = new EditionStepViewModel(CliTelemetry.Disabled(TimeProvider.System).Funnel, () => null);
            var vm = new OnboardingViewModel([edition, h.Vm]);
            await vm.PendingEnterForTesting;

            var window = new MainWindow { Onboarding = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            await edition.ChooseCloudCommand.Execute().ToTask();
            await vm.PendingAdvanceForTesting;
            Dispatcher.UIThread.RunJobs();

            T? Named<T>(string name) where T : Control =>
                window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

            var shown = (Named<Button>("ContinueAfterSignUpButton")?.IsEffectivelyVisible,
                Named<Button>("WorkAccountButton")?.IsEffectivelyVisible,
                Named<Button>("SignUpUseCodeButton")?.IsEffectivelyVisible,
                Named<Button>("SignUpWorkspaceUrlButton")?.IsEffectivelyVisible);

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return shown;
        });

        await Assert.That(continueShown).IsTrue();
        await Assert.That(workAccountShown).IsFalse();
        // The fallbacks stay reachable: a browser that cannot hand back, or a workspace better pasted.
        await Assert.That(codeShown).IsTrue();
        await Assert.That(urlShown).IsTrue();
    }

    // ── step transitions across the boundary ─────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(WizardNavigation.Back)]
    [Arguments(WizardNavigation.Skip)]
    [Arguments(WizardNavigation.Next)]
    public async Task Leaving_before_the_boundary_cancels_the_attempt_but_cannot_advance_unsigned(WizardNavigation direction) {
        var (canLeave, satisfied, status, isError, runs) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            var started = new TaskCompletionSource();
            h.Operation = async (_, ct) => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // the pre-boundary lane, cancellable

                return Committed();
            };

            var exec = h.SignIn();
            await started.Task.WaitAsync(Bounded);

            await h.Vm.SignInAsync(); // a re-entrant run while one is live must be a silent no-op

            var allowed = await h.Vm.CanLeaveAsync(direction, CancellationToken.None);
            await exec;

            return (allowed, h.Vm.Satisfied, h.Vm.Status, h.Vm.StatusIsError, h.Runs);
        });

        await Assert.That(canLeave).IsEqualTo(direction == WizardNavigation.Back);
        await Assert.That(satisfied).IsFalse();
        await Assert.That(status).IsEqualTo("Sign-in cancelled.");
        await Assert.That(isError).IsFalse();
        await Assert.That(runs).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Leaving_after_the_boundary_waits_for_the_commit_and_is_allowed() {
        var (heldWhileCommitting, canLeave, satisfied, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            var gate = new TaskCompletionSource<AuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Operation = (_, _) => gate.Task; // past the boundary: cancellation is ignored

            var exec = h.SignIn();
            var leaving = h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            var held = !leaving.IsCompleted; // the wizard is genuinely waiting on the commit

            gate.SetResult(Committed());
            var allowed = await leaving.WaitAsync(Bounded);
            // Read before awaiting the command: the leave itself must have settled the state.
            var (satisfied, status) = (h.Vm.Satisfied, h.Vm.Status);
            await exec;

            return (held, allowed, satisfied, status);
        });

        await Assert.That(heldWhileCommitting).IsTrue();
        await Assert.That(canLeave).IsTrue();
        await Assert.That(satisfied).IsTrue();
        await Assert.That(status).IsEqualTo("Signed in as sam");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_settled_attempt_clears_the_device_code_and_browser_surfaces() {
        var (code, uri, browser, waiting, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            var started = new TaskCompletionSource();
            h.Operation = async (_, ct) => {
                h.Progress.BrowserOpening("https://auth.example/authorize");
                h.Progress.DeviceCode("ABCD-1234", "https://github.com/login/device", "GitHub", prefilled: false);
                h.Progress.PollTick();
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);

                return Committed();
            };

            var exec = h.SignIn();
            await started.Task.WaitAsync(Bounded);
            await h.Vm.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None);
            await exec;

            return (h.Vm.DeviceCode, h.Vm.VerificationUri, h.Vm.BrowserUrl, h.Vm.WaitingText, h.Vm.Status);
        });

        await Assert.That(code).IsNull(); // nothing polls a cancelled device flow
        await Assert.That(uri).IsNull();
        await Assert.That(browser).IsNull();
        await Assert.That(waiting).IsNull();
        await Assert.That(status).IsEqualTo("Sign-in cancelled.");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Signing_in_without_a_staged_intent_starts_nothing_and_opens_the_url_field() {
        var (runs, panel, error) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Paste; // nothing typed — no valid intent

            await h.Vm.OnEnterAsync(CancellationToken.None);
            await h.SignIn();

            return (h.Runs, h.Vm.UrlPanelVisible, h.Connect.InputError);
        });

        await Assert.That(runs).IsEqualTo(0);
        await Assert.That(panel).IsTrue();
        await Assert.That(error).IsEqualTo(ConnectChoiceViewModel.InvalidServerMessage);
    }

    // ── quarantine notice ────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_quarantined_claims_file_surfaces_one_dismissible_notice_and_the_ack_persists() {
        var (notice, afterAck, acked, updates) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            await File.WriteAllTextAsync(h.ClaimsPath, "{ this is not json");

            await h.SignIn();
            var shown = h.Vm.QuarantineNotice;

            await h.Vm.AcknowledgeQuarantineCommand.Execute().ToTask();

            return (shown, h.Vm.QuarantineNotice, h.AppState.State.ConsentQuarantineAcked, h.AppState.Updates);
        });

        await Assert.That(notice).IsNotNull();
        await Assert.That(notice!).Contains("consent-flip claims file");
        await Assert.That(notice).Contains(".quarantined-0.json");
        await Assert.That(afterAck).IsNull();
        await Assert.That(acked).IsTrue();
        await Assert.That(updates).IsEqualTo(1);
    }

    /// Moving on with the notice up would strand it on a page nobody is looking at.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_quarantine_notice_holds_completion_until_it_is_acknowledged() {
        var (whileShown, afterAck) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            await File.WriteAllTextAsync(h.ClaimsPath, "{ this is not json");
            var raised = 0;
            h.Vm.Completed += () => raised++;

            await h.SignIn();
            var held = raised;

            await h.Vm.AcknowledgeQuarantineCommand.Execute().ToTask();

            return (held, raised);
        });

        await Assert.That(whileShown).IsEqualTo(0);
        await Assert.That(afterAck).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_already_acknowledged_quarantine_is_never_surfaced_again() {
        var notice = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            h.AppState.State = h.AppState.State with { ConsentQuarantineAcked = true };
            await File.WriteAllTextAsync(h.ClaimsPath, "{ this is not json");

            await h.SignIn();

            return h.Vm.QuarantineNotice;
        });

        await Assert.That(notice).IsNull();
    }

    // ── templates ────────────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_pane_renders_sign_in_with_the_work_account_first() {
        var (workAccount, urlBox, urlBoxShown, statusShown, editionFooter, signInFooter) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            var edition = new EditionStepViewModel(CliTelemetry.Disabled(TimeProvider.System).Funnel, () => null);
            var vm = new OnboardingViewModel([edition, h.Vm]);
            await vm.PendingEnterForTesting;

            var window = new MainWindow { Onboarding = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var footer = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "StepFooter");
            var footerOnEdition = footer.IsVisible;

            await edition.SignInCommand.Execute().ToTask(); // Edition -> Sign in
            await vm.PendingAdvanceForTesting;
            Dispatcher.UIThread.RunJobs();

            var button = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "WorkAccountButton");
            var box = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "ServerInputBox");
            var status = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "SignInStatusText");
            var shown = box?.IsEffectivelyVisible;
            var statusVisible = status?.IsEffectivelyVisible;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (button, box, shown, statusVisible, footerOnEdition, footer.IsVisible);
        });

        await Assert.That(workAccount).IsNotNull();
        await Assert.That(urlBox).IsNotNull();
        await Assert.That(urlBox!.Classes.Contains("onboardingField")).IsTrue();
        await Assert.That(urlBoxShown).IsFalse(); // behind "I have a workspace URL"
        await Assert.That(statusShown).IsFalse(); // the ready line only restates the button
        await Assert.That(editionFooter).IsFalse();
        await Assert.That(signInFooter).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_healthy_claims_store_surfaces_no_notice() {
        var notice = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;

            await h.SignIn();

            return h.Vm.QuarantineNotice;
        });

        await Assert.That(notice).IsNull();
    }
}
