using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Services;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Setup's daemon service: always ensured, and reported to the browser only when it asked.
///
/// <para>What the report half pins is mostly what it must NOT do: report when nothing was asked, report a
/// refusal over a machine something was attempted on, or give up on a single failed report and leave the
/// request outstanding under a screen saying it asked.</para>
/// </summary>
public class SetupDaemonServiceTests {
    const string ServerUrl = "https://tenant.example";
    const string FlowId    = "aaaaaaaaaaaaaaaaaaaaaa";

    static readonly DateTimeOffset Requested = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    sealed class Channel(FirstRunFlowResponse? view, params int[] reportCodes) : IFirstRunFlowChannel {
        public List<ReportFirstRunMachineActionRequest> Reports { get; } = [];

        public int Polls { get; private set; }

        public Task<FirstRunPollOutcome> PollAsync(string serverUrl, string flowId, CancellationToken ct) {
            Polls++;

            return Task.FromResult(new FirstRunPollOutcome(view is null ? 500 : 200, view));
        }

        public Task<FirstRunActionReportOutcome> ReportMachineActionAsync(
                string serverUrl, string flowId, ReportFirstRunMachineActionRequest request, CancellationToken ct) {
            Reports.Add(request);

            var code = Reports.Count <= reportCodes.Length ? reportCodes[Reports.Count - 1] : 200;

            return Task.FromResult(new FirstRunActionReportOutcome(code));
        }

        public Task<FirstRunCreateOutcome> CreateAsync(string s, string f, FirstRunMachineReport r, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunImportReportOutcome> ReportImportAsync(string s, string f, ReportFirstRunImportRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunImportReportOutcome> ReportImportOutcomeAsync(string s, string f, ReportFirstRunImportOutcomeRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunRelinquishOutcome> RelinquishAsync(string s, string f, string reason, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunHeartbeatOutcome> HeartbeatAsync(string s, string f, CancellationToken ct) => throw new NotSupportedException();
    }

    static FirstRunFlowResponse View(params string[] outstanding) =>
        new() {
            FlowId         = FlowId,
            Step           = "Done",
            CanFinish      = true,
            MachineActions = [.. outstanding.Select(c => new FirstRunMachineActionResponse {
                Capability = c, RequestedAt = Requested
            })]
        };

    static async Task<FirstRunMachineActionResult> EnsureAsync(Func<Task<FlowEnsure?>>? ladder = null) =>
        (await EnsureWithLineAsync(ladder)).Result;

    static Task<(FirstRunMachineActionResult Result, string? RenameLine)> EnsureWithLineAsync(
            Func<Task<FlowEnsure?>>? ladder = null) =>
        SetupDaemonService.EnsureAsync(
            new ConfigRoot("/nonexistent-config-root"),
            new ProfileContext(new(ServerUrl, "default", null, null), new ProfileConfig()),
            new UserHome("/nonexistent-home"),
            TimeProvider.System,
            previousName: null,
            ladder ?? (() => Task.FromResult<FlowEnsure?>(null)));

    static readonly ServiceEnsureJson Installed = new("kcap-1", "not_installed", "install", "installed", Verified: true);

    static Task ReportAsync(Channel channel, FirstRunMachineActionResult? result = null) =>
        SetupDaemonService.ReportAsync(
            channel, ServerUrl, FlowId,
            result ?? new FirstRunMachineActionResult(FirstRunMachineActionOutcomes.AlreadyEnabled, null));

    static readonly FirstRunMachineActionResult Enabled = new(FirstRunMachineActionOutcomes.AlreadyEnabled, null);

    /// <summary>Nothing asked, so nothing is reported, which keeps a flow from carrying an outcome it never
    /// requested.</summary>
    [Test]
    public async Task Nothing_outstanding_reports_nothing() {
        var channel = new Channel(View());

        await ReportAsync(channel);

        await Assert.That(channel.Reports).IsEmpty();
    }

    /// <summary>A capability this step does not own is left alone: the shim is the live lane's.</summary>
    [Test]
    public async Task Another_capability_outstanding_is_not_this_step_s_to_answer() {
        var channel = new Channel(View(FirstRunMachineCapabilities.PathShim));

        await ReportAsync(channel);

        await Assert.That(channel.Reports).IsEmpty();
    }

    [Test]
    public async Task An_unreadable_poll_reports_nothing() {
        var channel = new Channel(null);

        await ReportAsync(channel);

        await Assert.That(channel.Reports).IsEmpty();
    }

    /// <summary>The outcome is reported against the REQUEST's stamp, not a fresh one: the stamp is the
    /// request's identity, and a report carrying another would answer whichever one happens to stand.</summary>
    [Test]
    public async Task The_outcome_is_reported_against_the_request_it_answers() {
        var channel = new Channel(View(FirstRunMachineCapabilities.DaemonService));

        await ReportAsync(channel, Enabled);

        await Assert.That(channel.Reports.Count).IsEqualTo(1);
        await Assert.That(channel.Reports[0].Capability).IsEqualTo(FirstRunMachineCapabilities.DaemonService);
        await Assert.That(channel.Reports[0].RequestedAt).IsEqualTo(Requested);
        await Assert.That(channel.Reports[0].Outcome).IsEqualTo(FirstRunMachineActionOutcomes.AlreadyEnabled);
    }

    /// <summary>No service manager on this platform attempted nothing, so it refuses rather than failing.</summary>
    [Test]
    public async Task A_platform_with_no_service_manager_refuses_rather_than_failing() {
        var result = await EnsureAsync();

        await Assert.That(result.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Refused);
        await Assert.That(result.Reason).IsEqualTo(FirstRunMachineActionReasons.UnsupportedPlatform);
    }

    /// <summary>A ladder that threw attempted something, so it is a failure and not a refusal.</summary>
    [Test]
    public async Task A_ladder_that_throws_is_a_failure() {
        var result = await EnsureAsync(() => throw new InvalidOperationException("the manager query died"));

        await Assert.That(result.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Failed);
        await Assert.That(result.Reason).IsNull();
    }

    [Test]
    public async Task The_ladder_outcome_is_returned_to_setup() {
        var result = await EnsureAsync(() => Task.FromResult<FlowEnsure?>(
            new(new("kcap", "running", "none", "already_enabled"))));

        await Assert.That(result.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.AlreadyEnabled);
    }

    [Test]
    public async Task No_rename_says_nothing_about_an_old_service() {
        var (_, line) = await EnsureWithLineAsync(() => Task.FromResult<FlowEnsure?>(new(Installed)));

        await Assert.That(line).IsNull();
    }

    [Test]
    public async Task A_retired_service_is_named() {
        var (result, line) = await EnsureWithLineAsync(() => Task.FromResult<FlowEnsure?>(new(Installed, Retired: "kcap")));

        await Assert.That(result.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Enabled);
        await Assert.That(line).Contains("kcap");
        await Assert.That(line).DoesNotContain("uninstall");
    }

    /// <summary>A retire followed by a failed install leaves no daemon service, which the line must not
    /// read as a clean rename.</summary>
    [Test]
    public async Task A_retired_service_beside_a_failed_install_is_a_warning() {
        var failed = new ServiceEnsureJson("kcap-1", "not_installed", "install", "refused", "retry", "verify_readiness_timeout");

        var (_, line) = await EnsureWithLineAsync(() => Task.FromResult<FlowEnsure?>(new(failed, Retired: "kcap")));

        await Assert.That(line).Contains("[yellow]![/]");
        await Assert.That(line).Contains("no daemon service is running");
    }

    /// <summary>A service the rename could not retire still runs a second daemon, so the line says how to
    /// remove it.</summary>
    [Test]
    public async Task A_service_left_installed_comes_with_its_uninstall_command() {
        var (_, line) = await EnsureWithLineAsync(() => Task.FromResult<FlowEnsure?>(new(Installed, LeftInstalled: "kcap")));

        await Assert.That(line).Contains("kcap daemon service uninstall --name kcap");
    }

    /// <summary>A refused report is retried, because there is no next tick here to retry on.</summary>
    [Test]
    public async Task A_refused_report_is_retried() {
        var channel = new Channel(View(FirstRunMachineCapabilities.DaemonService), 500, 200);

        await ReportAsync(channel);

        await Assert.That(channel.Reports.Count).IsEqualTo(2);
    }

    /// <summary>A report nothing ever takes stops rather than looping: the request staying outstanding is
    /// the honest reading.</summary>
    [Test]
    public async Task A_report_that_never_lands_gives_up_bounded() {
        var channel = new Channel(View(FirstRunMachineCapabilities.DaemonService), 500, 500, 500, 500, 500);

        await ReportAsync(channel);

        await Assert.That(channel.Reports.Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_path_refresh_that_changed_nothing_prints_nothing() {
        await Assert.That(await RefreshPathAsync(() => Task.FromResult<PathRefresh?>(null))).IsNull();
    }

    [Test]
    public async Task A_path_refresh_that_threw_says_the_daemon_may_not_find_every_harness() {
        var line = await RefreshPathAsync(() => throw new IOException("unreadable plist"));

        await Assert.That(line).Contains("may not find every harness");
    }

    /// <summary>kcap-service is reinstalled only with --system; the plain command would write a Scheduled Task.</summary>
    [Test]
    public async Task A_failed_refresh_suggests_the_reinstall_that_names_the_installed_unit() {
        var line = await RefreshPathAsync(() => Task.FromResult<PathRefresh?>(
            new(UnitRefresh.UnitUnsupported, Reinstall: "kcap daemon service install --replace --verify --system")));

        await Assert.That(line).Contains("kcap daemon service install --replace --verify --system");
    }

    [Test]
    public async Task A_reloaded_service_says_it_was_restarted() {
        await Assert.That(await RefreshPathAsync(() => Task.FromResult<PathRefresh?>(new(UnitRefresh.Reloaded)))).Contains("Restarted");
    }

    static Task<string?> RefreshPathAsync(Func<Task<PathRefresh?>> refresh) =>
        SetupDaemonService.RefreshPathAsync(
            new ConfigRoot("/nonexistent-config-root"),
            new ProfileContext(new(ServerUrl, "default", null, null), new ProfileConfig()),
            new UserHome("/nonexistent-home"),
            TimeProvider.System,
            HarnessRegistry.Over(BinaryProbe.Searching(null)),
            refresh);

    [Test]
    public async Task A_busy_service_lock_asks_for_a_retry_not_a_reinstall() {
        var line = await RefreshPathAsync(() => Task.FromResult<PathRefresh?>(new(UnitRefresh.Contended)));

        await Assert.That(line).Contains("Run setup again shortly");
        await Assert.That(line).DoesNotContain("Reinstall");
    }

    [Test]
    public async Task A_failed_refresh_names_the_service_managers_error() {
        var line = await RefreshPathAsync(() => Task.FromResult<PathRefresh?>(
            new(UnitRefresh.Failed, "the daemon is not loaded — run `kcap daemon service start`")));

        await Assert.That(line).Contains("the daemon is not loaded");
    }
}
