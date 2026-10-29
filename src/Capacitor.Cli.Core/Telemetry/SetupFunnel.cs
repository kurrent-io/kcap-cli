using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// The signup funnel: `kcap setup` -> sign-in -> "no tenant" -> workspace offer -> outcome ->
/// setup success. Every event flushes eagerly via <see cref="CliTelemetry.CaptureNow"/> EXCEPT
/// the provisioning/poll outcomes (<see cref="WorkspaceProvisioned"/>, <see cref="WorkspaceFailed"/>):
/// abandonment happens before commitment, and the population this exists to measure abandons
/// setup there and never runs kcap again, so a deferred event at that point is a lost event, not
/// a delayed one.
///
/// The two outcome events are the exception: neither can be reached by walking away from a prompt,
/// so both arrive at the process's normal exit flush. They use the batched
/// <see cref="CliTelemetry.Capture"/> path instead — they don't need the eager flush, and eager
/// flushing would block synchronously inside SpectreTenantProvisioner's interactive prompts and
/// (for the poll outcomes) inside a Spectre live-display callback, neither of which tolerates
/// blocking work without garbling what it is drawing.
///
/// <see cref="WorkspaceFailed"/> does NOT imply a preceding <see cref="WorkspaceRequested"/>: the
/// flag-driven create rejects a bad or taken slug before asking the server for anything. Those
/// carry the reason the check produced (<c>invalid</c>, <c>blocked</c>, <c>reserved</c>,
/// <c>taken</c>, <c>unavailable</c>, <c>availability_unreachable</c>), so a funnel query can tell
/// them from a failure that had a workspace on the way.
///
/// Names deliberately avoid `cli_setup_completed`, which the SERVER already emits — a second
/// producer of that name would double-count across two different persons (the CLI user and
/// whoever the server attributes the event to).
/// </summary>
public sealed class SetupFunnel(CliTelemetry telemetry) {
    public void Started(bool hasExistingProfile, bool serverUrlProvided, bool noPrompt) =>
        Emit("cli_setup_started", new JsonObject {
            ["has_existing_profile"] = hasExistingProfile,
            ["server_url_provided"]  = serverUrlProvided,
            ["no_prompt"]            = noPrompt,
        });

    /// <summary>The edition choice came up, as the app wizard's first screen or in a bare interactive
    /// <c>kcap setup</c>; the denominator for the edition branch.</summary>
    public void EditionOffered() => Emit("cli_setup_edition_offered", new JsonObject());

    /// <summary>
    /// The funnel's first branch: <c>local</c>, <c>cloud</c> or <c>existing_workspace</c>. A run that
    /// goes Back and picks again emits again, so a breakdown reads the last choice before each later step.
    /// </summary>
    public void EditionChosen(string edition) =>
        Emit("cli_setup_edition_chosen", new JsonObject { ["edition"] = edition });

    /// <summary>The app is about to spawn <c>kcap setup --local</c>; one per attempt, Try again included,
    /// so an attempt whose spawn fails still counts.</summary>
    public void LocalInstallStarted() => Emit("cli_setup_local_install_started", new JsonObject());

    /// <summary>A run settled installed: <c>fresh</c>, <c>reused</c> or <c>recovered</c>.</summary>
    public void LocalInstallCommitted(string plan) =>
        Emit("cli_setup_local_install_committed", new JsonObject { ["plan"] = plan });

    /// <summary>A run settled failed, other than by the app quitting. <paramref name="code"/> is the CLI's
    /// failure code, else <c>cli_missing</c>, <c>spawn_failed</c> or <c>no_failure_line</c>;
    /// <paramref name="locked"/> means the wizard can no longer go back to the edition choice.</summary>
    public void LocalInstallFailed(string code, bool locked) =>
        Emit("cli_setup_local_install_failed", new JsonObject { ["code"] = code, ["locked"] = locked });

    public void LocalInstallRetried() => Emit("cli_setup_local_install_retried", new JsonObject());

    /// <summary>The desktop wizard put a page on screen; one per entry, Back included, so a drop-off reads
    /// as the last page shown.</summary>
    public void StepShown(string step) => Emit("cli_setup_step_shown", new JsonObject { ["step"] = step });

    /// <summary>The desktop wizard's daemon came up enabled, the first time it did in that run;
    /// <paramref name="alreadyRunning"/> when it was enabled before the run and setup changed nothing.</summary>
    public void DaemonEnabled(bool alreadyRunning) =>
        Emit("cli_setup_daemon_enabled", new JsonObject { ["already_running"] = alreadyRunning });

    /// <summary>The desktop wizard closed, on <paramref name="step"/>; <paramref name="finished"/> when it
    /// closed from its last page.</summary>
    public void WizardClosed(string step, bool finished) =>
        Emit("cli_setup_wizard_closed", new JsonObject { ["step"] = step, ["finished"] = finished });

    public void SigninOpened(string mode, string provider) =>
        Emit("cli_setup_signin_opened", new JsonObject { ["mode"] = mode, ["provider"] = provider });

    public void SigninCompleted(string provider) =>
        Emit("cli_setup_signin_completed", new JsonObject { ["provider"] = provider });

    public void SigninFailed(string reason) =>
        Emit("cli_setup_signin_failed", new JsonObject { ["reason"] = reason });

    /// <summary>
    /// The denominator for "reached signup": the user authenticated but has no tenant. The single
    /// most important event in the feature.
    /// </summary>
    public void TenantNone(string provider) =>
        Emit("cli_setup_tenant_none", new JsonObject { ["provider"] = provider });

    public void WorkspaceOffered()   => Emit("cli_setup_workspace_offered", new JsonObject());
    public void WorkspaceDeclined()  => Emit("cli_setup_workspace_declined", new JsonObject());
    public void WorkspaceRequested() => Emit("cli_setup_workspace_requested", new JsonObject());

    /// <summary>
    /// Terminal event for the "I already have a workspace" branch: the user was offered a new
    /// workspace and instead redirected setup at one they already have. Deliberately distinct
    /// from <see cref="WorkspaceDeclined"/> — this is not abandonment, setup continues against a
    /// different server (see <c>AuthResult.Retarget</c>) and may still
    /// reach <see cref="Succeeded"/>. Fires before any commitment to THIS offer (no
    /// <see cref="WorkspaceRequested"/> ever follows it), so it stays on the eager path.
    /// </summary>
    public void WorkspaceRedirected() => Emit("cli_setup_workspace_redirected", new JsonObject());

    public void WorkspaceProvisioned() => EmitDeferred("cli_setup_workspace_provisioned", new JsonObject());

    public void WorkspaceFailed(string reason) =>
        EmitDeferred("cli_setup_workspace_failed", new JsonObject { ["reason"] = reason });

    /// <param name="agentsConfigured">
    /// A count, not vendor names — how many coding-agent integrations were installed, not which
    /// ones. Keeping vendor identity out of this event avoids a growing set of per-vendor
    /// properties every time a new agent is supported.
    /// </param>
    public void Succeeded(int agentsConfigured) =>
        Emit("cli_setup_succeeded", new JsonObject { ["agents_configured"] = agentsConfigured });

    void Emit(string name, JsonObject props)         => telemetry.CaptureNow(name, props);
    void EmitDeferred(string name, JsonObject props) => telemetry.Capture(name, props);
}
