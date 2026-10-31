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
        await Assert.That(nothing).IsEqualTo("No new coding tools connected");
        await Assert.That(blocked).IsEqualTo("No new coding tools connected");
    }

    /// A setup that connected nothing names Settings → Harnesses once, in the lede when nothing was imported
    /// and under the empty capture list when history was.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task Connecting_later_points_at_harnesses_settings_once(bool local, bool imported) {
        var (lede, line) = await AvaloniaSession.DispatchAsync(async () => {
            var run = imported ? await FinishedRun(30, 0) : null;
            var facts = (local ? LocalFacts(import: run) : Facts(import: run)) with { Recording = [] };
            var vm = new DoneStepViewModel(() => facts);
            await vm.OnEnterAsync(CancellationToken.None);
            return (vm.Lede, vm.NoAgentsLine);
        });

        const string settings = "Settings → Harnesses";
        await Assert.That(lede.Contains(settings, StringComparison.Ordinal) ^ line.Contains(settings, StringComparison.Ordinal)).IsTrue();
        await Assert.That(line.Contains(settings, StringComparison.Ordinal)).IsEqualTo(imported);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Failed_imports_are_named_with_where_they_still_are() {
        var (visible, title, body, landed, unit) = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new DoneStepViewModel(() => Facts(import: null));
            var run = await FinishedRun(30, 3);
            vm = new DoneStepViewModel(() => Facts(import: run));
            await vm.OnEnterAsync(CancellationToken.None);

            return (vm.FailedVisible, vm.FailedTitle, vm.FailedBody, vm.LandedValue, vm.LandedUnit);
        });

        await Assert.That(visible).IsTrue();
        await Assert.That(title).IsEqualTo("3 sessions failed to import");
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

    static DoneFacts LocalFacts(string? dashboardUrl = "http://localhost:5050", HistoryImportRun? import = null) =>
        new(["Claude Code"], false, false, import, true, "test-mac", null, null, true, dashboardUrl);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_done_names_no_terminal_command() {
        var texts = await AvaloniaSession.DispatchAsync(async () => {
            var run = await FinishedRun(30, 3);
            var all = new List<string>();
            foreach (var facts in new[] { LocalFacts(), LocalFacts(import: run), LocalFacts() with { Recording = [] },
                         LocalFacts() with { DaemonRunning = false, DaemonProblem = "kcap isn't available on this machine." } }) {
                var vm = new DoneStepViewModel(() => facts);
                await vm.OnEnterAsync(CancellationToken.None);
                all.AddRange([vm.Lede, string.Concat(vm.ImportHint?.Select(s => s.Text) ?? []), vm.ServiceBody, vm.FailedBody]);
            }

            return all;
        });

        foreach (var text in texts) {
            await Assert.That(text).DoesNotContain("kcap import");
            await Assert.That(text).DoesNotContain("kcap setup");
            await Assert.That(text).DoesNotContain("kcap daemon");
        }
    }

    /// Starting agents from elsewhere needs a cloud workspace, so only cloud's service card offers it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task Only_cloud_promises_starting_agents_from_anywhere(bool local, bool promised) {
        var body = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new DoneStepViewModel(() => local ? LocalFacts() : Facts());
            await vm.OnEnterAsync(CancellationToken.None);
            return vm.ServiceBody;
        });

        await Assert.That(body).StartsWith("Keeps kcap storing sessions when the app is closed");
        await Assert.That(body.Contains("from anywhere", StringComparison.Ordinal)).IsEqualTo(promised);
    }

    /// One primary, Open kcap, on either edition; the browser holds the web workspace or the local dashboard.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false, "https://acme.kcap.ai")]
    [Arguments(true, "http://localhost:5050")]
    public async Task Open_in_browser_opens_the_workspace_or_the_local_dashboard_only_on_request(bool local, string expected) {
        var (onEntry, afterCommand, visible, next) = await AvaloniaSession.DispatchAsync(async () => {
            var opener = new RecordingUrlOpener();
            var vm = new DoneStepViewModel(() => local ? LocalFacts() : Facts(), opener);
            await vm.OnEnterAsync(CancellationToken.None);
            var entered = opener.Opened.Count;
            vm.OpenInBrowserCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();

            return (entered, opener.Opened.ToList(), vm.OpenInBrowserVisible, vm.NextLabel);
        });

        await Assert.That(onEntry).IsEqualTo(0);
        await Assert.That(afterCommand).IsEquivalentTo([expected]);
        await Assert.That(visible).IsTrue();
        await Assert.That(next).IsEqualTo("Open kcap");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Open_in_browser_is_hidden_when_there_is_nowhere_to_open() {
        var visible = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new DoneStepViewModel(() => LocalFacts(dashboardUrl: null), new RecordingUrlOpener());
            await vm.OnEnterAsync(CancellationToken.None);

            return vm.OpenInBrowserVisible;
        });

        await Assert.That(visible).IsFalse();
    }

    // ── the background service ───────────────────────────────────────────────

    /// The eyebrow and the pill read the service; Finish later is offered only while it is not running.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true, false, null, "Ready", "Running", false)]
    [Arguments(false, false, null, "Finishing up", "Installing…", false)]
    [Arguments(false, true, "The daemon service is not installed.", "Finishing up", "Installing…", false)]
    [Arguments(false, false, "The daemon install failed (verify_viability).", "One thing left", "Not running", true)]
    public async Task The_eyebrow_and_pill_follow_the_service(
            bool running, bool busy, string? problem, string eyebrow, string pill, bool finishLater) {
        var vm = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new DoneStepViewModel(() => Facts() with { DaemonRunning = running, DaemonBusy = busy, DaemonProblem = problem });
            await vm.OnEnterAsync(CancellationToken.None);
            return vm;
        });

        await Assert.That(vm.Eyebrow).IsEqualTo(eyebrow);
        await Assert.That(vm.ServiceStatus).IsEqualTo(pill);
        await Assert.That(vm.OffersFinishLater).IsEqualTo(finishLater);
        await Assert.That(vm.ServiceFailed).IsEqualTo(finishLater);
    }

    /// The problem is the card's, with the daemon's own account behind See details; Retry runs the enable
    /// again and the page follows the service as it changes.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Retry_runs_the_enable_again_and_the_page_follows_the_service() {
        var (details, shownDetails, failedBefore, retries, eyebrowAfter, detailsAfter) = await AvaloniaSession.DispatchAsync(async () => {
            var facts = Facts() with {
                DaemonRunning = false, DaemonProblem = "The daemon start failed (verify_viability).",
                ServiceDetail = "The daemon start is not yet confirmed — check status.",
            };
            var changed = new System.Reactive.Subjects.Subject<System.Reactive.Unit>();
            var retries = 0;
            var vm = new DoneStepViewModel(() => facts, prepare: retry => {
                if (!retry) return Task.FromResult(WorkspaceReadiness.DaemonNotRunning);

                retries++;
                facts = facts with { DaemonRunning = true, DaemonProblem = null, ServiceDetail = null };
                changed.OnNext(System.Reactive.Unit.Default);
                return Task.FromResult(WorkspaceReadiness.Ready);
            }, restate: changed);
            await vm.OnEnterAsync(CancellationToken.None);
            await vm.WhenAnyValue(x => x.Readiness).FirstAsync(r => r != WorkspaceReadiness.Preparing).ToTask();
            var failed = vm.ServiceFailed;

            await vm.ToggleDetailsCommand.Execute().ToTask();
            var shown = vm.DetailsVisible;
            var text = vm.ServiceDetails;
            await vm.RetryCommand.Execute().ToTask();

            return (text, shown, failed, retries, vm.Eyebrow, vm.DetailsVisible);
        });

        await Assert.That(failedBefore).IsTrue();
        await Assert.That(shownDetails).IsTrue();
        await Assert.That(details).Contains("verify_viability");
        await Assert.That(details).Contains("not yet confirmed");
        await Assert.That(retries).IsEqualTo(1);
        await Assert.That(eyebrowAfter).IsEqualTo("Ready");
        await Assert.That(detailsAfter).IsFalse();
    }

    /// An outcome line that arrives while the service is not failed still shows, beside the pill.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_outcome_line_shows_whatever_the_service_state() {
        var (installing, failed) = await AvaloniaSession.DispatchAsync(async () => {
            async Task<(string? Note, string Details)> For(DoneFacts facts) {
                var vm = new DoneStepViewModel(() => facts);
                await vm.OnEnterAsync(CancellationToken.None);
                return (vm.ServiceNote, vm.ServiceDetails);
            }

            const string line = "A daemon is running outside the installed service.";
            return (await For(Facts() with { DaemonRunning = false, ServiceDetail = line }),
                await For(Facts() with { DaemonRunning = false, DaemonProblem = "The daemon start failed.", ServiceDetail = line }));
        });

        await Assert.That(installing.Note).IsEqualTo("A daemon is running outside the installed service.");
        await Assert.That(failed.Note).IsNull();
        await Assert.That(failed.Details).Contains("A daemon is running outside the installed service.");
    }

    /// The page re-reads its facts only while it is the current page, so a service change before or after adds no reads.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_service_change_off_the_page_reads_nothing() {
        var reads = await AvaloniaSession.DispatchAsync(async () => {
            var count = 0;
            var changed = new System.Reactive.Subjects.Subject<System.Reactive.Unit>();
            var vm = new DoneStepViewModel(() => { count++; return Facts(); }, restate: changed);
            changed.OnNext(System.Reactive.Unit.Default);
            await vm.OnEnterAsync(CancellationToken.None);
            changed.OnNext(System.Reactive.Unit.Default);
            await vm.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None);
            changed.OnNext(System.Reactive.Unit.Default);
            return count;
        });

        await Assert.That(reads).IsEqualTo(2);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_restart_line_shows_only_when_new_hooks_need_one() {
        var (remote, local, nothing, blocked, pathAdded) = await AvaloniaSession.DispatchAsync(async () => {
            async Task<string?> LineFor(DoneFacts facts) {
                var vm = new DoneStepViewModel(() => facts);
                await vm.OnEnterAsync(CancellationToken.None);
                return vm.RestartLine;
            }

            return (await LineFor(Facts()), await LineFor(Facts() with { Local = true }),
                await LineFor(Facts(recording: [])), await LineFor(Facts(hazard: true)),
                await LineFor(Facts() with { PathAdded = true }));
        });

        await Assert.That(remote).IsEqualTo("Agents already open will start storing sessions the next time you launch them");
        // A terminal open before setup changed PATH still cannot run the hooks.
        await Assert.That(pathAdded).IsEqualTo(
            "Agents already open will start storing sessions once you launch them again from a new terminal");
        await Assert.That(local).IsEqualTo(remote);
        await Assert.That(nothing).IsNull();
        await Assert.That(blocked).IsNull();
    }

    // ── registering this machine before the browser opens ────────────────────

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
            var (preparingVisible, linkVisible) = (vm.PreparingVisible, vm.OpenInBrowserVisible);
            vm.OpenInBrowserCommand.Execute().Subscribe(_ => { }, _ => { });
            Dispatcher.UIThread.RunJobs();
            var before = opener.Opened.Count;

            registered.SetResult(WorkspaceReadiness.Ready);
            await vm.WhenAnyValue(x => x.Readiness).FirstAsync(r => r == WorkspaceReadiness.Ready).ToTask();
            await vm.OnEnterAsync(CancellationToken.None);
            vm.OpenInBrowserCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();

            return (preparingVisible, linkVisible, before, vm.OpenInBrowserVisible && !vm.PreparingVisible, opener.Opened.ToList(), calls);
        });

        await Assert.That(preparing).IsTrue();
        await Assert.That(early).IsFalse();
        await Assert.That(openedEarly).IsEqualTo(0);
        await Assert.That(ready).IsTrue();
        await Assert.That(opened).IsEquivalentTo(["https://acme.kcap.ai"]);
        await Assert.That(prepares).IsEqualTo(1);
    }

    /// A server that did not take the report is the not-ready panel's to say, with a retry; a daemon that is
    /// not running is the service card's, and finishing later is offered for either.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(WorkspaceReadiness.Unreported, true, false)]
    [Arguments(WorkspaceReadiness.DaemonNotRunning, false, true)]
    public async Task A_machine_that_is_not_registered_offers_a_retry(WorkspaceReadiness first, bool panel, bool serviceFailed) {
        var (notReady, failed, finishLater, linkWhileFailed, linkAfterRetry, calls) = await AvaloniaSession.DispatchAsync(async () => {
            var calls = new List<bool>();
            var vm = new DoneStepViewModel(() => Facts() with { DaemonRunning = false }, new RecordingUrlOpener(), retry => {
                calls.Add(retry);
                return Task.FromResult(calls.Count == 1 ? first : WorkspaceReadiness.Ready);
            });
            await vm.OnEnterAsync(CancellationToken.None);
            await vm.WhenAnyValue(x => x.Readiness).FirstAsync(r => r != WorkspaceReadiness.Preparing).ToTask();
            var before = (vm.NotReadyVisible, vm.ServiceFailed, vm.OffersFinishLater, vm.OpenInBrowserVisible);

            await vm.RetryCommand.Execute().ToTask();

            return (before.NotReadyVisible, before.ServiceFailed, before.OffersFinishLater, before.OpenInBrowserVisible, vm.OpenInBrowserVisible, calls);
        });

        await Assert.That(notReady).IsEqualTo(panel);
        await Assert.That(failed).IsEqualTo(serviceFailed);
        await Assert.That(finishLater).IsTrue();
        await Assert.That(linkWhileFailed).IsFalse();
        await Assert.That(linkAfterRetry).IsTrue();
        await Assert.That(calls).IsEquivalentTo([false, true]);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Signing_in_to_another_workspace_registers_this_machine_again() {
        var (linkBeforeReport, opened, calls) = await AvaloniaSession.DispatchAsync(async () => {
            var opener = new RecordingUrlOpener();
            var url = "https://acme.kcap.ai";
            var reports = new List<TaskCompletionSource<WorkspaceReadiness>>();
            var vm = new DoneStepViewModel(() => Facts() with { WorkspaceUrl = url }, opener, _ => {
                reports.Add(new TaskCompletionSource<WorkspaceReadiness>());
                return reports[^1].Task;
            });
            await vm.OnEnterAsync(CancellationToken.None);
            reports[0].SetResult(WorkspaceReadiness.Ready);
            await vm.WhenAnyValue(x => x.Readiness).FirstAsync(r => r == WorkspaceReadiness.Ready).ToTask();

            url = "https://other.kcap.ai";
            await vm.OnEnterAsync(CancellationToken.None);
            var link = vm.OpenInBrowserVisible;
            reports[1].SetResult(WorkspaceReadiness.Ready);
            await vm.WhenAnyValue(x => x.Readiness).FirstAsync(r => r == WorkspaceReadiness.Ready).ToTask();
            vm.OpenInBrowserCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();

            return (link, opener.Opened.ToList(), reports.Count);
        });

        await Assert.That(linkBeforeReport).IsFalse();
        await Assert.That(opened).IsEquivalentTo(["https://other.kcap.ai"]);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_report_for_the_previous_workspace_does_not_open_the_new_one() {
        var (readyAfterStaleReport, calls) = await AvaloniaSession.DispatchAsync(async () => {
            var url = "https://acme.kcap.ai";
            var reports = new List<TaskCompletionSource<WorkspaceReadiness>>();
            var vm = new DoneStepViewModel(() => Facts() with { WorkspaceUrl = url }, new RecordingUrlOpener(), _ => {
                reports.Add(new TaskCompletionSource<WorkspaceReadiness>());
                return reports[^1].Task;
            });
            await vm.OnEnterAsync(CancellationToken.None);

            url = "https://other.kcap.ai";
            await vm.OnEnterAsync(CancellationToken.None);
            reports[0].SetResult(WorkspaceReadiness.Ready);
            Dispatcher.UIThread.RunJobs();

            return (vm.Readiness == WorkspaceReadiness.Ready, reports.Count);
        });

        await Assert.That(readyAfterStaleReport).IsFalse();
        await Assert.That(calls).IsEqualTo(2);
    }

    // ── connected agents ─────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Connected_agents_read_connected_and_say_the_next_session_is_the_first_stored() {
        var (states, firstSession, label, added) = await AvaloniaSession.DispatchAsync(async () => {
            var added = 0;
            var vm = new DoneStepViewModel(() => Facts(recording: ["Claude Code", "Codex"]), addAgent: () => added++);
            await vm.OnEnterAsync(CancellationToken.None);
            await vm.AddAgentCommand.Execute().ToTask();

            return (vm.Capture.Select(c => c.State).ToList(), vm.FirstSessionVisible, vm.AddAgentLabel, added);
        });

        await Assert.That(states).IsEquivalentTo(["Connected", "Connected"]);
        await Assert.That(firstSession).IsTrue();
        await Assert.That(label).IsEqualTo("Add another agent");
        await Assert.That(added).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Adding_an_agent_is_offered_only_where_the_wizard_can_go_back_to_it() {
        var visible = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new DoneStepViewModel(() => Facts());
            await vm.OnEnterAsync(CancellationToken.None);
            return vm.AddAgentVisible;
        });

        await Assert.That(visible).IsFalse();
    }

    sealed class RecordingUrlOpener : IUrlOpener {
        public readonly List<string> Opened = [];
        public void Open(string url) => Opened.Add(url);
    }
}
