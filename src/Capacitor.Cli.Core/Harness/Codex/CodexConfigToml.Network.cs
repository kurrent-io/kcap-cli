using System.Globalization;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;

namespace Capacitor.Cli.Core.Harness.Codex;

public static partial class CodexConfigToml {
    const string KcapWildcard    = "**.kcap.ai";
    const string NetworkClaimKey = "sandbox_network";

    static readonly string[] NetworkAccessPath = ["sandbox_workspace_write", "network_access"];
    static readonly string[] ProxyEnabledPath  = ["features", "network_proxy", "enabled"];
    static readonly string[] ProxyDomainsPath  = ["features", "network_proxy", "domains"];

    /// <summary>
    /// Enables network access for Codex's <c>workspace-write</c> sandbox so kcap skills (which shell out to
    /// <c>kcap …</c>) can reach the server, behind a <c>network_proxy</c> allowlist of
    /// <paramref name="allowDomains"/> (build it with <see cref="BuildAllowDomains"/>). An empty allowlist is a
    /// no-op: an enabled proxy with no <c>allow</c> rule would deny kcap too.
    /// <list type="bullet">
    /// <item>Network off, no active proxy → <c>network_access=true</c>, <c>network_proxy.enabled=true</c> and
    ///   the allowlist.</item>
    /// <item>Proxy already enabled → the allow entries are merged into the user's <c>domains</c>, and
    ///   <c>network_access</c> is turned on.</item>
    /// <item>Network on with no active proxy → no-op; a proxy would only narrow the user's access.</item>
    /// </list>
    /// Every key written is claimed in the ownership ledger with the value it replaced, which is what
    /// <see cref="RestoreNetworkAccess"/> needs to undo it. A key on the path that is not a table fails the
    /// call rather than being replaced.
    /// </summary>
    public static Change EnableNetworkAccess(IReadOnlyCollection<string> allowDomains, string configPath) {
        if (allowDomains.Count == 0) return Change.Unchanged;

        lock (_writeLock) {
            try {
                configPath = CanonicalConfigPath(configPath);
                using var crossProcess = AcquireConfigLock(configPath);

                var root    = LoadToml(configPath);
                var writes  = new List<(string[] Path, object? Previous, object Value)>();
                var created = new List<string[]>();

                if (!MutateNetworkAccess(root, allowDomains, writes, created)) return Change.Unchanged;

                WriteTomlAtomic(configPath, root);

                // Config first, claim second: a crash leaves an unclaimed change, which uninstall keeps.
                var ledgerPath = LedgerPath(configPath);
                var ledger     = ReadOwnershipLedger(ledgerPath) ?? NewOwnershipLedger();
                RecordNetworkClaims(ledger, writes, created);
                WriteJsonAtomic(ledgerPath, ledger);

                return Change.Updated;
            } catch {
                return Change.Failed;
            }
        }
    }

    /// <summary>
    /// Undoes the sandbox network keys <see cref="EnableNetworkAccess"/> claimed, and only those still holding
    /// the value kcap wrote: each goes back to the value it replaced, or away if there was none, and a table kcap
    /// created goes once it is empty. Everything else stays, and <see cref="CodexNetworkRestore.Kept"/> says why —
    /// a key changed since, a replaced value kcap cannot rebuild, or a proxy that must stay on because network
    /// access stays on (turning it off would open the sandbox to every host). With no claim at all, kcap-looking
    /// allow entries for <paramref name="kcapDomains"/> or <c>**.kcap.ai</c> are reported as kept under
    /// <see cref="Change.PreservedOwnershipUnknown"/>, never removed. The claim is relinquished either way.
    /// </summary>
    public static CodexNetworkRestore RestoreNetworkAccess(string configPath, IReadOnlyCollection<string> kcapDomains) {
        lock (_writeLock) {
            try {
                configPath = CanonicalConfigPath(configPath);
                var ledgerPath = LedgerPath(configPath);
                if (!File.Exists(configPath) && !File.Exists(ledgerPath)) return CodexNetworkRestore.Nothing;

                using var crossProcess = AcquireConfigLock(configPath);

                var root   = LoadToml(configPath);
                var ledger = ReadOwnershipLedger(ledgerPath);

                if (ledger is null || ledger[NetworkClaimKey] is not JsonObject claim) return Unproven(root, kcapDomains);

                var kept     = new List<string>();
                var restores = new List<(string[] Path, object? Previous)>();

                foreach (var node in claim["settings"] as JsonArray ?? new JsonArray()) {
                    if (node is not JsonObject setting || ReadPath(setting["path"]) is not { } path || setting["set"] is not { } set)
                        continue;

                    var current = Lookup(root, path);
                    if (current is null) continue;

                    if (!JsonNode.DeepEquals(NormalizeToml(current), set)) {
                        kept.Add($"{Describe(path, current)} (changed after kcap set it)");
                        continue;
                    }

                    if (!setting.ContainsKey("previous")) {
                        restores.Add((path, null));
                    } else if (Denormalize(setting["previous"]) is { } previous) {
                        restores.Add((path, previous));
                    } else {
                        kept.Add($"{Describe(path, current)} (kcap cannot rebuild the value it replaced)");
                    }
                }

                var networkIndex = restores.FindIndex(r => r.Path.SequenceEqual(NetworkAccessPath));
                var networkAfter = networkIndex >= 0 ? restores[networkIndex].Previous : Lookup(root, NetworkAccessPath);
                var proxyIndex   = restores.FindIndex(r => r.Path.SequenceEqual(ProxyEnabledPath));

                if (networkAfter is true && proxyIndex >= 0) {
                    kept.Add($"{Describe(ProxyEnabledPath, true)} (network access stays on, so turning the proxy off would let the sandbox reach every host)");
                    restores.RemoveAt(proxyIndex);
                }

                // Relinquish before touching config: a crash leaves unclaimed settings, which are kept.
                ledger.Remove(NetworkClaimKey);
                WriteJsonAtomic(ledgerPath, ledger);

                var restored = new List<string>();

                foreach (var (path, previous) in restores) {
                    var parent = LookupTable(root, path[..^1])!;

                    if (previous is null) {
                        parent.Remove(path[^1]);
                        restored.Add($"{FormatKey(path)} removed");
                    } else {
                        parent[path[^1]] = previous;
                        restored.Add(Describe(path, previous));
                    }
                }

                var pruned = PruneCreatedTables(root, claim["created_tables"] as JsonArray);

                if (restored.Count > 0 || pruned) WriteTomlAtomic(configPath, root);

                var change = restored.Count > 0
                    ? kept.Count > 0 ? Change.UpdatedWithPreservedEntries : Change.Updated
                    : kept.Count > 0 ? Change.PreservedUnownedEntries : Change.Unchanged;

                return new(change, restored, kept);
            } catch {
                return CodexNetworkRestore.Failed;
            }
        }
    }

    /// <summary>
    /// Deletes <c>mcp-ownership-v1.json</c> when it claims nothing. An empty ledger proves nothing, so this
    /// never touches the config itself; a ledger that cannot be parsed, or that carries keys this version does
    /// not know, is kept, as it may still be the only proof of what kcap wrote.
    /// </summary>
    public static bool DeleteEmptyOwnershipLedger(string configPath) {
        lock (_writeLock) {
            try {
                configPath = CanonicalConfigPath(configPath);
                var ledgerPath = LedgerPath(configPath);
                if (!File.Exists(ledgerPath)) return false;

                using var crossProcess = AcquireConfigLock(configPath);

                if (ReadOwnershipLedger(ledgerPath) is not { } ledger || !IsEmptyLedger(ledger)) return false;

                File.Delete(ledgerPath);

                return true;
            } catch {
                return false;
            }
        }
    }

    static bool MutateNetworkAccess(TomlTable root, IReadOnlyCollection<string> allowDomains,
                                    List<(string[] Path, object? Previous, object Value)> writes, List<string[]> created) {
        var networkOn    = Lookup(root, NetworkAccessPath) is true;
        var proxyEnabled = Lookup(root, ProxyEnabledPath) is true;

        if (networkOn && !proxyEnabled) return false;

        if (!networkOn) SetClaimed(root, NetworkAccessPath, true, writes, created);

        // An enabled proxy is the user's allowlist policy: kcap only adds its hosts to it.
        if (!proxyEnabled) SetClaimed(root, ProxyEnabledPath, true, writes, created);

        foreach (var domain in allowDomains) {
            string[] path = [.. ProxyDomainsPath, domain];
            if (Lookup(root, path) is "allow") continue;

            SetClaimed(root, path, "allow", writes, created);
        }

        return writes.Count > 0;
    }

    static void SetClaimed(TomlTable root, string[] path, object value, List<(string[] Path, object? Previous, object Value)> writes, List<string[]> created) {
        var table = root;

        for (var i = 0; i < path.Length - 1; i++) {
            if (!table.TryGetValue(path[i], out var next)) {
                var fresh = new TomlTable();
                table[path[i]] = fresh;
                created.Add(path[..(i + 1)]);
                table = fresh;
            } else {
                table = next as TomlTable ?? throw new InvalidDataException($"{FormatKey(path[..(i + 1)])} is not a table");
            }
        }

        writes.Add((path, table.TryGetValue(path[^1], out var previous) ? previous : null, value));
        table[path[^1]] = value;
    }

    static void RecordNetworkClaims(JsonObject ledger, List<(string[] Path, object? Previous, object Value)> writes, List<string[]> created) {
        if (ledger[NetworkClaimKey] is not JsonObject claim) ledger[NetworkClaimKey] = claim = new JsonObject();
        if (claim["settings"] is not JsonArray settings) claim["settings"] = settings = new JsonArray();
        if (claim["created_tables"] is not JsonArray tables) claim["created_tables"] = tables = new JsonArray();

        foreach (var (path, previous, value) in writes) {
            // kcap writes a key only when it differs from kcap's value, so an older claim on it is stale.
            for (var i = settings.Count - 1; i >= 0; i--)
                if (settings[i] is JsonObject old && SamePath(old["path"], path)) settings.RemoveAt(i);

            var setting = new JsonObject { ["path"] = PathNode(path), ["set"] = NormalizeToml(value) };
            if (previous is not null) setting["previous"] = NormalizeToml(previous);
            settings.Add((JsonNode)setting);
        }

        foreach (var path in created)
            if (!tables.Any(t => SamePath(t, path))) tables.Add((JsonNode)PathNode(path));
    }

    static CodexNetworkRestore Unproven(TomlTable root, IReadOnlyCollection<string> kcapDomains) {
        var kept = new List<string>();

        foreach (var domain in kcapDomains.Prepend(KcapWildcard).Distinct(StringComparer.Ordinal)) {
            string[] path = [.. ProxyDomainsPath, domain];
            if (Lookup(root, path) is "allow") kept.Add($"{Describe(path, "allow")} (kcap has no record that it made this change)");
        }

        if (kept.Count == 0) return CodexNetworkRestore.Nothing;

        if (Lookup(root, ProxyEnabledPath) is true)
            kept.Insert(0, $"{Describe(ProxyEnabledPath, true)} (kcap has no record that it made this change)");
        if (Lookup(root, NetworkAccessPath) is true)
            kept.Insert(0, $"{Describe(NetworkAccessPath, true)} (kcap has no record that it made this change)");

        return new(Change.PreservedOwnershipUnknown, [], kept);
    }

    static bool PruneCreatedTables(TomlTable root, JsonArray? created) {
        if (created is null) return false;

        var pruned = false;

        foreach (var path in created.Select(ReadPath).OfType<string[]>().Where(p => p.Length > 0).OrderByDescending(p => p.Length)) {
            if (LookupTable(root, path[..^1]) is { } parent &&
                parent.TryGetValue(path[^1], out var value) && value is TomlTable { Count: 0 }) {
                parent.Remove(path[^1]);
                pruned = true;
            }
        }

        return pruned;
    }

    static TomlTable LoadToml(string configPath) =>
        File.Exists(configPath)
            ? TomlSerializer.Deserialize(File.ReadAllTextShared(configPath), _tomlTypeInfo.TableInfo) ?? new TomlTable()
            : new TomlTable();

    static string LedgerPath(string canonicalConfigPath) =>
        Path.Combine(Path.GetDirectoryName(canonicalConfigPath) ?? ".", "mcp-ownership-v1.json");

    static bool IsEmptyLedger(JsonObject ledger) =>
        ledger.All(p => p.Key == "version" || (p.Key == "entries" && p.Value is JsonObject { Count: 0 }));

    static object? Lookup(TomlTable root, string[] path) =>
        LookupTable(root, path[..^1]) is { } parent && parent.TryGetValue(path[^1], out var value) ? value : null;

    static TomlTable? LookupTable(TomlTable root, string[] path) {
        var table = root;

        foreach (var key in path) {
            if (!table.TryGetValue(key, out var next) || next is not TomlTable child) return null;
            table = child;
        }

        return table;
    }

    /// <summary>Rebuilds a value the ledger recorded with <see cref="NormalizeToml"/>; null for a shape kcap
    /// cannot reproduce exactly.</summary>
    static object? Denormalize(JsonNode? node) {
        if (node is not JsonObject value || StringField(value, "type") is not { } type || value["value"] is not JsonValue raw)
            return null;

        return type switch {
            "bool" when raw.TryGetValue<bool>(out var b)                      => b,
            "string" when raw.TryGetValue<string>(out var s)                  => s,
            "System.Int64" when raw.TryGetValue<string>(out var l)
                             && long.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong) => parsedLong,
            "System.Double" when raw.TryGetValue<string>(out var d)
                              && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble) => parsedDouble,
            _ => null,
        };
    }

    static JsonArray PathNode(string[] path) =>
        new(path.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());

    static string[]? ReadPath(JsonNode? node) {
        if (node is not JsonArray array) return null;

        var path = new string[array.Count];

        for (var i = 0; i < array.Count; i++) {
            if (array[i] is not JsonValue v || !v.TryGetValue<string>(out var key)) return null;
            path[i] = key;
        }

        return path;
    }

    static bool SamePath(JsonNode? node, string[] path) => ReadPath(node) is { } read && read.SequenceEqual(path);

    static string Describe(string[] path, object value) => $"{FormatKey(path)} = {FormatValue(value)}";

    static string FormatKey(string[] path) =>
        string.Join('.', path.Select(k => k.Length > 0 && k.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? k : $"\"{k}\""));

    static string FormatValue(object value) => value switch {
        bool b   => b ? "true" : "false",
        string s => $"\"{s}\"",
        _        => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}
