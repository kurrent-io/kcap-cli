using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Capacitor.Cli.Core;

/// <summary>
/// Read-modify-write engine for <c>~/.codex/config.toml</c>. Owns the AOT-safe
/// Tomlyn plumbing (load, atomic write, untyped-model type info) so both the
/// daemon's per-worktree pre-trust (<see cref="TrustWorktree"/>) and the setup
/// wizard's sandbox network-access opt-in (<see cref="EnableNetworkAccess"/>)
/// share one code path.
///
/// Tomlyn's TomlTable mode round-trips preserve all other top-level tables
/// (model, mcp_servers, plugins, marketplaces, ad-hoc user keys) but DO NOT
/// preserve comments or original formatting. Acceptable because the file is
/// largely tool-managed and human edits are expected to be sparse.
/// </summary>
public static class CodexConfigToml {
    static readonly Lock              _writeLock    = new();
    static readonly TomlTableTypeInfo _tomlTypeInfo = new();

    public enum Change { Unchanged, Updated, Failed }

    static string DefaultConfigPath => Path.Combine(CodexPaths.Home, "config.toml");

    /// <summary>
    /// AI-794 — enable network access for Codex's <c>workspace-write</c> sandbox so
    /// kcap skills (which shell out to <c>kcap …</c>) can reach the Capacitor server.
    /// Codex blocks all sandbox network by default; a constrained
    /// <c>network_proxy</c> allowlist keeps everything else closed.
    ///
    /// The change respects what the user already has (see the truth table below).
    /// <paramref name="allowDomains"/> is the host allowlist to permit, e.g.
    /// <c>["**.kcap.ai"]</c> for SaaS plus any self-hosted hosts — build it with
    /// <see cref="BuildAllowDomains"/>. An empty allowlist is a no-op (enabling the
    /// proxy with no <c>allow</c> rule would deny everything, breaking kcap too).
    ///
    /// <list type="bullet">
    /// <item>Default config (no network) → set <c>network_access=true</c> +
    ///   <c>network_proxy.enabled=true</c> + the allowlist. Only the allowlisted
    ///   hosts become reachable; everything else stays blocked as before.</item>
    /// <item><c>network_proxy.enabled=true</c> already → merge the allow entries into
    ///   the user's <c>domains</c> (theirs preserved) and ensure
    ///   <c>network_access=true</c>.</item>
    /// <item><c>network_access=true</c>, no active proxy (fully open) → no-op; kcap
    ///   already works and pinning a proxy would only narrow their access.</item>
    /// </list>
    /// </summary>
    public static Change EnableNetworkAccess(IReadOnlyCollection<string> allowDomains, string? configPath = null) =>
        allowDomains.Count == 0
            ? Change.Unchanged
            : Update(configPath ?? DefaultConfigPath, root => MutateNetworkAccess(root, allowDomains));

    /// <summary>
    /// Writes the per-worktree pre-trust entry:
    /// <code>
    /// [projects."/abs/worktree/path"]
    /// trust_level = "trusted"
    /// </code>
    /// </summary>
    public static Change TrustWorktree(string worktreePath, string? configPath = null) =>
        Update(configPath ?? DefaultConfigPath, root => MutateTrust(root, worktreePath));

    /// <summary>
    /// Builds the Codex proxy allowlist from a set of Capacitor server URLs (the
    /// active one plus every configured profile). Any host under <c>kcap.ai</c>
    /// collapses to a single <c>**.kcap.ai</c> wildcard — it matches the apex and
    /// every subdomain, so all SaaS tenants (current and future) and the auth proxy
    /// (<c>auth.kcap.ai</c>) are covered without per-tenant maintenance. Self-hosted
    /// hosts are added as exact entries. Output is deterministic (wildcard first,
    /// then hosts sorted) so repeated writes are idempotent.
    /// </summary>
    public static IReadOnlyList<string> BuildAllowDomains(IEnumerable<string?> serverUrls) {
        var hosts              = new List<string>();
        var seen               = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includeKcapWildcard = false;

        foreach (var url in serverUrls) {
            var host = TryGetHost(url);
            if (host is null) continue;

            if (host.Equals("kcap.ai", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".kcap.ai", StringComparison.OrdinalIgnoreCase)) {
                includeKcapWildcard = true;
            } else if (seen.Add(host)) {
                hosts.Add(host);
            }
        }

        hosts.Sort(StringComparer.Ordinal);

        if (includeKcapWildcard) hosts.Insert(0, "**.kcap.ai");

        return hosts;
    }

    static string? TryGetHost(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && !string.IsNullOrEmpty(abs.Host))
            return abs.Host;

        // Bare host[:port] without a scheme (self-hosted shorthand).
        return Uri.TryCreate("https://" + url, UriKind.Absolute, out var withScheme) && !string.IsNullOrEmpty(withScheme.Host)
            ? withScheme.Host
            : null;
    }

    /// <summary>
    /// Load → mutate → atomic-write. <paramref name="mutate"/> returns true when it
    /// changed <paramref name="root"/>. Returns <see cref="Change.Failed"/> on a
    /// parse error (we never clobber a config we can't read) or a write error,
    /// <see cref="Change.Unchanged"/> when nothing changed, otherwise
    /// <see cref="Change.Updated"/>.
    /// </summary>
    static Change Update(string configPath, Func<TomlTable, bool> mutate) {
        lock (_writeLock) {
            TomlTable root;

            if (File.Exists(configPath)) {
                try {
                    root = TomlSerializer.Deserialize(File.ReadAllText(configPath), _tomlTypeInfo.TableInfo) ?? new TomlTable();
                } catch {
                    return Change.Failed;
                }
            } else {
                root = new TomlTable();
            }

            bool changed;

            try {
                changed = mutate(root);
            } catch {
                return Change.Failed;
            }

            if (!changed) return Change.Unchanged;

            try {
                // First-time users have no ~/.codex; create it before the atomic rename.
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                WriteTomlAtomic(configPath, root);

                return Change.Updated;
            } catch {
                return Change.Failed;
            }
        }
    }

    static bool MutateTrust(TomlTable root, string worktreePath) {
        var projects = GetOrAddTable(root, "projects");
        var entry    = GetOrAddTable(projects, worktreePath);

        if (entry.TryGetValue("trust_level", out var existing) &&
            existing is string s && string.Equals(s, "trusted", StringComparison.Ordinal))
            return false;

        entry["trust_level"] = "trusted";

        return true;
    }

    static bool MutateNetworkAccess(TomlTable root, IReadOnlyCollection<string> allowDomains) {
        // Inspect current state WITHOUT mutating, so the "fully open" branch can
        // bail out without having created empty tables.
        var sandbox   = root.TryGetValue("sandbox_workspace_write", out var sObj) ? sObj as TomlTable : null;
        var networkOn = sandbox is not null && sandbox.TryGetValue("network_access", out var na) && na is true;

        var features     = root.TryGetValue("features", out var fObj) ? fObj as TomlTable : null;
        var proxy        = features is not null && features.TryGetValue("network_proxy", out var pObj) ? pObj as TomlTable : null;
        var proxyEnabled = proxy is not null && proxy.TryGetValue("enabled", out var pe) && pe is true;

        if (proxyEnabled) {
            // The user runs an allowlist policy — extend it with our hosts and make
            // sure network access is granted, preserving their existing entries.
            var changed = MergeAllowDomains(GetOrAddTable(proxy!, "domains"), allowDomains);

            if (!networkOn) {
                GetOrAddTable(root, "sandbox_workspace_write")["network_access"] = true;
                changed = true;
            }

            return changed;
        }

        // Fully open (network on, no active proxy): kcap already works; don't narrow it.
        if (networkOn) return false;

        // Default (network off, no active proxy): enable + tight kcap-only allowlist.
        GetOrAddTable(root, "sandbox_workspace_write")["network_access"] = true;

        var networkProxy = GetOrAddTable(GetOrAddTable(root, "features"), "network_proxy");
        networkProxy["enabled"] = true;
        MergeAllowDomains(GetOrAddTable(networkProxy, "domains"), allowDomains);

        return true;
    }

    static bool MergeAllowDomains(TomlTable domains, IReadOnlyCollection<string> allowDomains) {
        var changed = false;

        foreach (var domain in allowDomains) {
            if (domains.TryGetValue(domain, out var v) && v is string s && string.Equals(s, "allow", StringComparison.Ordinal))
                continue;

            domains[domain] = "allow";
            changed         = true;
        }

        return changed;
    }

    static TomlTable GetOrAddTable(TomlTable parent, string key) {
        if (parent.TryGetValue(key, out var existing) && existing is TomlTable table) return table;

        var created = new TomlTable();
        parent[key] = created;

        return created;
    }

    static void WriteTomlAtomic(string path, TomlTable root) {
        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, TomlSerializer.Serialize(root, _tomlTypeInfo.TableInfo));

        try {
            File.Move(tmp, path, overwrite: true);
        } catch {
            try { File.Delete(tmp); } catch {
                /* best-effort */
            }

            throw;
        }
    }

    /// <summary>
    /// AOT-safe accessor for <see cref="TomlTypeInfo{T}"/> of <see cref="TomlTable"/>.
    /// <see cref="TomlSerializerContext.GetBuiltInTypeInfo{T}"/> is protected, so a thin
    /// subclass surfaces the built-in untyped-model metadata without reflection.
    /// </summary>
    sealed class TomlTableTypeInfo : TomlSerializerContext {
        static readonly TomlSerializerOptions DefaultOptions = new();

        public readonly TomlTypeInfo<TomlTable> TableInfo = GetBuiltInTypeInfo<TomlTable>(DefaultOptions)!;

        public override TomlTypeInfo? GetTypeInfo(Type type, TomlSerializerOptions options) =>
            type == typeof(TomlTable) ? TableInfo : null;
    }
}
