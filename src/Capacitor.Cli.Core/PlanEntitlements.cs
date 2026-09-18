namespace Capacitor.Cli.Core;

/// <summary>The plan-gated feature keys the server reports on. Values match the server's own
/// entitlement names; an unrecognised key is carried through rather than dropped, so a newer server
/// can deny a feature this CLI has not heard of yet.</summary>
public static class PlanFeature {
    public const string WorkItems = "work_items";
    public const string Projects  = "projects";
    public const string Analytics = "analytics";
}

/// <summary>
/// Which plan-gated features the connected tenant may use, as last reported by the server's
/// <c>X-Kcap-Plan</c> header. Only DENIALS are carried: an unknown feature reads as allowed, so a
/// server that never sends the header, an unreachable one, and a CLI older than the feature all
/// behave exactly as they do today.
/// </summary>
public sealed class PlanEntitlements {
    /// <summary>Nothing known to be denied — the fail-open default.</summary>
    public static readonly PlanEntitlements Unknown = new([]);

    // Defensive caps: the value is attacker-reachable only via a server the user has adopted, but a
    // malformed header must not turn into an unbounded set held for the process's life.
    const int MaxEntries    = 32;
    const int MaxKeyLength  = 64;

    readonly HashSet<string> _denied;

    PlanEntitlements(HashSet<string> denied) => _denied = denied;

    public IReadOnlyCollection<string> Denied => _denied;

    /// <summary>Whether <paramref name="feature"/> may be used. True for anything not explicitly denied.</summary>
    public bool Allows(string feature) => !_denied.Contains(feature);

    public static PlanEntitlements FromDenied(IEnumerable<string> denied) =>
        new(NewSet(denied.Where(IsUsableKey).Take(MaxEntries)));

    /// <summary>
    /// Parses the <c>X-Kcap-Plan</c> value — comma-separated <c>key=flag</c> pairs where <c>0</c>
    /// denies and anything else allows, e.g. <c>work_items=0,projects=0,analytics=1</c>. A blank,
    /// absent or unparseable value yields <see cref="Unknown"/>; a malformed pair is skipped rather
    /// than failing the whole header, so one bad entry cannot re-enable the rest.
    /// </summary>
    public static PlanEntitlements Parse(string? headerValue) {
        if (string.IsNullOrWhiteSpace(headerValue)) return Unknown;

        var denied = NewSet([]);

        foreach (var pair in headerValue.Split(',', MaxEntries + 1, StringSplitOptions.TrimEntries)) {
            if (denied.Count >= MaxEntries) break;

            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;

            var key = pair[..eq].Trim();
            if (!IsUsableKey(key)) continue;
            if (pair[(eq + 1)..].Trim() == "0") denied.Add(key);
        }

        return denied.Count == 0 ? Unknown : new PlanEntitlements(denied);
    }

    /// <summary>The <c>X-Kcap-Plan</c>-shaped rendering of the denied set, for the on-disk cache —
    /// so what is stored and what arrives on the wire parse through the same code.</summary>
    public string Render() => string.Join(',', _denied.Order(StringComparer.Ordinal).Select(f => $"{f}=0"));

    static HashSet<string> NewSet(IEnumerable<string> values) => new(values, StringComparer.OrdinalIgnoreCase);

    // Printable ASCII only: the key reaches a file path decision nowhere, but it is rendered back out
    // and compared, and a control character in either has no legitimate source.
    static bool IsUsableKey(string key) =>
        key.Length is > 0 and <= MaxKeyLength && key.All(c => c is > ' ' and < (char)0x7f && c != '=' && c != ',');
}
