using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Duende.IdentityModel.OidcClient.Browser;
using Config_Profile = Capacitor.Cli.Core.Config.Profile;

namespace Capacitor.Cli.Core.Auth;

/// <summary>One durable publication set: the config mutation and provider stamp, followed by token writes tracked so a partial failure is known.</summary>
sealed record CommitRequest(
    IReadOnlyList<AuthIdentity>         Identities,
    string                              Provider,
    string                              ActiveProfile,
    string                              CanonicalServer,
    Func<ProfileConfig, ProfileConfig>? ConfigMutation,
    // Its callback marks the ACTIVE profile's credential landed; another profile's save does not count.
    Func<Action, Task<string?>>?        PublishTokens,
    // False for a login that must not claim the profile for this server (see LoginTarget.Foreign).
    bool                                WriteStamp = true,
    CommitPrecondition?                 Precondition = null,
    // Publishes the config durably (flushed, directories synced); implies the strict read.
    bool                                Durable = false);

/// <summary>The ordered commit boundary: the before-commit hook is the last cancellable await, after which every publication is uncancellable.</summary>
static class CommitBoundary {
    internal static async Task<AuthResult> CommitAsync(
            ConfigRoot                                                  root,
            CommitRequest                                               request,
            Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? beforeCommit,
            IAuthProgress                                               progress,
            CancellationToken                                           ct) =>
        (await CommitOnceAsync(root, request, beforeCommit, progress, stillNamed: null, ct))!;

    const int NamingAttempts = 3;

    /// <summary>
    /// Commits discovered tenants under the profile names the config gives them. The names are
    /// chosen from a read taken before the lock, so the commit re-checks them against the config it
    /// writes and, when a profile written in between changed them, prepares the request again.
    /// </summary>
    internal static async Task<AuthResult> CommitNamedAsync(
            ConfigRoot                                                  root,
            DiscoveredTenant[]                                          tenants,
            DiscoveredTenant                                            picked,
            Func<DiscoveredTenant[], DiscoveredTenant, CommitRequest>   prepare,
            Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? beforeCommit,
            IAuthProgress                                               progress,
            CancellationToken                                           ct) {
        for (var attempt = 0; attempt < NamingAttempts; attempt++) {
            var snapshot = ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(root), out var current) ? current : new ProfileConfig();
            var (named, namedPicked) = TenantDiscovery.NameProfiles(snapshot, tenants, picked);

            var result = await CommitOnceAsync(
                root, prepare(named, namedPicked), beforeCommit, progress,
                config => TenantDiscovery.NamesHold(config, tenants, picked, named, namedPicked), ct);

            if (result is not null) return result;
        }

        const string changed = "the profiles kept changing during sign-in; nothing saved.";
        progress.Error($"Error: {changed}");

        return new AuthResult.Failed(changed);
    }

    /// <summary>Null when <paramref name="stillNamed"/> refused the config under the lock: nothing
    /// was written and the caller may prepare the request again.</summary>
    static async Task<AuthResult?> CommitOnceAsync(
            ConfigRoot                                                  root,
            CommitRequest                                               request,
            Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? beforeCommit,
            IAuthProgress                                               progress,
            Func<ProfileConfig, bool>?                                  stillNamed,
            CancellationToken                                           ct) {
        if (ct.IsCancellationRequested) return new AuthResult.Cancelled();

        if (beforeCommit is not null) {
            try {
                await beforeCommit(request.Identities, ct);
            } catch (OperationCanceledException) {
                return new AuthResult.Cancelled();
            } catch (Exception ex) {
                progress.Error($"Error: sign-in could not be prepared: {ex.Message}");

                return new AuthResult.Failed(ex.Message);
            }
        }

        // A token-only arm has no config commit to make the boundary durable, so what landed is tracked rather than assumed.
        var configPublished = request.ConfigMutation is not null || request.WriteStamp || request.Precondition is not null;
        var renamed         = false;

        if (configPublished) {
            try {
                ProfileConfig Mutation(ProfileConfig config) {
                    if (stillNamed?.Invoke(config) == false) {
                        renamed = true;

                        return config;
                    }

                    request.Precondition?.Check(config, request.ActiveProfile);
                    // Profile write and stamp are ONE mutation: no window where a profile exists unstamped.
                    return Stamp(request.ConfigMutation?.Invoke(config) ?? config, request);
                }

                // A precondition is decided on what the file says, so an unreadable file must abort
                // rather than be read as a fresh default and then published over.
                if (request.Durable) await ConfigMutator.MutateStrictAsync(root, Mutation, durable: true, ct: CancellationToken.None);
                else if (request.Precondition is null) await ConfigMutator.MutateAsync(root, Mutation, CancellationToken.None);
                else await ConfigMutator.MutateStrictAsync(root, Mutation, CancellationToken.None);
            } catch (CommitPreconditionFailedException ex) {
                progress.Error($"Error: {ex.Message}");

                return new AuthResult.Failed(ex.Message);
            } catch (ConfigUnreadableException) {
                progress.Error("Error: sign-in could not be saved: the configuration file could not be read.");

                return new AuthResult.Failed("config unreadable");
            } catch (Exception ex) {
                // A durable publication's directory sync can throw after the rename, so a Failed commit may already be visible; the caller retries.
                progress.Error($"Error: sign-in could not be saved: {ex.Message}");

                return new AuthResult.Failed(ex.Message);
            }

            if (renamed) return null;
        }

        string? username   = null;
        var     tokenSaved = false;

        try {
            if (request.PublishTokens is not null) username = await request.PublishTokens(() => tokenSaved = true);
        } catch (Exception ex) {
            // Nothing durable exists, so Committed would be a lie — fail honestly instead.
            if (!configPublished && !tokenSaved) {
                progress.Error($"Error: sign-in could not be saved: {ex.Message}");

                return new AuthResult.Failed(ex.Message);
            }

            // Past a landed publication the boundary has begun; report what was lost rather than a torn stop.
            progress.Error($"Error: some credentials could not be saved: {ex.Message}");
        }

        return new AuthResult.Committed(
            request.ActiveProfile, request.CanonicalServer, request.Provider, username, request.Identities,
            CredentialSaved: request.PublishTokens is null || tokenSaved);
    }

    static ProfileConfig Stamp(ProfileConfig config, CommitRequest request) {
        if (!request.WriteStamp) return config;

        var profiles = new Dictionary<string, Config_Profile>(config.Profiles);

        foreach (var identity in request.Identities) {
            var profile = profiles.GetValueOrDefault(identity.Profile) ?? new Config_Profile();
            profiles[identity.Profile] = profile with {
                AuthProvider = new AuthProviderStamp(request.Provider, identity.CanonicalServer)
            };
        }

        return config with { Profiles = profiles };
    }

    /// Every writer of <c>active_profile</c> settles the outgoing profile's legacy credential first,
    /// so the name the legacy file follows never moves away from it. Null on success, else the line
    /// to report.
    internal static async Task<string?> SettleLegacyCredentialAsync(ConfigRoot root, TokenStore store, CancellationToken ct) {
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(root), out var before))
            return "Error: the configuration file could not be read.";
        try {
            await store.MigrateLegacyAsync(before.ActiveName, ct);
            return null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or ArgumentException) {
            return $"Error: could not move the saved sign-in of profile '{before.ActiveName}': {ex.Message}";
        }
    }

    /// <summary>Points a profile at the server only when it doesn't already name the same one.</summary>
    internal static ProfileConfig PointProfileAtServer(ProfileConfig config, string profileName, string serverUrl) {
        var existing = config.Profiles.GetValueOrDefault(profileName);

        if (existing is not null && ServerIdentity.SameServer(existing.ServerUrl, serverUrl)) return config;

        return config with {
            Profiles = new Dictionary<string, Config_Profile>(config.Profiles) {
                [profileName] = (existing ?? new Config_Profile()) with { ServerUrl = AppConfig.NormalizeUrl(serverUrl) }
            }
        };
    }
}

/// <summary>
/// What a known-server login may write. The stamp claims the profile for this server, so it is
/// written only when the profile already names it or the caller opted to adopt it; adopting also
/// writes <c>server_url</c>. A foreign profile with no adoption gets neither.
/// </summary>
sealed record LoginTarget(
    string Profile, string CanonicalServer, string ServerUrl, bool PointsAtServer, bool AdoptServer,
    bool ExistedAtRead = true, CommitPrecondition? Precondition = null) {
    internal bool Adopting  => !PointsAtServer && AdoptServer;
    internal bool Foreign   => !PointsAtServer && !AdoptServer;
    internal bool WriteStamp => !Foreign;

    internal Func<ProfileConfig, ProfileConfig>? ConfigMutation =>
        Adopting ? config => CommitBoundary.PointProfileAtServer(config, Profile, ServerUrl) : null;

    /// Evaluated under the profile's token lock: the profile must still exist, and with a
    /// precondition still name the server. A foreign login for a profile that never existed has
    /// nothing that could have been removed, so it saves unguarded.
    internal Func<ProfileConfig, bool>? SaveGuard =>
        Foreign && !ExistedAtRead
            ? null
            : config => config.Profiles.TryGetValue(Profile, out var existing)
                     && (Precondition is null || ServerIdentity.SameServer(existing.ServerUrl, ServerUrl));
}

/// <summary>
/// GUI-neutral onboarding operations over the existing auth flows: sign in to a known server, or
/// discover and join a tenant. Every step up to the commit boundary is cancellable and renders
/// through <paramref name="progress"/> — nothing here touches the console.
/// </summary>
/// <param name="launcher">
/// Opens the sign-in page for the device grant and the loopback flow. Required rather than
/// defaulted to the system browser: the flows below reach it on every interactive route, so a
/// caller that says nothing would open a page — under test, on the developer's machine.
/// </param>
/// <param name="beforeCommit">
/// Runs with every identity the boundary is about to publish, before anything durable exists;
/// throwing aborts the operation with nothing written (the caller may retry).
/// </param>
/// <param name="httpFactory">
/// Draws the anonymous lane for the legs that carry no credential of ours — the auth-config read and
/// the token exchange. Drawn per operation: a client taken at construction would freeze the handler
/// it was built with.
/// </param>
public sealed class OnboardingFacade(
        ConfigRoot                                                  root,
        TokenStore                                                  store,
        IHttpClientFactory                                          httpFactory,
        IAuthProxyClient                                            proxy,
        GitHubOAuthClient                                           github,
        WorkOSClient                                                workos,
        IAuthProgress                                               progress,
        IBrowserLauncher                                            launcher,
        ITenantPicker                                               picker,
        ITenantProvisioner?                                         provisioner,
        CliTelemetry                                                telemetry,
        AuthEndpoints                                               endpoints,
        TimeProvider                                                time,
        Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? beforeCommit) {
    /// <summary>Test seam for the one WorkOS effect with no HTTP surface (loopback browser + OidcClient).</summary>
    internal Func<CancellationToken, Task<WorkOSAuthResponse?>>? WorkOSOrglessLogin { get; init; }

    /// <summary>Test seams for the known-server WorkOS login, whose OidcClient leg bypasses the http factory.</summary>
    internal IBrowser? WorkOSBrowser { get; init; }

    /// <summary>
    /// Escape-hatch keyboard. Defaults to none for the same reason <see cref="IAuthProgress"/> is
    /// injected rather than defaulted: a GUI host has no console, and only a terminal host knows it
    /// does. <c>Capacitor.Cli</c> supplies <see cref="ConsoleKeyWatcher"/>.
    /// </summary>
    internal IKeyWatcher KeyWatcher { get; init; } = NoKeyWatcher.Instance;

    /// <summary>
    /// Asks each discovered WorkOS workspace whether it is still there, given the org-less sign-in's bearer.
    /// Only <see cref="WorkspaceAnswer.Gone"/> and <see cref="WorkspaceAnswer.Pending"/> leave one out; an outage keeps it. Unset, every listed
    /// workspace is offered.
    /// </summary>
    public Func<DiscoveredTenant, string, CancellationToken, Task<WorkspaceAnswer>>? ProbeWorkspace { get; init; }

    /// <param name="adoptServer">
    /// When the profile doesn't already name this server: true writes its <c>server_url</c> and the
    /// provider stamp, false leaves config untouched (a <c>None</c> server then has nothing to sign in with).
    /// </param>
    /// <param name="precondition">
    /// What must still hold about the profile when config is written: a profile removed or repointed
    /// while the browser was open is refused rather than committed over.
    /// </param>
    public Task<AuthResult> LoginAsync(
            string serverUrl, bool forceDevice, string profile, CancellationToken ct, bool adoptServer = false,
            CommitPrecondition? precondition = null) =>
        GuardAsync(() => LoginCoreAsync(serverUrl, forceDevice, profile, adoptServer, precondition, ct), ct);

    /// <param name="provider"><see cref="AuthProvider.GitHubApp"/> or <see cref="AuthProvider.WorkOS"/>.</param>
    /// <param name="confirmPick">Hand even a single workspace to the picker rather than signing in to it.</param>
    public Task<AuthResult> DiscoverAsync(string provider, bool forceDevice, CancellationToken ct, bool confirmPick = false) =>
        GuardAsync(() => DiscoverCoreAsync(provider, forceDevice, confirmPick, ct), ct);

    async Task<AuthResult> LoginCoreAsync(
            string serverUrl, bool forceDevice, string profile, bool adoptServer, CommitPrecondition? precondition,
            CancellationToken ct) {
        using var http = httpFactory.CreateClient(CapacitorClients.Anonymous);

        var config = await OAuthLoginFlow.FetchAuthConfigAsync(http, serverUrl, ct, progress);

        if (config is null) return Stop($"Failed to fetch auth config from {serverUrl}/auth/config", ct);

        // Hooks read the provider from this cache for a day, keyed as CapacitorServer.Url trims it; a
        // server that changed provider would otherwise be treated as the old one until it expired.
        AuthProviderCache.Set(serverUrl.TrimEnd('/'), config.Provider, root, time);

        if (!ServerIdentity.TryCanonicalizeForStamping(serverUrl, out var canonical, out var identityError)) {
            return Fail($"Error: {identityError}", identityError, ct);
        }

        var configured = (await AppConfig.LoadProfileConfig(root, ct)).Profiles.GetValueOrDefault(profile);
        var target     = new LoginTarget(
            profile, canonical, serverUrl,
            PointsAtServer: ServerIdentity.SameServer(configured?.ServerUrl, serverUrl),
            AdoptServer: adoptServer,
            ExistedAtRead: configured is not null,
            Precondition: precondition);

        return config.Provider switch {
            AuthProvider.None      => await LoginNoneAsync(target, ct),
            AuthProvider.GitHubApp => await LoginGitHubAsync(http, config, forceDevice, target, ct),
            AuthProvider.WorkOS    => await LoginWorkOSAsync(config, forceDevice, target, ct),
            _                      => UnknownProvider(config.Provider)
        };
    }

    async Task<AuthResult> LoginNoneAsync(LoginTarget target, CancellationToken ct) {
        // Nothing to sign in with on a None server, so a profile that doesn't name it stays unconfigured.
        if (target.Foreign) {
            return Fail(
                $"Error: profile '{target.Profile}' is not configured for {target.ServerUrl} — pass --profile or run kcap setup.",
                $"Profile '{target.Profile}' is not configured for {target.ServerUrl}.", ct);
        }

        var request = new CommitRequest(
            [new AuthIdentity(target.Profile, target.CanonicalServer)], AuthProvider.None, target.Profile, target.CanonicalServer,
            ConfigMutation: target.ConfigMutation,
            PublishTokens: null,
            Precondition: target.Precondition);

        var result = await CommitBoundary.CommitAsync(root, request, beforeCommit, progress, ct);

        if (result is AuthResult.Committed) {
            progress.Notice("Server has no authentication configured — login not required.");
        }

        return result;
    }

    async Task<AuthResult> LoginGitHubAsync(
            HttpClient http, AuthDiscoveryResponse config, bool forceDevice, LoginTarget target, CancellationToken ct) {
        var accessToken = await OAuthLoginFlow.AcquireGitHubTokenAsync(
            github, config.GithubClientId!, config.GithubCodeExchangeUrl, forceDevice, launcher,
            telemetry.Join, time, ct, progress);

        if (accessToken is null) return Stop("GitHub sign-in did not complete.", ct, AuthFailureReason.SigninDenied);

        var exchanged = await OAuthLoginFlow.ExchangeAsync(
            http, target.ServerUrl, accessToken, config.Provider, target.Profile, progress, time, ct);

        if (exchanged is null) return Stop("Token exchange failed.", ct);

        return await CommitTokensAsync(exchanged.Value.Tokens, exchanged.Value.Username, config.Provider, target, ct);
    }

    async Task<AuthResult> LoginWorkOSAsync(
            AuthDiscoveryResponse config, bool forceDevice, LoginTarget target, CancellationToken ct) {
        // The loopback browser is owned by OAuthLoginFlow.AcquireWorkOSAsync, which is where the join
        // collaborator is attached — see the ownership guard, which enumerates every site.
        var apiBase = WorkOSApiBaseRule.Resolve(target.ServerUrl, config.ApiBaseUrl, out var refused);

        if (refused) progress.Error(WorkOSApiBaseRule.RefusedWarning(target.ServerUrl, config.ApiBaseUrl!));

        var authenticated = await OAuthLoginFlow.WorkOSTokensForServerAsync(
            workos, apiBase, target.ServerUrl, config.ClientId!, config.OrganizationId, forceDevice, launcher,
            telemetry.Join, WorkOSBrowser, ct, progress, time, KeyWatcher);

        if (authenticated is null) return Stop("WorkOS sign-in did not complete.", ct, AuthFailureReason.SigninDenied);

        return await CommitTokensAsync(
            authenticated.Value.Tokens, authenticated.Value.Username, AuthProvider.WorkOS, target, ct);
    }

    async Task<AuthResult> CommitTokensAsync(
            StoredTokens tokens, string? username, string provider, LoginTarget target, CancellationToken ct) {
        var request = new CommitRequest(
            [new AuthIdentity(target.Profile, target.CanonicalServer)], provider, target.Profile, target.CanonicalServer,
            ConfigMutation: target.ConfigMutation,
            PublishTokens: async saved => {
                var outcome = await store.SaveGuardedAsync(target.Profile, tokens, target.SaveGuard, CancellationToken.None);
                if (outcome == GuardedWriteOutcome.Written) saved();
                else progress.Error(outcome == GuardedWriteOutcome.ConfigUnreadable
                    ? $"Error: the configuration file could not be read; the sign-in for '{target.Profile}' was not saved."
                    : $"Error: profile '{target.Profile}' was removed or repointed during sign-in; nothing saved.");

                return username;
            },
            WriteStamp: target.WriteStamp,
            Precondition: target.Precondition);

        var result = await CommitBoundary.CommitAsync(root, request, beforeCommit, progress, ct);

        if (result is AuthResult.Committed { CredentialSaved: true }) progress.Notice($"Logged in as {username}");

        return result;
    }

    /// <summary>
    /// Sign in and report the workspaces this account can reach, without choosing one. Publishes
    /// nothing: no profile, no activation, no token, and no workspace created — the commit boundary
    /// is never entered, and no provisioner is consulted.
    ///
    /// <para>Not <see cref="DiscoverAsync"/> with a declining picker: a sole workspace is selected
    /// before any picker is asked, so that route would configure the machine in the one case a
    /// report is most needed, and an account with none would fail rather than answer.</para>
    /// </summary>
    public async Task<DiscoveryReport> DiscoverOnlyAsync(string provider, bool forceDevice, CancellationToken ct) {
        try {
            return await DiscoverOnlyCoreAsync(provider, forceDevice, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return DiscoveryReport.Cancelled(provider);
        }
    }

    async Task<DiscoveryReport> DiscoverOnlyCoreAsync(string provider, bool forceDevice, CancellationToken ct) {
        var proxyConfig = await proxy.GetConfigAsync(endpoints.ProxyUrl, ct);

        if (proxyConfig is null) return Halted(provider, "Cannot reach the Kurrent auth service.", ct);

        return provider switch {
            AuthProvider.WorkOS    => await ListWorkOSAsync(proxyConfig, forceDevice, ct),
            AuthProvider.GitHubApp => await ListGitHubAsync(proxyConfig, forceDevice, ct),
            _                      => DiscoveryReport.Failure(provider, $"Unknown auth provider '{provider}'."),
        };
    }

    async Task<DiscoveryReport> ListWorkOSAsync(
            ProxyConfigResponse proxyConfig, bool forceDevice, CancellationToken ct) {
        if (string.IsNullOrEmpty(proxyConfig.WorkOSClientId))
            return DiscoveryReport.Failure(AuthProvider.WorkOS, "This server isn't configured for WorkOS sign-in.");

        var auth = WorkOSOrglessLogin is not null
            ? await WorkOSOrglessLogin(ct)
            : await OAuthLoginFlow.AcquireWorkOSAsync(
                  workos, WorkOSClient.ApiBase, proxyConfig.WorkOSClientId, organizationId: null, forceDevice,
                  launcher, telemetry.Join, time, browser: null, ct: ct, progress: progress, keys: KeyWatcher);

        if (auth is null) return Halted(AuthProvider.WorkOS, "WorkOS sign-in failed.", ct);

        var result = await proxy.DiscoverWorkOSTenantsAsync(endpoints.ProxyUrl, auth.AccessToken, ct);

        if (result.Error != DiscoveryError.None)
            return Halted(AuthProvider.WorkOS, TenantDiscovery.Describe(result.Error, AuthProvider.WorkOS), ct);

        var (tenants, pending) = ProbeWorkspace is null
            ? (result.Tenants, [])
            : await WorkOSDiscovery.DropGoneAsync(result.Tenants, ProbeWorkspace, auth.AccessToken, progress, ct);

        // The hosted lane is the only one that can provision, and only for an account with none.
        return new DiscoveryReport(tenants, AuthProvider.WorkOS, CanCreate: tenants.Length == 0 && pending.Length == 0);
    }

    async Task<DiscoveryReport> ListGitHubAsync(
            ProxyConfigResponse proxyConfig, bool forceDevice, CancellationToken ct) {
        if (string.IsNullOrEmpty(proxyConfig.GitHubClientId))
            return DiscoveryReport.Failure(AuthProvider.GitHubApp, "Cannot reach the Kurrent auth service.");

        var accessToken = await OAuthLoginFlow.AcquireGitHubTokenAsync(
            github, proxyConfig.GitHubClientId, proxyConfig.GitHubCodeExchangeUrl, forceDevice, launcher,
            telemetry.Join, time, ct, progress);

        if (accessToken is null) return Halted(AuthProvider.GitHubApp, "GitHub sign-in did not complete.", ct);

        var result = await proxy.DiscoverTenantsAsync(endpoints.ProxyUrl, accessToken, ct);

        if (result.Error != DiscoveryError.None)
            return Halted(AuthProvider.GitHubApp, TenantDiscovery.Describe(result.Error, AuthProvider.GitHubApp), ct);

        // GitHub-App discovery has nothing to create with: a workspace arrives by having the app
        // installed on an org, so reporting that this account may create one would be a dead end.
        return new DiscoveryReport(result.Tenants, AuthProvider.GitHubApp, CanCreate: false);
    }

    // The proxy client and the sign-ins answer a cancelled request as a failed one, and reporting an
    // outage for it sends the reader to look at a service that is fine.
    static DiscoveryReport Halted(string provider, string error, CancellationToken ct) =>
        ct.IsCancellationRequested ? DiscoveryReport.Cancelled(provider) : DiscoveryReport.Failure(provider, error);

    async Task<AuthResult> DiscoverCoreAsync(string provider, bool forceDevice, bool confirmPick, CancellationToken ct) {
        var proxyConfig = await proxy.GetConfigAsync(endpoints.ProxyUrl, ct);

        if (proxyConfig is null) {
            return Fail("Cannot reach the Kurrent auth service.", ct, AuthFailureReason.Unreachable);
        }

        return provider switch {
            AuthProvider.WorkOS    => await DiscoverWorkOSAsync(proxyConfig, forceDevice, confirmPick, ct),
            AuthProvider.GitHubApp => await DiscoverGitHubAsync(proxyConfig, forceDevice, ct),
            _                      => UnknownProvider(provider)
        };
    }

    async Task<AuthResult> DiscoverWorkOSAsync(
            ProxyConfigResponse proxyConfig, bool forceDevice, bool confirmPick, CancellationToken ct) {
        var clientId = proxyConfig.WorkOSClientId ?? "";

        var flow = await WorkOSDiscovery.DiscoverAsync(
            endpoints.ProxyUrl, proxyConfig, proxy, picker, telemetry.Funnel,
            orglessLogin: () => WorkOSOrglessLogin is not null
                ? WorkOSOrglessLogin(ct)
                // Org-less: the sign-in picks the organization, and discovery reconciles it afterwards.
                : OAuthLoginFlow.AcquireWorkOSAsync(
                    workos, WorkOSClient.ApiBase, clientId, organizationId: null, forceDevice, launcher,
                    telemetry.Join, time, browser: null, ct: ct, progress: progress, keys: KeyWatcher),
            orgSwitch: (refreshToken, organizationId) =>
                workos.SwitchOrganizationAsync(WorkOSClient.ApiBase, clientId, refreshToken, organizationId, ct),
            orglessRefresh: async (refreshToken, refreshCt) =>
                (await workos.RefreshAsync(WorkOSClient.ApiBase, clientId, refreshToken, refreshCt)).Response,
            provisioner: provisioner,
            time: time,
            ct: ct,
            progress: progress,
            // Bearer and channel are filled in by discovery once the login has answered; only the
            // proxy half is knowable here.
            pickContext: new TenantPickContext(
                Proxy: proxy,
                ProxyUrl: endpoints.ProxyUrl,
                PickerVersion: proxyConfig.CliPickerVersion),
            probe: ProbeWorkspace,
            confirmPick: confirmPick);

        return flow switch {
            WorkOSDiscoveryFlow.Ready ready       => await WorkOSDiscovery.PublishAsync(
                                                        root, store, ready, progress, beforeCommit, time, ct),
            WorkOSDiscoveryFlow.Retarget retarget => new AuthResult.Retarget(retarget.ServerInput),
            WorkOSDiscoveryFlow.Failed failed     => Stop(failed.Message, ct, failed.Reason),
            _                                     => Stop("No kcap tenants are linked to your account.", ct,
                                                          AuthFailureReason.NoTenantsFound)
        };
    }

    async Task<AuthResult> DiscoverGitHubAsync(
            ProxyConfigResponse proxyConfig, bool forceDevice, CancellationToken ct) {
        if (string.IsNullOrEmpty(proxyConfig.GitHubClientId)) {
            return Fail("Cannot reach the Kurrent auth service.", ct, AuthFailureReason.Unreachable);
        }

        var accessToken = await OAuthLoginFlow.AcquireGitHubTokenAsync(
            github, proxyConfig.GitHubClientId, proxyConfig.GitHubCodeExchangeUrl, forceDevice, launcher,
            telemetry.Join, time, ct, progress);

        if (accessToken is null) return Stop("GitHub sign-in did not complete.", ct, AuthFailureReason.SigninDenied);

        var outcome = await new TenantDiscovery(proxy, picker).RunAsync(endpoints.ProxyUrl, accessToken, ct);

        if (outcome.ErrorMessage is not null) {
            var reason = outcome.NoTenantsFound ? AuthFailureReason.NoTenantsFound : AuthFailureReason.Other;

            return outcome.AlreadyReported
                ? Stop(outcome.ErrorMessage, ct, reason)
                : Fail(outcome.ErrorMessage, ct, reason);
        }

        var canonicalByOrigin = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var tenant in outcome.Tenants) {
            if (!ServerIdentity.TryCanonicalizeForStamping(tenant.Origin, out var canonical, out var identityError)) {
                return Fail($"Error: {identityError}", identityError, ct);
            }

            canonicalByOrigin[tenant.Origin] = canonical;
        }

        if (await CommitBoundary.SettleLegacyCredentialAsync(root, store, ct) is { } settleError)
            return Fail(settleError, settleError, ct);

        return await CommitBoundary.CommitNamedAsync(root, outcome.Tenants, outcome.Picked!, (tenants, picked) => new CommitRequest(
                [.. tenants.Select(t => new AuthIdentity(t.ProfileName, canonicalByOrigin[t.Origin]))],
                AuthProvider.GitHubApp, picked.ProfileName, canonicalByOrigin[picked.Origin],
                ConfigMutation: config => TenantDiscovery.MergeProfiles(config, tenants, picked),
                PublishTokens: saved => ExchangeEveryTenantAsync(tenants, picked, accessToken, saved)),
            beforeCommit, progress, ct);
    }

    // Inside the boundary: each tenant's exchange is network-then-save, and ANY failure — mapped or
    // thrown — costs that tenant its token (a per-tenant warning) rather than the whole commit. Only
    // the picked tenant's save counts as the boundary's credential: it is the active profile, and
    // another tenant's token landing says nothing about it.
    async Task<string?> ExchangeEveryTenantAsync(
            DiscoveredTenant[] tenants, DiscoveredTenant picked, string githubAccessToken, Action saved) {
        using var http = httpFactory.CreateClient(CapacitorClients.Anonymous);

        string? pickedUsername = null;

        foreach (var tenant in tenants) {
            try {
                var exchanged = await OAuthLoginFlow.ExchangeAsync(
                    http, AppConfig.NormalizeUrl(tenant.Origin), githubAccessToken, AuthProvider.GitHubApp,
                    tenant.ProfileName, progress, time, CancellationToken.None);

                if (exchanged is null) {
                    WarnExchangeFailed(tenant.ProfileName);

                    continue;
                }

                var outcome = await store.SaveGuardedAsync(
                    tenant.ProfileName, exchanged.Value.Tokens, cfg => cfg.Profiles.ContainsKey(tenant.ProfileName), CancellationToken.None);
                if (outcome != GuardedWriteOutcome.Written) {
                    WarnExchangeFailed(tenant.ProfileName);

                    continue;
                }

                if (tenant.ProfileName == picked.ProfileName) {
                    saved();
                    pickedUsername = exchanged.Value.Username;
                }
            } catch (Exception) {
                WarnExchangeFailed(tenant.ProfileName);
            }
        }

        return pickedUsername;
    }

    void WarnExchangeFailed(string profile) =>
        progress.Error($"Warning: token exchange failed for {profile}. Run 'kcap login' after switching to that profile.");

    AuthResult.Failed UnknownProvider(string provider) {
        progress.Error($"Error: Unknown auth provider '{provider}'. Update your kcap CLI.");

        return new AuthResult.Failed($"Unknown auth provider '{provider}'");
    }

    static async Task<AuthResult> GuardAsync(Func<Task<AuthResult>> operation, CancellationToken ct) {
        try {
            return await operation();
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return new AuthResult.Cancelled();
        }
    }

    // A pre-boundary failure under a live cancel IS a cancel: the proxy client and the WorkOS
    // refresh map OperationCanceledException onto their own failure results.
    static AuthResult Stop(string message, CancellationToken ct, AuthFailureReason reason = AuthFailureReason.Other) =>
        ct.IsCancellationRequested ? new AuthResult.Cancelled() : new AuthResult.Failed(message, reason);

    // Same rule for a façade-rendered failure, with the line suppressed under a live cancel: a user
    // who cancelled must not be shown a transport error the cancel itself caused.
    AuthResult Fail(string message, CancellationToken ct, AuthFailureReason reason = AuthFailureReason.Other) =>
        Fail(message, message, ct, reason);

    AuthResult Fail(string rendered, string message, CancellationToken ct, AuthFailureReason reason = AuthFailureReason.Other) {
        if (ct.IsCancellationRequested) return new AuthResult.Cancelled();

        progress.Error(rendered);

        return new AuthResult.Failed(message, reason);
    }
}
