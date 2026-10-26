namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>The Codex transport names and the version floor app-server needs. A build below the floor
/// runs PTY, and an app-server launch it was pinned to is refused.</summary>
internal static class CodexTransportDecision {
    public const string AppServer  = "app-server";
    public const string Pty        = "pty";

    /// <summary>The lowest Codex whose app-server method set and containment probes are verified.</summary>
    public const string VersionFloor = "0.146.0";

    /// <summary>Why a launch's expected transport cannot be honoured, or null when it can. Only a
    /// Codex launch has more than one transport, so every other vendor ignores the field.</summary>
    public static string? ExpectedTransportRejection(string vendor, string? expected) =>
        expected is null or AppServer or Pty || !string.Equals(vendor, "codex", StringComparison.Ordinal)
            ? null
            : $"codex_transport_unrecognised: the server expects runtime transport '{expected}', which this daemon does not know";

    /// <summary>True when <paramref name="cliVersion"/> parses and is >= <see cref="VersionFloor"/>.
    /// Tolerant of surrounding text (e.g. <c>"codex-cli 0.146.0"</c>) — it extracts the first
    /// dotted numeric token.</summary>
    public static bool MeetsFloor(string? cliVersion) {
        var actual = ParseSemver(cliVersion);
        var floor  = ParseSemver(VersionFloor);
        if (actual is null || floor is null) return false;

        var (aMaj, aMin, aPat) = actual.Value;
        var (fMaj, fMin, fPat) = floor.Value;
        if (aMaj != fMaj) return aMaj > fMaj;
        if (aMin != fMin) return aMin > fMin;
        return aPat >= fPat;
    }

    static (int Major, int Minor, int Patch)? ParseSemver(string? version) {
        if (string.IsNullOrWhiteSpace(version)) return null;

        // Accept ONLY a clean numeric release token `<digits>.<digits>[.<digits>]` (a `codex-cli`
        // prefix word is split off on whitespace and ignored). A prerelease or build-metadata tail
        // (`0.146.0-rc.1`, `0.146.0+meta`) or a non-numeric part makes that token non-clean, so it
        // is rejected and the caller fails toward PTY — this gate enables the containment-sensitive
        // transport, so an RC that is BELOW the verified release must never be normalized upward to it.
        foreach (var token in version.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
            var t = token.TrimStart('v', 'V');
            var parts = t.Split('.');
            if (parts.Length is < 2 or > 3) continue;
            if (!AllDigits(parts[0]) || !AllDigits(parts[1])) continue;
            if (parts.Length == 3 && !AllDigits(parts[2])) continue;

            return (int.Parse(parts[0]), int.Parse(parts[1]), parts.Length == 3 ? int.Parse(parts[2]) : 0);
        }
        return null;
    }

    static bool AllDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
}
