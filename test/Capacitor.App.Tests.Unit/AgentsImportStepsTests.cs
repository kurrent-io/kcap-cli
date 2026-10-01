using System.Reactive.Threading.Tasks;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Shared vendor-detection fixture builder for the Agents/Import step tests below.
static class VendorDetection {
    public static IReadOnlySet<HarnessId> Build(params string[] detectedVendorIds) =>
        detectedVendorIds.Select(v => HarnessId.From(v)!.Value).ToHashSet();
}

/// The app's detection feed overrides the process PATH with the login-shell terminal PATH when the
/// probe resolves one. Pure static helper — no AvaloniaSession needed.
public class AgentDetectionFeedTests {
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Uses_the_probed_terminal_PATH_not_the_process_PATH() {
        Skip.When(OperatingSystem.IsWindows(), "chmod-based executable probe is POSIX-only.");

        using var tmp = new TempDir();
        var emptyDir  = tmp.CreateDir("empty");
        var claudeDir = tmp.CreateDir("claude");
        var claudeBin = claudeDir.PathTo("claude");
        await File.WriteAllTextAsync(claudeBin, "#!/bin/sh\n");
        File.SetUnixFileMode(claudeBin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Same probe, two different crafted PATHs: the outcome must track the probe, not
        // whatever the real process PATH happens to contain on this machine.
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>(emptyDir) };
        var withoutClaude = await HarnessesStepViewModel.BuildDetectionFeed(probe, Home)(CancellationToken.None);

        probe.TerminalPathBehavior = _ => Task.FromResult<string?>(claudeDir);
        var withClaude = await HarnessesStepViewModel.BuildDetectionFeed(probe, Home)(CancellationToken.None);

        await Assert.That(withoutClaude.ContainsKey(HarnessId.Claude)).IsFalse();
        await Assert.That(withClaude[HarnessId.Claude].BinaryFound).IsTrue();
    }

    [Test]
    public async Task Falls_back_to_the_process_PATH_when_the_probe_is_inconclusive() {
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>(null) };

        var actual = await HarnessesStepViewModel.BuildDetectionFeed(probe, Home)(CancellationToken.None);

        // Pins the process-PATH fallback, so expected must come from the same resolution
        // BuildDetectionFeed falls back to, not a hermetic registry.
        var harnesses = HarnessRegistry.FromEnvironment(Home);
        var expected  = harnesses.Where(h => harnesses.Detected(h.Id)).Select(h => h.Id);

        await Assert.That(actual.Keys).IsEquivalentTo(expected);
    }
}

public class ImportStepViewModelTests {
    sealed class Harness {
        public readonly FakeKcapCli Cli = new();
        public readonly IReadOnlySet<HarnessId> Detected;
        public readonly ImportStepViewModel Vm;

        public Harness(IReadOnlySet<HarnessId>? detected = null) {
            Detected = detected ?? VendorDetection.Build();
            Vm = new ImportStepViewModel(Cli, ct => Task.FromResult(Detected), action => action());
        }

        public ImportVendorRow Row(string label) => Vm.Vendors.First(r => r.Label == label);
    }

    static bool CanExecute<TParam, TResult>(ReactiveUI.Reactive.ReactiveCommand<TParam, TResult> command) {
        var value = false;
        using var subscription = command.CanExecute.Subscribe(v => value = v); // replayed on subscribe
        return value;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OnEnterAsync_pre_checks_detected_vendors_only() {
        var (claude, codex) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(VendorDetection.Build("claude"));
            await h.Vm.OnEnterAsync(CancellationToken.None);

            return (h.Row("Claude Code").IsSelected, h.Row("Codex").IsSelected);
        });

        await Assert.That(claude).IsTrue();
        await Assert.That(codex).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Run_is_disabled_until_the_scoped_text_field_is_filled_in() {
        var (everythingOk, orgBlank, orgFilled, repoBlank, repoFilled) = await AvaloniaSession.DispatchAsync(() => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true; // isolates the scope gate from the vendor-selection gate

            var everythingOk = CanExecute(h.Vm.RunCommand); // default scope

            h.Vm.Scope = ImportScopeChoice.Org;
            var orgBlank = CanExecute(h.Vm.RunCommand);
            h.Vm.OrgText = "acme";
            var orgFilled = CanExecute(h.Vm.RunCommand);

            h.Vm.Scope = ImportScopeChoice.Repo;
            var repoBlank = CanExecute(h.Vm.RunCommand);
            h.Vm.RepoText = "acme/widgets";
            var repoFilled = CanExecute(h.Vm.RunCommand);

            return (everythingOk, orgBlank, orgFilled, repoBlank, repoFilled);
        });

        await Assert.That(everythingOk).IsTrue();
        await Assert.That(orgBlank).IsFalse();
        await Assert.That(orgFilled).IsTrue();
        await Assert.That(repoBlank).IsFalse();
        await Assert.That(repoFilled).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Run_is_disabled_when_no_vendor_is_selected() {
        var (noneSelected, oneSelected) = await AvaloniaSession.DispatchAsync(() => {
            var h = new Harness();

            var noneSelected = CanExecute(h.Vm.RunCommand);
            h.Row("Claude Code").IsSelected = true;
            var oneSelected = CanExecute(h.Vm.RunCommand);

            return (noneSelected, oneSelected);
        });

        // Empty VendorFlags means "import everything" to the CLI — the opposite of unchecking every box.
        await Assert.That(noneSelected).IsFalse();
        await Assert.That(oneSelected).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_direct_run_with_no_vendor_selected_never_calls_ImportAsync() {
        var callCount = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(); // valid scope (Everything) and a real CLI, but zero vendors selected

            await h.Vm.RunAsync();

            return h.Cli.ImportCallCount;
        });

        await Assert.That(callCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_direct_run_with_a_blank_scope_field_never_calls_ImportAsync() {
        var callCount = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Vm.Scope = ImportScopeChoice.Org; // OrgText left blank

            await h.Vm.RunAsync();

            return h.Cli.ImportCallCount;
        });

        await Assert.That(callCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_thrown_exception_sets_a_visible_status_with_the_retry_hint() {
        var status = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Cli.ImportBehavior = (_, _, _) => throw new InvalidOperationException("spawn failed");

            await h.Vm.RunCommand.Execute().ToTask();

            return h.Vm.Status;
        });

        await Assert.That(status).IsNotNull();
        await Assert.That(status).Contains("spawn failed");
        await Assert.That(status).Contains(ImportStepViewModel.RetryHint);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Everything_scope_argv_carries_only_the_selected_vendor_flags() {
        var request = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Row("Codex").IsSelected       = true;

            await h.Vm.RunCommand.Execute().ToTask();

            return h.Cli.ImportRequests.Single();
        });

        await Assert.That(request.Scope).IsEqualTo(ImportScopeChoice.Everything);
        await Assert.That(request.OrgOrRepo).IsNull();
        await Assert.That(request.VendorFlags).IsEquivalentTo(["--claude", "--codex"], CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Org_scope_argv_carries_the_org_text() {
        var request = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Vm.Scope   = ImportScopeChoice.Org;
            h.Vm.OrgText = "acme";

            await h.Vm.RunCommand.Execute().ToTask();

            return h.Cli.ImportRequests.Single();
        });

        await Assert.That(request.Scope).IsEqualTo(ImportScopeChoice.Org);
        await Assert.That(request.OrgOrRepo).IsEqualTo("acme");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repo_scope_argv_carries_the_owner_slash_name_text() {
        var request = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Vm.Scope    = ImportScopeChoice.Repo;
            h.Vm.RepoText = "acme/widgets";

            await h.Vm.RunCommand.Execute().ToTask();

            return h.Cli.ImportRequests.Single();
        });

        await Assert.That(request.Scope).IsEqualTo(ImportScopeChoice.Repo);
        await Assert.That(request.OrgOrRepo).IsEqualTo("acme/widgets");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Log_is_bounded_to_500_lines_with_a_drop_notice_once_truncation_starts() {
        var (count, truncated, first, last) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Cli.ImportBehavior = (_, onLine, _) => {
                for (var i = 1; i <= 510; i++) onLine(new StreamedLine(ProcessStreamKind.Stdout, $"line {i}"));

                return Task.FromResult(new StreamingResult(0, false, []));
            };

            await h.Vm.RunCommand.Execute().ToTask();

            return (h.Vm.Log.Count, h.Vm.Truncated, h.Vm.Log[0], h.Vm.Log[^1]);
        });

        await Assert.That(count).IsEqualTo(500);
        await Assert.That(truncated).IsTrue();
        await Assert.That(first).IsEqualTo("line 11"); // the first 10 of 510 fell off the bounded tail
        await Assert.That(last).IsEqualTo("line 510");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Completion_appends_the_retry_hint_on_success() {
        var status = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(0, false, []));

            await h.Vm.RunCommand.Execute().ToTask();

            return h.Vm.Status;
        });

        await Assert.That(status).Contains(ImportStepViewModel.RetryHint);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Completion_appends_the_retry_hint_on_failure_too() {
        var status = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(1, false, []));

            await h.Vm.RunCommand.Execute().ToTask();

            return h.Vm.Status;
        });

        await Assert.That(status).Contains(ImportStepViewModel.RetryHint);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_run_never_blocks_leaving() {
        var canLeave = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(1, false, []));
            await h.Vm.RunCommand.Execute().ToTask();

            return await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        });

        await Assert.That(canLeave).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cancel_button_stops_the_run_and_reports_cancellation_without_the_retry_hint() {
        var status = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            var started = new TaskCompletionSource();
            h.Cli.ImportBehavior = async (_, _, ct) => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);

                return new StreamingResult(0, false, []);
            };

            var run = h.Vm.RunAsync();
            await started.Task;
            await h.Vm.CancelCommand.Execute().ToTask();
            await run;

            return h.Vm.Status;
        });

        await Assert.That(status).IsEqualTo("Import cancelled.");
        await Assert.That(status).DoesNotContain(ImportStepViewModel.RetryHint);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task CanLeaveAsync_kills_a_running_import_and_never_vetoes() {
        var (canLeave, status) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            var started = new TaskCompletionSource();
            h.Cli.ImportBehavior = async (_, _, ct) => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);

                return new StreamingResult(0, false, []);
            };

            var run = h.Vm.RunAsync();
            await started.Task;

            var canLeave = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            await run;

            return (canLeave, h.Vm.Status);
        });

        await Assert.That(canLeave).IsTrue();
        await Assert.That(status).IsEqualTo("Import cancelled.");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task No_CLI_shows_the_message_and_never_calls_import() {
        var (message, cliAvailable, callCount) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Cli.CliPath = null;

            await h.Vm.RunAsync();

            return (h.Vm.Message, h.Vm.CliAvailable, h.Cli.ImportCallCount);
        });

        await Assert.That(message).IsEqualTo("kcap CLI not found");
        await Assert.That(cliAvailable).IsFalse();
        await Assert.That(callCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Satisfied_only_after_a_run_completes_with_exit_zero() {
        var (afterFailure, afterSuccess) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(1, false, []));
            await h.Vm.RunCommand.Execute().ToTask();
            var afterFailure = h.Vm.Satisfied;

            h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(0, false, []));
            await h.Vm.RunCommand.Execute().ToTask();
            var afterSuccess = h.Vm.Satisfied;

            return (afterFailure, afterSuccess);
        });

        await Assert.That(afterFailure).IsFalse();
        await Assert.That(afterSuccess).IsTrue();
    }
}

/// Template smoke coverage, mirroring WizardSimpleStepsTests: named controls per template, wired
/// through the real window and a real navigation from Agents into Import.
public class ImportTemplateTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_window_selects_a_template_for_the_Import_step() {
        var result = await AvaloniaSession.DispatchAsync(async () => {
            var cli    = new FakeKcapCli();
            var detect = new Func<CancellationToken, Task<IReadOnlySet<HarnessId>>>(_ => Task.FromResult(VendorDetection.Build("claude")));

            var import = new ImportStepViewModel(cli, detect, action => action());
            var done   = new DoneStepViewModel(() => []);

            var vm = new OnboardingViewModel([import, done]);
            await vm.PendingEnterForTesting;

            var window = new MainWindow { Onboarding = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var runButton        = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "RunImportButton");
            var everythingChoice = window.GetVisualDescendants().OfType<RadioButton>().FirstOrDefault(r => r.Name == "EverythingChoice");
            var everythingWasChecked = everythingChoice?.IsChecked;
            var orgChoice        = window.GetVisualDescendants().OfType<RadioButton>().FirstOrDefault(r => r.Name == "OrgChoice");
            var orgBox           = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "OrgTextBox");
            var vendorCheckBoxes = window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Name == "ImportVendorCheckBox").ToList();
            var stepScroll       = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "StepScroll");
            var orgHidden        = orgBox?.IsVisible;

            if (orgChoice is not null) orgChoice.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            var orgShown = orgBox?.IsVisible;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (runButton, everythingChoice, everythingWasChecked,
                ImportRows: vendorCheckBoxes.Count, stepScroll, orgHidden, orgShown);
        });

        await Assert.That(result.runButton).IsNotNull();
        await Assert.That(result.everythingChoice).IsNotNull();
        await Assert.That(result.everythingWasChecked).IsTrue();
        await Assert.That(result.ImportRows).IsEqualTo(9);
        await Assert.That(result.stepScroll).IsNotNull();
        await Assert.That(result.orgHidden).IsFalse();
        await Assert.That(result.orgShown).IsTrue();
    }
}
