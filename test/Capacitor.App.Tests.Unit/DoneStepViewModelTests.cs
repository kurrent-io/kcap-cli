using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// The last page reads its headline and figures off what the earlier pages did.
public class DoneStepViewModelTests {
    static DoneFacts Facts(IReadOnlyList<string>? recording = null, bool hazard = false, HistoryImportRun? import = null) =>
        new(recording ?? ["Claude Code"], false, hazard, import, true, "test-mac", "https://acme.kcap.ai");

    static async Task<HistoryImportRun> FinishedRun(int imported, int failed) {
        var cli = new FakeKcapCli {
            ImportBehavior = (_, onLine, _) => {
                onLine(new StreamedLine(ProcessStreamKind.Stdout, $"  {imported} imported · 0 skipped · {failed} failed"));
                return Task.FromResult(new StreamingResult(0, false, []));
            },
        };
        var run = new HistoryImportRun(100, 2, "from the last 90 days", 1, action => action());
        run.Start(cli, [new ImportRequest(ImportScopeChoice.Repo, null, [], ["a/b"])]);
        await run.Completion;

        return run;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_headline_follows_what_actually_happened() {
        var (imported, remembering, nothing, blocked) = await AvaloniaSession.DispatchAsync(async () => {
            async Task<string> TitleFor(DoneFacts facts) {
                var vm = new DoneStepViewModel(() => facts);
                await vm.OnEnterAsync(CancellationToken.None);
                return vm.Title;
            }

            return (await TitleFor(Facts(import: await FinishedRun(40, 0))), await TitleFor(Facts()),
                await TitleFor(Facts(recording: [])), await TitleFor(Facts(hazard: true)));
        });

        await Assert.That(imported).IsEqualTo("Your work so far");
        await Assert.That(remembering).IsEqualTo("From now on, your agents remember");
        await Assert.That(nothing).IsEqualTo("No new recording connections");
        await Assert.That(blocked).IsEqualTo("No new recording connections");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_restart_line_shows_only_when_new_hooks_need_one() {
        var (remote, local, nothing, blocked) = await AvaloniaSession.DispatchAsync(async () => {
            async Task<string?> LineFor(DoneFacts facts) {
                var vm = new DoneStepViewModel(() => facts);
                await vm.OnEnterAsync(CancellationToken.None);
                return vm.RestartLine;
            }

            return (await LineFor(Facts()), await LineFor(Facts() with { Local = true }),
                await LineFor(Facts(recording: [])), await LineFor(Facts(hazard: true)));
        });

        await Assert.That(remote).Contains("Restart any agent you already have open");
        await Assert.That(local).IsEqualTo(remote);
        await Assert.That(nothing).IsNull();
        await Assert.That(blocked).IsNull();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Failed_uploads_are_named_with_where_they_still_are() {
        var (visible, title, body, landed, unit) = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new DoneStepViewModel(() => Facts(import: null));
            var run = await FinishedRun(30, 3);
            vm = new DoneStepViewModel(() => Facts(import: run));
            await vm.OnEnterAsync(CancellationToken.None);

            return (vm.FailedVisible, vm.FailedTitle, vm.FailedBody, vm.LandedValue, vm.LandedUnit);
        });

        await Assert.That(visible).IsTrue();
        await Assert.That(title).IsEqualTo("3 sessions failed to upload");
        await Assert.That(body).Contains("test-mac");
        await Assert.That(body).Contains("retry");
        await Assert.That(landed).IsEqualTo("30");
        await Assert.That(unit).IsEqualTo("/ 100");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_running_import_shows_sessions_as_they_land() {
        var (before, landed, activity, progress, running, after) = await AvaloniaSession.DispatchAsync(async () => {
            var gate = new TaskCompletionSource<StreamingResult>();
            Action<StreamedLine>? emit = null;
            var cli = new FakeKcapCli {
                ImportBehavior = (_, onLine, _) => {
                    emit = onLine;
                    return gate.Task;
                },
            };
            var run = new HistoryImportRun(100, 2, "from the last 90 days", 1, action => action());
            run.Start(cli, [new ImportRequest(ImportScopeChoice.Repo, null, [], ["a/b"])]);
            var vm = new DoneStepViewModel(() => Facts(import: run));
            await vm.OnEnterAsync(CancellationToken.None);
            var waiting = vm.ActivityLine;

            for (var i = 0; i < 20; i++) emit!(new StreamedLine(ProcessStreamKind.Stdout, $"  Loading s{i} (claude)"));
            for (var i = 0; i < 5; i++) emit!(new StreamedLine(ProcessStreamKind.Stdout, $"  Skipping k{i} (already current)"));
            var snapshot = (vm.LandedValue, vm.ActivityLine, vm.ProgressValue, vm.Running);

            gate.SetResult(new StreamingResult(0, false, []));
            await run.Completion;

            return (waiting, snapshot.LandedValue, snapshot.ActivityLine, snapshot.ProgressValue, snapshot.Running, vm.Running);
        });

        await Assert.That(before).IsEqualTo("Looking through your history…");
        await Assert.That(landed).IsEqualTo("20");
        await Assert.That(activity).IsEqualTo("25 of 100 sessions checked");
        await Assert.That(progress).IsEqualTo(25.0);
        await Assert.That(running).IsTrue();
        await Assert.That(after).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_facts_are_read_again_on_every_entry() {
        var calls = await AvaloniaSession.DispatchAsync(async () => {
            var count = 0;
            var vm = new DoneStepViewModel(() => { count++; return Facts(); });
            await vm.OnEnterAsync(CancellationToken.None);
            await vm.OnEnterAsync(CancellationToken.None);
            return count;
        });

        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Open_your_workspace_opens_the_signed_in_server() {
        var opened = await AvaloniaSession.DispatchAsync(async () => {
            var opener = new RecordingUrlOpener();
            var vm = new DoneStepViewModel(() => Facts(), opener);
            await vm.OnEnterAsync(CancellationToken.None);
            vm.OpenWorkspaceCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            return opener.Opened.ToList();
        });

        await Assert.That(opened).IsEquivalentTo(["https://acme.kcap.ai"]);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_workspace_opens_only_once_this_machine_is_registered() {
        var (preparing, early, openedEarly, ready, opened, prepares) = await AvaloniaSession.DispatchAsync(async () => {
            var opener = new RecordingUrlOpener();
            var registered = new TaskCompletionSource<WorkspaceReadiness>();
            var calls = 0;
            var vm = new DoneStepViewModel(() => Facts(), opener, _ => {
                calls++;
                return registered.Task;
            });
            await vm.OnEnterAsync(CancellationToken.None);
            var (preparingVisible, workspaceVisible) = (vm.PreparingVisible, vm.WorkspaceVisible);
            vm.OpenWorkspaceCommand.Execute().Subscribe(_ => { }, _ => { });
            Dispatcher.UIThread.RunJobs();
            var before = opener.Opened.Count;

            registered.SetResult(WorkspaceReadiness.Ready);
            await vm.WhenAnyValue(x => x.Readiness).FirstAsync(r => r == WorkspaceReadiness.Ready).ToTask();
            await vm.OnEnterAsync(CancellationToken.None);
            vm.OpenWorkspaceCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();

            return (preparingVisible, workspaceVisible, before, vm.WorkspaceVisible && !vm.PreparingVisible, opener.Opened.ToList(), calls);
        });

        await Assert.That(preparing).IsTrue();
        await Assert.That(early).IsFalse();
        await Assert.That(openedEarly).IsEqualTo(0);
        await Assert.That(ready).IsTrue();
        await Assert.That(opened).IsEquivalentTo(["https://acme.kcap.ai"]);
        await Assert.That(prepares).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_machine_that_is_not_registered_offers_a_retry() {
        var (notReady, title, body, linkWhileFailed, linkAfterRetry, calls) = await AvaloniaSession.DispatchAsync(async () => {
            var calls = new List<bool>();
            var vm = new DoneStepViewModel(() => Facts(), new RecordingUrlOpener(), retry => {
                calls.Add(retry);
                return Task.FromResult(calls.Count == 1 ? WorkspaceReadiness.DaemonNotRunning : WorkspaceReadiness.Ready);
            });
            await vm.OnEnterAsync(CancellationToken.None);
            await vm.WhenAnyValue(x => x.Readiness).FirstAsync(r => r != WorkspaceReadiness.Preparing).ToTask();
            var failed = (vm.NotReadyVisible, vm.NotReadyTitle, vm.NotReadyBody, vm.WorkspaceVisible);

            await vm.RetryCommand.Execute().ToTask();

            return (failed.NotReadyVisible, failed.NotReadyTitle, failed.NotReadyBody, failed.WorkspaceVisible, vm.WorkspaceVisible, calls);
        });

        await Assert.That(notReady).IsTrue();
        await Assert.That(title).IsEqualTo("Your workspace can't open yet");
        await Assert.That(body).Contains("daemon is running on test-mac");
        await Assert.That(linkWhileFailed).IsFalse();
        await Assert.That(linkAfterRetry).IsTrue();
        await Assert.That(calls).IsEquivalentTo([false, true]);
    }

    static DoneFacts LocalFacts(string? dashboardUrl = "http://localhost:5050", HistoryImportRun? import = null) =>
        new(["Claude Code"], false, false, import, true, "test-mac", null, null, true, dashboardUrl);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_done_names_no_terminal_command() {
        var texts = await AvaloniaSession.DispatchAsync(async () => {
            var run = await FinishedRun(30, 3);
            var all = new List<string>();
            foreach (var facts in new[] { LocalFacts(), LocalFacts(import: run), LocalFacts() with { Recording = [] },
                         LocalFacts() with { DaemonRunning = false, DaemonProblem = null } }) {
                var vm = new DoneStepViewModel(() => facts);
                await vm.OnEnterAsync(CancellationToken.None);
                all.AddRange([vm.Lede, vm.FooterLine ?? "", vm.DaemonLine, vm.FailedBody, vm.NextLabel ?? ""]);
            }

            return all;
        });

        foreach (var text in texts) {
            await Assert.That(text).DoesNotContain("kcap import");
            await Assert.That(text).DoesNotContain("kcap setup");
            await Assert.That(text).DoesNotContain("kcap daemon");
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_done_opens_the_dashboard_only_on_request() {
        var (onEntry, afterCommand, workspaceVisible, next) = await AvaloniaSession.DispatchAsync(async () => {
            var opener = new RecordingUrlOpener();
            var vm = new DoneStepViewModel(() => LocalFacts(), opener);
            await vm.OnEnterAsync(CancellationToken.None);
            var entered = opener.Opened.Count;
            vm.OpenDashboardCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();

            return (entered, opener.Opened.ToList(), vm.WorkspaceVisible, vm.NextLabel);
        });

        await Assert.That(onEntry).IsEqualTo(0);
        await Assert.That(afterCommand).IsEquivalentTo(["http://localhost:5050"]);
        await Assert.That(workspaceVisible).IsFalse();
        await Assert.That(next).IsEqualTo("Continue");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_done_hides_the_dashboard_when_its_url_is_unknown() {
        var visible = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new DoneStepViewModel(() => LocalFacts(dashboardUrl: null), new RecordingUrlOpener());
            await vm.OnEnterAsync(CancellationToken.None);

            return vm.DashboardVisible;
        });

        await Assert.That(visible).IsFalse();
    }

    sealed class RecordingUrlOpener : IUrlOpener {
        public readonly List<string> Opened = [];
        public void Open(string url) => Opened.Add(url);
    }
}
