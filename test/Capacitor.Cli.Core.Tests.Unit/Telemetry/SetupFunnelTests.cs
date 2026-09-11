using Capacitor.Cli.Core.Auth;
using NSubstitute;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

public class SetupFunnelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    TelemetryProbe StartCapturing() => TelemetryProbe.Live("setup", Config.Root);

    [Test]
    public async Task Happy_path_emits_the_full_sequence() {
        var probe = StartCapturing();

        probe.Telemetry.Funnel.Started(hasExistingProfile: false, serverUrlProvided: false, noPrompt: false);
        probe.Telemetry.Funnel.SigninOpened("browser", "workos");
        probe.Telemetry.Funnel.SigninCompleted("workos");
        probe.Telemetry.Funnel.TenantNone("workos");
        probe.Telemetry.Funnel.WorkspaceOffered();
        probe.Telemetry.Funnel.WorkspaceRequested();
        probe.Telemetry.Funnel.WorkspaceProvisioned();
        probe.Telemetry.Funnel.Succeeded(agentsConfigured: 3);

        // CollectionOrdering.Matching: IsEquivalentTo defaults to set comparison (any order) and
        // would pass even on a transposed sequence — this is exactly the funnel's ordering that
        // matters (a PostHog ordered funnel converts on step order, not just step presence).
        await Assert.That(probe.Names).IsEquivalentTo(new[] {
            "cli_setup_started", "cli_setup_signin_opened", "cli_setup_signin_completed",
            "cli_setup_tenant_none", "cli_setup_workspace_offered", "cli_setup_workspace_requested",
            "cli_setup_workspace_provisioned", "cli_setup_succeeded",
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Abandoned_at_signup_stops_after_the_offer() {
        var probe = StartCapturing();

        probe.Telemetry.Funnel.Started(false, false, false);
        probe.Telemetry.Funnel.SigninCompleted("workos");
        probe.Telemetry.Funnel.TenantNone("workos");
        probe.Telemetry.Funnel.WorkspaceOffered();
        probe.Telemetry.Funnel.WorkspaceDeclined();

        await Assert.That(probe.Events[^1].Name).IsEqualTo("cli_setup_workspace_declined");
        await Assert.That(probe.Names.Any(n => n == "cli_setup_succeeded")).IsFalse();
    }

    [Test]
    public async Task Provisioning_failure_carries_a_reason() {
        var probe = StartCapturing();

        probe.Telemetry.Funnel.WorkspaceFailed("slug_taken");

        await Assert.That(probe.Events[^1].Name).IsEqualTo("cli_setup_workspace_failed");
        await Assert.That(probe.Events[^1].Properties["reason"]!.GetValue<string>()).IsEqualTo("slug_taken");
    }

    [Test]
    public async Task Started_carries_its_entry_conditions() {
        var probe = StartCapturing();

        // Mixed, not all-true: all-true would pass under a transposed mapping (e.g.
        // ["no_prompt"] = serverUrlProvided) just as easily as the correct one.
        probe.Telemetry.Funnel.Started(hasExistingProfile: true, serverUrlProvided: false, noPrompt: true);

        var props = probe.Events[0].Properties;
        await Assert.That(props["has_existing_profile"]!.GetValue<bool>()).IsTrue();
        await Assert.That(props["server_url_provided"]!.GetValue<bool>()).IsFalse();
        await Assert.That(props["no_prompt"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Succeeded_reports_a_count_not_vendor_names() {
        var probe = StartCapturing();

        probe.Telemetry.Funnel.Succeeded(agentsConfigured: 4);

        await Assert.That(probe.Events[^1].Properties["agents_configured"]!.GetValue<int>()).IsEqualTo(4);
    }

    // Guards collisions with events OTHER producers own: the server's own cli_setup_completed,
    // and cli_auth_return, which kcap-web's Worker emits and the CLI never does. Two producers
    // sharing a name would double-count across two different persons.
    [Test]
    public async Task No_funnel_event_collides_with_a_server_event_name() {
        string[] serverEvents = [
            "user_registered", "user_logged_in", "cli_setup_completed", "session_ingest_started",
            "session_ingest_ended", "eval_ran", "fact_retained", "daemon_connected",
            "daemon_disconnected", "hosted_agent_started", "hosted_agent_ended",
            "cli_auth_return",
        ];

        var probe = StartCapturing();
        probe.Telemetry.Funnel.Started(false, false, false);
        probe.Telemetry.Funnel.SigninOpened("browser", "workos");
        probe.Telemetry.Funnel.SigninCompleted("workos");
        probe.Telemetry.Funnel.SigninFailed("timeout");
        probe.Telemetry.Funnel.TenantNone("workos");
        probe.Telemetry.Funnel.WorkspaceOffered();
        probe.Telemetry.Funnel.WorkspaceDeclined();
        probe.Telemetry.Funnel.WorkspaceRedirected();
        probe.Telemetry.Funnel.WorkspaceRequested();
        probe.Telemetry.Funnel.WorkspaceProvisioned();
        probe.Telemetry.Funnel.WorkspaceFailed("poll_timeout");
        probe.Telemetry.Funnel.Succeeded(1);

        // Without this, an Emit that silently no-ops would leave the sink empty and the loop below
        // would assert nothing — a test that cannot fail. 12 = the 12 calls above.
        await Assert.That(probe.Events.Count).IsEqualTo(12);

        foreach (var e in probe.Events)
            await Assert.That(serverEvents.Contains(e.Name)).IsFalse();
    }

    // Call-site coverage, not just SetupFunnel's statics: every test above calls SetupFunnel
    // directly, so a wiring defect inside WorkOSDiscovery.DiscoverAsync itself — e.g. the original
    // bug of anchoring signin_completed/signin_failed on the overall outcome instead of the
    // live-auth result — was invisible to this suite. A zero-tenant, no-provisioner run reaches
    // the legacy "ask your admin" dead-end (NoTenants) despite sign-in having fully succeeded,
    // which is exactly the case that anchoring on the overall outcome gets wrong.
    [Test]
    public async Task WorkOSDiscovery_emits_signin_completed_before_tenant_none_for_a_zero_tenant_run() {
        var probe = StartCapturing();

        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.None)));

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(), probe.Telemetry.Funnel,
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

        // No provisioner passed -> the legacy "ask your admin" dead-end -> NoTenants, even though
        // sign-in itself worked fine.
        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.NoTenants>();

        await Assert.That(probe.Names).IsEquivalentTo(
            new[] { "cli_setup_signin_completed", "cli_setup_tenant_none" }, CollectionOrdering.Matching);
    }
}
