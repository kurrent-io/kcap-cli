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

    static string DefaultConfigPath => Path.Combine(CodexPaths.Home(), "config.toml");

    /// <summary>
    /// The kcap MCP servers auto-registered for Codex CLI in <c>~/.codex/config.toml</c>.
    /// Codex reads MCP servers from the snake_case <c>[mcp_servers]</c> TOML table — note
    /// this is NOT the camelCase <c>mcpServers</c> key used by the Claude/Codex plugin
    /// *descriptor* JSON. <c>kcap-flows</c> is intentionally excluded: it launches a paid
    /// hosted reviewer and stays Claude-only (AI-1056). <c>kcap-memory</c> IS included
    /// (AI-1146): it is a free, harness-agnostic team-memory server, so Codex users going
    /// through <c>kcap setup</c> get it alongside review/sessions.
    /// </summary>
    static readonly (string Name, string[] Args)[] KcapMcpServers = [
        ("kcap-review", ["mcp", "review"]),
        ("kcap-sessions", ["mcp", "sessions"]),
        ("kcap-memory", ["mcp", "memory"])
    ];

    const string McpServerCommand = "kcap";

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
            : Update(configPath ?? DefaultConfigPath, root => MutateNetworkAccess(root, allowDomains), out _);

    /// <summary>
    /// Writes the per-worktree pre-trust entry:
    /// <code>
    /// [projects."/abs/worktree/path"]
    /// trust_level = "trusted"
    /// </code>
    /// </summary>
    public static Change TrustWorktree(string worktreePath, string? configPath = null) =>
        TrustWorktree(worktreePath, out _, configPath);

    /// <summary>
    /// As <see cref="TrustWorktree(string,string?)"/>, but surfaces the underlying
    /// exception on <see cref="Change.Failed"/> so the daemon can log the root cause
    /// (parse vs permissions vs IO) it would otherwise lose to the swallowing
    /// <see cref="Update"/>. <paramref name="error"/> is null unless the result is
    /// <see cref="Change.Failed"/>.
    /// </summary>
    internal static Change TrustWorktree(string worktreePath, out Exception? error, string? configPath = null) =>
        Update(configPath ?? DefaultConfigPath, root => MutateTrust(root, worktreePath), out error);

    /// <summary>
    /// Registers the kcap MCP servers (<c>kcap-review</c>, <c>kcap-sessions</c>,
    /// <c>kcap-memory</c>) under the
    /// top-level <c>[mcp_servers]</c> table of <c>~/.codex/config.toml</c> (or
    /// <paramref name="configPath"/>) so Codex CLI picks them up with no manual TOML edit.
    /// Idempotent, and non-destructive: an entry that already exists (a prior registration
    /// or a user customization such as an absolute-path <c>command</c>) is left untouched;
    /// only missing servers are added. User-defined <c>mcp_servers</c> entries are preserved.
    /// </summary>
    public static Change RegisterKcapMcpServers(string? configPath = null) =>
        Update(configPath ?? DefaultConfigPath, MutateRegisterMcpServers, out _);

    /// <summary>
    /// Removes the kcap-owned MCP server entries (<c>kcap-review</c>, <c>kcap-sessions</c>,
    /// <c>kcap-memory</c>)
    /// from <c>~/.codex/config.toml</c> (or <paramref name="configPath"/>). Only those names
    /// are touched; user-defined servers are preserved. Drops the <c>[mcp_servers]</c> table
    /// entirely when removing them empties it, so uninstall leaves no bare table behind.
    /// </summary>
    public static Change UnregisterKcapMcpServers(string? configPath = null) =>
        Update(configPath ?? DefaultConfigPath, MutateUnregisterMcpServers, out _);

    /// <summary>
    /// Reads the top-level <c>model = "…"</c> key from <c>~/.codex/config.toml</c>
    /// (or <paramref name="configPath"/>), honouring <c>CODEX_HOME</c> via
    /// <see cref="CodexPaths.Home"/>. Returns null when the file is missing, unreadable,
    /// has no top-level <c>model</c> key, or the value isn't a string — so callers can
    /// fall back to the dispatched model. Read-only; never throws.
    /// </summary>
    public static string? ReadTopLevelModel(string? configPath = null) {
        var path = configPath ?? DefaultConfigPath;

        if (!File.Exists(path)) return null;

        try {
            var root = TomlSerializer.Deserialize(File.ReadAllText(path), _tomlTypeInfo.TableInfo);

            return root is not null && root.TryGetValue("model", out var v) && v is string s && !string.IsNullOrWhiteSpace(s)
                ? s
                : null;
        } catch {
            return null;
        }
    }

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
        var hosts               = new List<string>();
        var seen                = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
    /// <see cref="Change.Updated"/>. <paramref name="error"/> carries the captured
    /// exception on <see cref="Change.Failed"/> (null otherwise) so callers can log it.
    /// </summary>
    static Change Update(string configPath, Func<TomlTable, bool> mutate, out Exception? error) {
        error = null;

        lock (_writeLock) {
            TomlTable root;

            if (File.Exists(configPath)) {
                try {
                    root = TomlSerializer.Deserialize(File.ReadAllText(configPath), _tomlTypeInfo.TableInfo) ?? new TomlTable();
                } catch (Exception ex) {
                    error = ex;

                    return Change.Failed;
                }
            } else {
                root = new TomlTable();
            }

            bool changed;

            try {
                changed = mutate(root);
            } catch (Exception ex) {
                error = ex;

                return Change.Failed;
            }

            if (!changed) return Change.Unchanged;

            try {
                // First-time users have no ~/.codex; create it before the atomic rename.
                // GetDirectoryName is null/empty for a directory-less path — skip the
                // create in that case (the file lands in the current directory).
                var dir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                WriteTomlAtomic(configPath, root);

                return Change.Updated;
            } catch (Exception ex) {
                error = ex;

                return Change.Failed;
            }
        }
    }

    static bool MutateTrust(TomlTable root, string worktreePath) {
        var projects = GetOrAddTable(root, "projects");
        var entry    = GetOrAddTable(projects, worktreePath);

        if (entry.TryGetValue("trust_level", out var existing) &&
            existing is string s                               && string.Equals(s, "trusted", StringComparison.Ordinal))
            return false;

        entry["trust_level"] = "trusted";

        return true;
    }

    static bool MutateRegisterMcpServers(TomlTable root) {
        // If `mcp_servers` exists but isn't a table, refuse rather than let GetOrAddTable
        // replace it — that would silently destroy the user's value and break the
        // non-destructive contract. Update() turns this throw into Change.Failed, so the
        // caller warns instead. (A non-table `mcp_servers` is an invalid Codex config, but
        // we still won't clobber it.)
        if (root.TryGetValue("mcp_servers", out var existing) && existing is not TomlTable)
            throw new InvalidOperationException(
                "~/.codex/config.toml has a non-table `mcp_servers` value; refusing to overwrite it."
            );

        var servers = GetOrAddTable(root, "mcp_servers");
        var changed = false;

        foreach (var (name, args) in KcapMcpServers) {
            // Never clobber an existing entry: a prior kcap registration is already
            // correct, and a user may have customized it (e.g. an absolute-path command
            // for a GUI host). Only create the server when it's missing entirely.
            if (servers.ContainsKey(name)) continue;

            var entry = new TomlTable {
                ["command"] = McpServerCommand,
                ["args"]    = ToTomlArray(args)
            };
            servers[name] = entry;
            changed       = true;
        }

        return changed;
    }

    static bool MutateUnregisterMcpServers(TomlTable root) {
        if (!root.TryGetValue("mcp_servers", out var sObj) || sObj is not TomlTable servers) return false;

        var changed = false;

        foreach (var (name, _) in KcapMcpServers)
            if (servers.Remove(name))
                changed = true;

        // Don't leave a bare [mcp_servers] behind if we emptied it.
        if (servers.Count == 0 && root.Remove("mcp_servers")) changed = true;

        return changed;
    }

    static TomlArray ToTomlArray(string[] values) {
        var arr = new TomlArray();
        foreach (var v in values) arr.Add(v);

        return arr;
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
                changed                                                          = true;
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
