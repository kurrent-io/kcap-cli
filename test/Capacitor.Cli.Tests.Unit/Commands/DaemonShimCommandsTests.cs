using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The <c>kcap daemon shim ensure</c> ladder: a fresh login-shell probe decides already-on-path /
/// install / fail-closed refusal (pure classifier rows), and the verb's JSON contract carries the
/// outcome + coded reason the flow keys off. The installer's own mechanics
/// (preflight taxonomy, osascript, post-install re-probe) are covered in Core.Tests — here the
/// install arm is driven through the injected seams so the verb's wire contract is testable without
/// a real admin prompt.
/// </summary>
[NotInParallel] // most tests capture Console (process-global); the dispatch test writes usage to the real stderr
public class DaemonShimCommandsTests {
    sealed class FakeProbe(bool? onPath) : ILoginShellProbe {
        public Task<string?> TerminalPathAsync(CancellationToken ct) => Task.FromResult<string?>("/usr/bin:/bin");
        public Task<bool?> KcapOnPathAsync(CancellationToken ct, bool forceRefresh = false) => Task.FromResult(onPath);
        public Task<string?> KcapPathAsync(CancellationToken ct, bool forceRefresh = false) => Task.FromResult<string?>(null);
    }

    static async Task<(int Exit, ShimEnsureJson? Json, string? Out)> Run(
            string[] args, bool? onPath = null, string? target = "/usr/local/lib/kcap",
            Func<string, CancellationToken, Task<ShimResult>>? install = null,
            Func<string, ShimPreflight>? preflight = null, bool installable = false) {
        using var capture = ConsoleOutput.StartFullCapture();
        var exit = await DaemonShimCommands.Ensure(args, TimeProvider.System, resolveTarget: () => target,
            probe: new FakeProbe(onPath), install: install,
            preflight: preflight ?? (_ => ShimPreflight.Installable), installable: installable);
        var output = capture.GetCapturedOutput();
        var text = output + capture.GetCapturedError();
        ShimEnsureJson? json = null;
        if (output.Contains("{\"capability\"")) json = JsonSerializer.Deserialize<ShimEnsureJson>(output, ShimJsonContext.Default.ShimEnsureJson);
        return (exit, json, text);
    }

    // --- Link-target resolution (npm launcher vs standalone binary) ---

    [Test]
    public async Task ResolveLinkTarget_uses_the_npm_launcher_when_this_is_an_npm_install() {
        // .../node_modules/@kurrent/kcap-linux-x64/bin/kcap  →  launcher:
        // .../node_modules/@kurrent/kcap/bin/kcap.js
        // GetFullPath both sides: the production branch normalizes the derived launcher, and on
        // Windows a hardcoded POSIX path would otherwise compare against the current drive's root.
        var native   = Path.GetFullPath("/usr/local/lib/node_modules/@kurrent/kcap-linux-x64/bin/kcap");
        var launcher = Path.GetFullPath("/usr/local/lib/node_modules/@kurrent/kcap/bin/kcap.js");

        var target = DaemonShimCommands.ResolveLinkTarget(() => native, p => p == launcher);

        await Assert.That(target).IsEqualTo(launcher);
    }

    [Test]
    public async Task ResolveLinkTarget_falls_back_to_the_running_binary_when_no_launcher_sibling_exists() {
        var native = "/opt/kcap/bin/kcap";
        var target = DaemonShimCommands.ResolveLinkTarget(() => native, _ => false);
        await Assert.That(target).IsEqualTo(native);
    }

    /// npm on Windows nests the platform package, and kcap.js cannot run from PATH; the prefix's
    /// kcap.cmd is what runs the launcher.
    [Test]
    public async Task ResolveLinkTarget_on_Windows_uses_the_npm_prefix_shim_for_a_nested_install() {
        var prefix   = Path.GetFullPath("/Users/someone/AppData/Roaming/npm");
        var modules  = Path.Combine(prefix, "node_modules");
        var native   = Path.Combine(modules, "@kurrent", "kcap", "node_modules", "@kurrent", "kcap-win-x64", "bin", "kcap.exe");
        var launcher = Path.Combine(modules, "@kurrent", "kcap", "bin", "kcap.js");
        var shim     = Path.Combine(prefix, "kcap.cmd");

        var target = DaemonShimCommands.ResolveLinkTarget(() => native, p => p == launcher || p == shim, windows: true);

        await Assert.That(target).IsEqualTo(shim);
    }

    [Test]
    public async Task ResolveLinkTarget_on_Windows_uses_the_npm_prefix_shim_for_a_flat_install() {
        var prefix   = Path.GetFullPath("/Users/someone/AppData/Roaming/npm");
        var modules  = Path.Combine(prefix, "node_modules");
        var native   = Path.Combine(modules, "@kurrent", "kcap-win-x64", "bin", "kcap.exe");
        var launcher = Path.Combine(modules, "@kurrent", "kcap", "bin", "kcap.js");
        var shim     = Path.Combine(prefix, "kcap.cmd");

        var target = DaemonShimCommands.ResolveLinkTarget(() => native, p => p == launcher || p == shim, windows: true);

        await Assert.That(target).IsEqualTo(shim);
    }

    [Test]
    public async Task ResolveLinkTarget_on_Windows_uses_the_running_binary_outside_npm() {
        var native = Path.GetFullPath("/Users/someone/AppData/Local/kcap/current/kcap.exe");
        var target = DaemonShimCommands.ResolveLinkTarget(() => native, _ => false, windows: true);
        await Assert.That(target).IsEqualTo(native);
    }

    [Test]
    public async Task ResolveLinkTarget_null_process_path_is_null() {
        var target = DaemonShimCommands.ResolveLinkTarget(() => null, _ => true);
        await Assert.That(target).IsNull();
    }

    // --- Classifier rows ---

    [Test]
    public async Task Classify_already_on_path_is_terminal_when_probe_finds_kcap() {
        var d = ShimEnsureClassifier.Classify(onPath: true, installable: true);
        await Assert.That(d.Action).IsEqualTo(ShimEnsureAction.AlreadyOnPath);
        await Assert.That(d.Reason).IsNull();
    }

    [Test]
    public async Task Classify_installable_only_with_an_installer_and_positive_absence() {
        var d = ShimEnsureClassifier.Classify(onPath: false, installable: true);
        await Assert.That(d.Action).IsEqualTo(ShimEnsureAction.Install);
    }

    [Test]
    public async Task Classify_unknown_probe_fails_closed_even_with_an_installer() {
        var d = ShimEnsureClassifier.Classify(onPath: null, installable: true);
        await Assert.That(d.Action).IsEqualTo(ShimEnsureAction.Refuse);
        await Assert.That(d.Reason).IsEqualTo("probe_unknown");
    }

    [Test]
    public async Task Classify_without_an_installer_refuses_even_on_positive_absence() {
        var d = ShimEnsureClassifier.Classify(onPath: false, installable: false);
        await Assert.That(d.Action).IsEqualTo(ShimEnsureAction.Refuse);
        await Assert.That(d.Reason).IsEqualTo("unsupported_platform");
    }

    // Platform beats probe: without an installer the refusal is unsupported_platform even when the probe
    // is unknown — the flow expects a stable platform row, not a probe-dependent one.
    [Test]
    public async Task Classify_without_an_installer_and_an_unknown_probe_is_unsupported_platform() {
        var d = ShimEnsureClassifier.Classify(onPath: null, installable: false);
        await Assert.That(d.Action).IsEqualTo(ShimEnsureAction.Refuse);
        await Assert.That(d.Reason).IsEqualTo("unsupported_platform");
    }

    [Test]
    public async Task Classify_already_on_path_is_terminal_without_an_installer_too() {
        var d = ShimEnsureClassifier.Classify(onPath: true, installable: false);
        await Assert.That(d.Action).IsEqualTo(ShimEnsureAction.AlreadyOnPath);
    }

    // --- Verb ladder / JSON contract ---

    [Test]
    public async Task Ensure_already_on_path_exits_zero_and_reports_already_on_path() {
        var (exit, json, _) = await Run(["--json"], onPath: true);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("already_on_path");
        await Assert.That(json.OnPath).IsTrue();
        await Assert.That(json.Action).IsEqualTo("none");
    }

    [Test]
    public async Task Ensure_probe_unknown_fails_closed_with_coded_reason() {
        var (exit, json, _) = await Run(["--json"], onPath: null, installable: true);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("refused");
        await Assert.That(json.Reason).IsEqualTo("probe_unknown");
    }

    [Test]
    public async Task Ensure_without_an_installer_refuses_with_unsupported_platform() {
        var (exit, json, _) = await Run(["--json"], onPath: false, installable: false);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("refused");
        await Assert.That(json.Reason).IsEqualTo("unsupported_platform");
    }

    [Test]
    public async Task Ensure_no_cli_path_refuses_before_any_probe() {
        var (exit, json, _) = await Run(["--json"], target: null);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("refused");
        await Assert.That(json.Reason).IsEqualTo("no_cli_path");
        await Assert.That(json.Target).IsNull();
    }

    [Test]
    public async Task Ensure_installed_exits_zero_with_installed_outcome() {
        var (exit, json, _) = await Run(["--json"], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Installed, null, null)));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("installed");
        await Assert.That(json.OnPath).IsTrue();
        await Assert.That(json.Action).IsEqualTo("install");
    }

    [Test]
    public async Task Ensure_installed_but_not_on_path_exits_nonzero_with_detail() {
        var (exit, json, _) = await Run(["--json"], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.InstalledButNotOnPath, "Add the line", null)));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("installed_not_on_path");
        await Assert.That(json.Detail).IsEqualTo("Add the line");
        await Assert.That(json.OnPath).IsFalse();
    }

    [Test]
    public async Task Ensure_cancelled_reports_cancelled() {
        var (exit, json, _) = await Run(["--json"], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Cancelled, null, null)));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("cancelled");
    }

    [Test]
    public async Task Ensure_failed_carries_detail_and_sudo_fallback() {
        var (exit, json, _) = await Run(["--json"], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Failed, "osascript failed", "sudo ln -s ...")));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("failed");
        await Assert.That(json.Detail).IsEqualTo("osascript failed");
        await Assert.That(json.SudoFallback).IsEqualTo("sudo ln -s ...");
    }

    [Test]
    public async Task Ensure_failed_without_sudo_fallback_still_exits_nonzero() {
        var (exit, json, _) = await Run(["--json"], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Failed, "could not re-verify", null)));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("failed");
        await Assert.That(json.Detail).IsEqualTo("could not re-verify");
        await Assert.That(json.SudoFallback).IsNull();
        // A null re-probe means the PATH was never positively re-read — OnPath must not claim false.
        await Assert.That(json.OnPath).IsNull();
    }

    [Test]
    public async Task Ensure_a_conflict_at_usr_local_bin_does_not_stop_a_path_folder_install() {
        var (exit, json, _) = await Run(["--json"], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Installed, null, null)),
            preflight: _ => ShimPreflight.Conflict);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("installed");
    }

    // A failed install with a foreign entry at the destination is the coded conflict row, not a generic failure.
    [Test]
    public async Task Ensure_failed_install_with_conflict_now_is_still_the_coded_conflict_row() {
        var (exit, json, _) = await Run(["--json"], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Failed, "ln: /usr/local/bin/kcap: File exists", null)),
            preflight: _ => ShimPreflight.Conflict);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("refused");
        await Assert.That(json.Reason).IsEqualTo("conflict");
        await Assert.That(json.Detail).Contains("left untouched");
    }

    // A failed install with NO foreign entry at the destination stays a plain failure.
    [Test]
    public async Task Ensure_failed_install_with_installable_preflight_stays_failed() {
        var (exit, json, _) = await Run(["--json"], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Failed, "osascript failed", null)),
            preflight: _ => ShimPreflight.Installable);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("failed");
        await Assert.That(json.Reason).IsNull();
    }

    [Test]
    public async Task Ensure_unknown_flag_is_rejected_before_any_probe_or_prompt() {
        var (exit, _, text) = await Run(["--bogus"], onPath: false, installable: true,
            install: (_, _) => throw new InvalidOperationException("the install seam must not run"));
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(text).Contains("Usage: kcap daemon shim");
    }

    // --- Human output (no --json) ---

    [Test]
    public async Task Ensure_human_output_states_already_on_path_without_json() {
        var (exit, _, text) = await Run([], onPath: true);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(text).Contains("already on your terminal PATH");
    }

    [Test]
    public async Task Ensure_human_refusal_prints_the_coded_reason_line() {
        var (exit, _, text) = await Run([], onPath: null, installable: true);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(text).Contains("Could not determine whether kcap is on your terminal PATH");
    }

    [Test]
    public async Task Ensure_human_installed_does_not_claim_a_fresh_link() {
        var (exit, _, text) = await Run([], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Installed, null, null)));
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(text).Contains("kcap is now on your terminal PATH");
        await Assert.That(text).DoesNotContain("Linked");
    }

    [Test]
    public async Task Ensure_human_installed_not_on_path_prints_the_actionable_detail() {
        var (exit, _, text) = await Run([], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.InstalledButNotOnPath, "Add: export PATH=\"/usr/local/bin:$PATH\"", null)));
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(text).Contains("export PATH");
    }

    /// A standard user's install edits their shell profile, so what it will do is said first.
    [Test]
    public async Task Ensure_human_install_prints_the_disclosure_before_installing() {
        var (_, _, text) = await Run([], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Installed, null, null)));

        await Assert.That(text!.IndexOf("works from any", StringComparison.Ordinal)).IsBetween(0, text.IndexOf("now on your terminal PATH", StringComparison.Ordinal));
    }


    [Test]
    public async Task Ensure_json_install_keeps_the_disclosure_off_stdout() {
        using var capture = ConsoleOutput.StartFullCapture();
        await DaemonShimCommands.Ensure(["--json"], TimeProvider.System, resolveTarget: () => "/usr/local/lib/kcap",
            probe: new FakeProbe(false), install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Installed, null, null)),
            preflight: _ => ShimPreflight.Installable, installable: true);

        await Assert.That(capture.GetCapturedError()).Contains("works from any");
        await Assert.That(JsonSerializer.Deserialize(capture.GetCapturedOutput(), ShimJsonContext.Default.ShimEnsureJson)!.Outcome)
            .IsEqualTo("installed");
    }

    [Test]
    public async Task Ensure_human_cancelled_prints_the_cancel_line() {
        var (exit, _, text) = await Run([], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Cancelled, null, null)));
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(text).Contains("canceled");
    }

    [Test]
    public async Task Ensure_human_failed_prints_detail_and_sudo_fallback() {
        var (exit, _, text) = await Run([], onPath: false, installable: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Failed, "osascript failed", "sudo ln -s ...")));
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(text).Contains("osascript failed");
        await Assert.That(text).Contains("sudo ln -s ...");
    }

    [Test]
    public async Task Dispatch_unknown_verb_prints_usage() {
        var exit = await DaemonShimCommands.DispatchAsync(["bogus"], TimeProvider.System);
        await Assert.That(exit).IsEqualTo(1);
    }

    // --- The flow's entry point: the same ladder, silently ---

    /// <summary>
    /// The loop that drives this is drawing progress dots on the same stdout, so a line written here
    /// lands mid-wait and reads as output of the flow's own. The browser gets the DTO instead.
    /// </summary>
    [Test]
    public async Task EvaluateAsync_returns_the_outcome_and_writes_nothing() {
        using var capture = ConsoleOutput.StartFullCapture();

        var result = await DaemonShimCommands.EvaluateAsync(
            TimeProvider.System,
            resolveTarget: () => "/usr/local/lib/kcap", probe: new FakeProbe(true), installable: true);

        await Assert.That(result.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.AlreadyOnPath);
        await Assert.That(capture.GetCapturedOutput() + capture.GetCapturedError()).IsEmpty();
    }

    [Test]
    public async Task EvaluateAsync_refuses_without_an_installer_with_the_platform_row() {
        var result = await DaemonShimCommands.EvaluateAsync(
            TimeProvider.System,
            resolveTarget: () => "/usr/local/lib/kcap", probe: new FakeProbe(false), installable: false);

        await Assert.That(result.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Refused);
        await Assert.That(result.Reason).IsEqualTo(FirstRunMachineActionReasons.UnsupportedPlatform);
    }

    // "We could not tell" is not "it failed", and the browser renders them differently.
    [Test]
    public async Task EvaluateAsync_refuses_an_unknown_probe_rather_than_installing() {
        var result = await DaemonShimCommands.EvaluateAsync(
            TimeProvider.System,
            resolveTarget: () => "/usr/local/lib/kcap", probe: new FakeProbe(null), installable: true,
            install: (_, _) => throw new InvalidOperationException("must not install on an unknown probe"));

        await Assert.That(result.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Refused);
        await Assert.That(result.Reason).IsEqualTo(FirstRunMachineActionReasons.ProbeUnknown);
    }

    // Exit 0 only when the terminal now resolves kcap. Derived from the outcome, so the verb's exit
    // code and the browser's copy cannot come apart.
    [Test]
    [Arguments(FirstRunMachineActionOutcomes.AlreadyOnPath,      0)]
    [Arguments(FirstRunMachineActionOutcomes.Installed,          0)]
    [Arguments(FirstRunMachineActionOutcomes.InstalledNotOnPath, 1)]
    [Arguments(FirstRunMachineActionOutcomes.Cancelled,          1)]
    [Arguments(FirstRunMachineActionOutcomes.Failed,             1)]
    [Arguments(FirstRunMachineActionOutcomes.Refused,            1)]
    public async Task The_exit_code_follows_the_outcome(string outcome, int expected) {
        var dto = new ShimEnsureJson(
            DaemonShimCommands.Capability, "/usr/local/lib/kcap", true, null, "none", outcome);

        await Assert.That(dto.ExitCode).IsEqualTo(expected);
    }

    // The document is the contract; a derived convenience must not appear in it.
    [Test]
    public async Task The_exit_code_is_not_a_field_of_the_json_document() {
        var json = JsonSerializer.Serialize(
            new ShimEnsureJson(DaemonShimCommands.Capability, null, null, null, "none",
                FirstRunMachineActionOutcomes.Refused, FirstRunMachineActionReasons.NoCliPath),
            ShimJsonContext.Default.ShimEnsureJson);

        await Assert.That(json).DoesNotContain("exit_code");
    }

    /// <summary>
    /// The live flow lane advertises the shim and nothing else.
    ///
    /// <para><b>The daemon service is the absence that matters.</b> Its unit bakes settings the steps after
    /// this leg have not written, so it is performed at the end of setup — and an unadvertised capability
    /// is left outstanding by the loop rather than reported, which is the state the screen renders as
    /// having asked.</para>
    /// </summary>
    [Test]
    public async Task The_flow_host_advertises_only_the_capability_it_can_perform_mid_flow() {
        await Assert.That(new SetupMachineActions(TimeProvider.System).Capabilities).IsEquivalentTo(
            new[] { FirstRunMachineCapabilities.PathShim });
    }

    [Test]
    public async Task The_flow_host_refuses_a_capability_it_never_advertised() {
        // The loop filters on Capabilities, so reaching here is a programming error rather than a
        // server sending something new — which is why it throws instead of reporting an outcome.
        await Assert.That(async () =>
                await new SetupMachineActions(TimeProvider.System).PerformAsync("reboot_the_laptop", CancellationToken.None))
            .Throws<ArgumentOutOfRangeException>();
    }
}
