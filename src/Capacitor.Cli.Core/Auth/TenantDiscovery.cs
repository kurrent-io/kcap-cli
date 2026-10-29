using System.Collections.Frozen;
using System.Globalization;
using Capacitor.Cli.Core.Config;
using Config_Profile = Capacitor.Cli.Core.Config.Profile;

namespace Capacitor.Cli.Core.Auth;

/// <param name="NoTenantsFound">
/// True only for the GENUINE "authenticated, zero tenants" case — never for a discovery-service
/// failure (proxy unreachable, token rejected, upstream error), which also returns an empty
/// <paramref name="Tenants"/> array and a non-null <paramref name="ErrorMessage"/>. Both shapes
/// look identical from <see cref="Tenants"/>.Length alone, so a caller emitting
/// telemetry off that length was inflating the funnel's key "reached signup" denominator with
/// discovery outages that have nothing to do with signup.
/// </param>
public sealed record DiscoveryOutcome(
    DiscoveredTenant[] Tenants,
    DiscoveredTenant?  Picked,
    string?            ErrorMessage,
    bool               NoTenantsFound = false,
    // The picker owns its own null messaging, so the caller carries this message without rendering it.
    bool               AlreadyReported = false
);

public interface ITenantPicker {
    DiscoveredTenant?       Pick(DiscoveredTenant[] tenants);

    /// <summary>
    /// Null means no tenant was chosen AND the picker has already told the user why — discovery adds
    /// no line of its own. The two reasons need different follow-ups (a user backing out has nothing
    /// left to do; a session that cannot prompt needs to be told how to name one), and only the
    /// picker knows which happened.
    /// </summary>
    /// <param name="context">
    /// What a picker needs beyond the rows — the bearer, the proxy client and whether a browser is
    /// reachable. Carried as an argument rather than set on the instance: the seam is built in
    /// <c>SetupCommand</c> before any login exists, and a "set this first" contract is one a caller
    /// can silently skip.
    /// </param>
    Task<DiscoveredTenant?> PickAsync(DiscoveredTenant[] tenants, TenantPickContext context, CancellationToken ct);
}

public class TenantDiscovery(IAuthProxyClient proxy, ITenantPicker picker) {
    /// <summary>
    /// The error in words. A rejected token is the provider's rejection, so the lane names itself:
    /// telling a WorkOS user that GitHub turned them away sends them to the wrong sign-in.
    /// </summary>
    internal static string Describe(DiscoveryError error, string provider) {
        var who = provider == AuthProvider.WorkOS ? "WorkOS" : "GitHub";

        return error switch {
            DiscoveryError.ProxyUnreachable => "The Kurrent auth service is unreachable.",
            DiscoveryError.TokenRejected    => $"{who} rejected the authentication token. Please sign in again.",
            DiscoveryError.UpstreamError    => "Kurrent auth service returned an error. Try again later.",
            _                               => "Tenant discovery failed."
        };
    }

    public async Task<DiscoveryOutcome> RunAsync(string proxyUrl, string githubAccessToken, CancellationToken ct = default) {
        var result = await proxy.DiscoverTenantsAsync(proxyUrl, githubAccessToken, ct);

        if (result.Error != DiscoveryError.None) {
            return new([], null, Describe(result.Error, AuthProvider.GitHubApp));
        }

        if (result.Tenants.Length == 0) {
            return new([], null,
                "No kcap tenants are linked to your GitHub orgs. Ask your admin to install the Kurrent GitHub App on your org, or re-run with --server-url <url> for a self-hosted server.",
                NoTenantsFound: true);
        }

        var picked = result.Tenants.Length == 1
            ? result.Tenants[0]
            : await picker.PickAsync(result.Tenants, TenantPickContext.None, ct);

        if (picked is null) {
            return new(result.Tenants, null, "No tenant selected.", AlreadyReported: true);
        }

        return new(result.Tenants, picked, null);
    }

    /// <summary>
    /// Names each tenant's profile before anything is written, so the config, the stamp and the
    /// token all land under one name. A profile already on the tenant's server keeps its own name:
    /// a hand-named profile is updated rather than twinned under the slug, and the `kcap use`
    /// habits and profile bindings that name it keep working. Among several on that server the
    /// active one wins, then the slug's. Profiles record no organization, so where tenants share a
    /// server only a profile named for one of them is reused: handing out the rest would follow
    /// discovery order and could swap their credentials between runs. No two tenants share a name:
    /// a slug another tenant already claimed takes the first free numbered suffix.
    /// </summary>
    public static (DiscoveredTenant[] Tenants, DiscoveredTenant Picked) NameProfiles(
            ProfileConfig existing, DiscoveredTenant[] tenants, DiscoveredTenant picked) {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var names   = new string?[tenants.Length];

        // A tenant listed twice is one tenant: its rows share a name rather than claiming two.
        var first = tenants.Select((t, i) => Array.FindIndex(tenants, o => SameTenant(o, t))).ToArray();

        // Tenants are named in an order of their own ids rather than the order discovery lists them,
        // so a collision resolves the same way on every sign-in.
        var order = Enumerable.Range(0, tenants.Length).Where(i => first[i] == i)
            .OrderBy(i => StableKey(tenants[i]), StringComparer.Ordinal).ToArray();

        bool AloneOnServer(int i) => !Enumerable.Range(0, tenants.Length).Any(k =>
            first[k] == k && k != i && ServerIdentity.SameServer(tenants[k].Origin, tenants[i].Origin));

        // A profile already named for the tenant, under any of its rows' names, on its server is that
        // tenant's whatever order the tenants arrive in.
        foreach (var i in order) {
            names[i] = Enumerable.Range(0, tenants.Length)
                .Where(j => first[j] == i)
                .Select(j => tenants[j].ProfileName)
                .FirstOrDefault(name => IsOnServer(existing, name, tenants[i]) && !claimed.Contains(name));
            if (names[i] is { } own) claimed.Add(own);
        }

        foreach (var i in order)
            if (names[i] is null && AloneOnServer(i) && ProfileOnServer(existing, tenants[i], claimed) is { } onServer && claimed.Add(onServer))
                names[i] = onServer;

        foreach (var i in order)
            names[i] ??= Unclaimed(existing, claimed, tenants[i].ProfileName);

        for (var i = 0; i < tenants.Length; i++)
            names[i] ??= names[first[i]];

        var named = tenants.Select((t, i) => t with { ProfileName = names[i]! }).ToArray();
        var index = Array.IndexOf(tenants, picked);

        return (named, index >= 0
            ? named[index]
            : picked with { ProfileName = ProfileOnServer(existing, picked, FrozenSet<string>.Empty) ?? picked.ProfileName });
    }

    /// <summary>Whether naming <paramref name="tenants"/> against <paramref name="config"/> still gives
    /// the names in <paramref name="named"/>: the commit checks it under the config lock, because a
    /// profile written after the names were chosen can change them.</summary>
    public static bool NamesHold(
            ProfileConfig config, DiscoveredTenant[] tenants, DiscoveredTenant picked,
            DiscoveredTenant[] named, DiscoveredTenant namedPicked) {
        var (now, nowPicked) = NameProfiles(config, tenants, picked);

        return nowPicked.ProfileName == namedPicked.ProfileName
            && now.Select(t => t.ProfileName).SequenceEqual(named.Select(t => t.ProfileName), StringComparer.Ordinal);
    }

    /// <summary>Rows naming one tenant on one server: the same provider-qualified id (a WorkOS
    /// organization, a GitHub org), whatever slug each carries; rows without one match on name. One
    /// id on two servers is two profiles.</summary>
    static bool SameTenant(DiscoveredTenant a, DiscoveredTenant b) {
        if (a.IsWorkOS != b.IsWorkOS || !(a.Origin == b.Origin || ServerIdentity.SameServer(a.Origin, b.Origin))) return false;

        var idA = ProviderId(a);
        var idB = ProviderId(b);

        return !string.IsNullOrEmpty(idA) || !string.IsNullOrEmpty(idB) ? idA == idB : a.ProfileName == b.ProfileName;
    }

    static string StableKey(DiscoveredTenant t) =>
        string.Join('\n', t.IsWorkOS ? "w" : "g", ProviderId(t) ?? "", t.ProfileName, t.Origin);

    static string? ProviderId(DiscoveredTenant t) =>
        t.IsWorkOS ? t.OrganizationId : t.OrgId != 0 ? t.OrgId.ToString(CultureInfo.InvariantCulture) : null;

    static bool IsOnServer(ProfileConfig existing, string name, DiscoveredTenant tenant) =>
        existing.Profiles.TryGetValue(name, out var profile)
     && !string.IsNullOrWhiteSpace(profile.ServerUrl) && ServerIdentity.SameServer(profile.ServerUrl, tenant.Origin);

    /// <summary>An existing profile on the tenant's server that no other tenant has taken: the active
    /// one first, then the slug's, then the first by name.</summary>
    static string? ProfileOnServer(ProfileConfig existing, DiscoveredTenant tenant, IReadOnlySet<string> claimed) {
        var onServer = existing.Profiles
            .Where(p => !claimed.Contains(p.Key) && IsOnServer(existing, p.Key, tenant))
            .Select(p => p.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (onServer.Count == 0) return null;
        if (onServer.Contains(existing.ActiveName, StringComparer.Ordinal)) return existing.ActiveName;

        return onServer.Contains(tenant.ProfileName, StringComparer.Ordinal) ? tenant.ProfileName : onServer[0];
    }

    /// <summary>The slug, or else the first numbered suffix no profile has: profiles record no
    /// organization, so an existing numbered profile may be another tenant's.</summary>
    static string Unclaimed(ProfileConfig existing, HashSet<string> claimed, string name) {
        if (claimed.Add(name)) return name;

        for (var n = 2; ; n++) {
            var candidate = $"{name}-{n}";
            if (!existing.Profiles.ContainsKey(candidate) && claimed.Add(candidate)) return candidate;
        }
    }

    public static ProfileConfig MergeProfiles(
            ProfileConfig      existing,
            DiscoveredTenant[] discovered,
            DiscoveredTenant   active) {
        var profiles      = new Dictionary<string, Config_Profile>(existing.Profiles);
        var activeProfile = existing.ActiveName;
        var template = existing.Profiles.GetValueOrDefault(activeProfile)
                    ?? existing.Profiles.GetValueOrDefault(ProfileConfig.DefaultName)
                    ?? new Config_Profile();

        foreach (var t in discovered) {
            var name = t.ProfileName;
            // Seed a brand-new tenant profile from the template, but never inherit the
            // template's remembered import org — that GitHub owner belongs to a different
            // tenant and would silently scope this tenant's `kcap import --org`. Existing
            // profiles keep their own ImportOrg.
            var basis = profiles.GetValueOrDefault(name) ?? template with { ImportOrg = null };
            profiles[name] = basis with {
                ServerUrl = AppConfig.NormalizeUrl(t.Origin)
            };
        }

        return existing with {
            Profiles      = profiles,
            ActiveProfile = active.ProfileName
        };
    }
}
