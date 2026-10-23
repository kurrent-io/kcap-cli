using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.App.Services.Onboarding;

/// <summary>
/// The façade's progress sink, re-published as events on the UI scheduler.
/// <paramref name="post"/> is <c>Dispatcher.UIThread.Post</c> in production and an immediate
/// invoke in tests — every event crosses it, because the flows raise them from their own threads.
/// </summary>
public sealed class UiAuthProgress(Action<Action> post) : IAuthProgress {
    public event Action<string>?         NoticeReceived;
    public event Action<string>?         ErrorReceived;
    public event Action<string>?         BrowserOpened;
    public event Action<string, string, bool>? DeviceCodeReceived;
    public event Action?                 PollTicked;

    /// A discovered workspace is still being created; sign-in waits for it rather than leaving it out.
    public event Action<string>?         WorkspaceProvisioning;

    public void Notice(string message) => post(() => NoticeReceived?.Invoke(message));

    public void Error(string message) => post(() => ErrorReceived?.Invoke(message));

    public void BrowserOpening(string url) => post(() => BrowserOpened?.Invoke(url));

    // `provider` is deliberately dropped: it exists to name who is asking in a sentence this host
    // does not render. `prefilled` is not - it changes whether the code is typed or checked.
    public void DeviceCode(string code, string verificationUri, string? provider, bool prefilled) =>
        post(() => DeviceCodeReceived?.Invoke(code, verificationUri, prefilled));

    public void PollTick() => post(() => PollTicked?.Invoke());

    public void ProvisioningWait(string workspace) => post(() => WorkspaceProvisioning?.Invoke(workspace));

    string? _failedWorkspace;

    /// kcap.ai stopped creating a discovered workspace, or it is no longer this account's. Held rather than
    /// posted, so the attempt's own result can never be handled before it.
    public void ProvisioningFailed(string workspace) => Volatile.Write(ref _failedWorkspace, workspace);

    /// The workspace the last attempt saw fail to provision, or null; reading clears it.
    public string? TakeProvisioningFailure() => Interlocked.Exchange(ref _failedWorkspace, null);
}

/// <summary>
/// The tenant pick as a UI round trip: <see cref="PickAsync"/> publishes the tenants and parks on
/// a completion source the view resolves. A null selection is the user backing out, which this
/// reports itself — per <see cref="ITenantPicker"/>, discovery adds no line of its own.
/// </summary>
public sealed class WizardTenantPicker(IAuthProgress progress) : ITenantPicker {
    readonly Lock _gate = new();

    TaskCompletionSource<DiscoveredTenant?>? _pending;

    /// Raised on the flow's own thread; the view model marshals it.
    public event Action<DiscoveredTenant[]>? SelectionRequested;

    public DiscoveredTenant? Pick(DiscoveredTenant[] tenants) =>
        throw new NotSupportedException("The wizard picker is asynchronous — the façade consumes PickAsync.");

    public async Task<DiscoveredTenant?> PickAsync(DiscoveredTenant[] tenants, TenantPickContext context, CancellationToken ct) {
        var pending = new TaskCompletionSource<DiscoveredTenant?>(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<DiscoveredTenant?>? displaced;

        lock (_gate) {
            displaced = _pending;
            _pending  = pending;
        }

        // A displaced pick has no view left to resolve it — end it rather than orphan its await.
        displaced?.TrySetCanceled(CancellationToken.None);

        using var registration = ct.Register(() => pending.TrySetCanceled(ct));

        SelectionRequested?.Invoke(tenants);

        try {
            var picked = await pending.Task.ConfigureAwait(false);

            if (picked is null) progress.Error("No tenant selected.");

            return picked;
        } finally {
            lock (_gate) {
                if (ReferenceEquals(_pending, pending)) _pending = null;
            }
        }
    }

    public void Select(DiscoveredTenant? tenant) {
        TaskCompletionSource<DiscoveredTenant?>? pending;

        lock (_gate) {
            pending  = _pending;
            _pending = null;
        }

        pending?.TrySetResult(tenant);
    }
}

/// The bridge set one wizard run shares; the composition root builds the façade over exactly
/// these. The sink is built from this object's own <see cref="Post"/>, so a bridge marshalling
/// through a different dispatcher than the rest is not representable.
public sealed class WizardBridges {
    public WizardBridges(Action<Action> post, CliTelemetry telemetry, AuthEndpoints endpoints) {
        Post      = post;
        Telemetry = telemetry;
        Endpoints = endpoints;
        Progress  = new UiAuthProgress(post);
        Picker    = new WizardTenantPicker(Progress);
    }

    public Action<Action>     Post      { get; }
    public CliTelemetry       Telemetry { get; }
    public AuthEndpoints      Endpoints { get; }
    public UiAuthProgress     Progress  { get; }
    public WizardTenantPicker Picker    { get; }
}

/// <summary>
/// The one mapping from a Connect intent to a façade call, used by the composition root to build
/// <see cref="WizardAuthService"/>'s operation. Wizard sign-ins adopt the server — that is what
/// carries <see cref="OnboardingGate"/> to Complete.
/// </summary>
public static class WizardSignInOperation {
    /// <param name="funnel">Where a paste sign-in's outcome is recorded; discovery records its own.</param>
    public static Func<ConnectIntent, CancellationToken, Task<AuthResult>> For(
            OnboardingFacade facade, string profile, CommitPrecondition? precondition = null, SetupFunnel? funnel = null) =>
        async (intent, ct) => intent switch {
            ConnectIntent.Paste paste => Recorded(await facade.LoginAsync(
                ResolveServer(paste.ServerInput), forceDevice: false, profile, ct, adoptServer: true, precondition: precondition), funnel),
            ConnectIntent.Discover discover =>
                await facade.DiscoverAsync(AuthProvider.WorkOS, discover.ForceDevice, ct, discover.ConfirmPick),
            _ => new AuthResult.Failed("No connection was chosen.")
        };

    /// A paste outcome as the CLI's funnel counts one: only a real sign-in completes and only a refused one
    /// fails. A server that could not be reached, that needs no sign-in, or whose credential could not be
    /// saved records nothing: the wizard does not count that last one as signed in either.
    internal static AuthResult Recorded(AuthResult result, SetupFunnel? funnel) {
        switch (result) {
            case AuthResult.Committed { Provider: not AuthProvider.None, CredentialSaved: true } committed:
                funnel?.SigninCompleted(committed.Provider);
                break;
            case AuthResult.Failed { Reason: AuthFailureReason.SigninDenied }:
                funnel?.SigninFailed("signin_denied");
                break;
        }

        return result;
    }

    /// <summary>
    /// Origin first, then slug expansion: a pasted page URL must lose its path before
    /// <see cref="ServerInput.ResolveTenantArg"/> decides it already looks like a host. A
    /// scheme-less host or host:port then gets Core's own scheme rule — the pure half of the
    /// normalizer the CLI probes with, so a loopback server lands on http here too.
    /// </summary>
    public static string ResolveServer(string input) {
        var resolved = ServerInput.ResolveTenantArg(ServerInput.ToServerOrigin(input));

        return HostOnly(resolved) ? ServerUrlNormalizer.WithLoopbackDefault(resolved) : resolved;
    }

    // A host or host:port (bracketed IPv6 included); anything naming an unusable scheme ("file:")
    // is left alone to fail the server-URL validator.
    static bool HostOnly(string value) {
        var bracketEnd = value.StartsWith('[') ? value.IndexOf(']') : -1;
        var colon      = value.IndexOf(':', bracketEnd > 0 ? bracketEnd + 1 : 0);

        return colon < 0 || (colon + 1 < value.Length && value[(colon + 1)..].All(char.IsAsciiDigit));
    }
}
