using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Install;

/// <summary>
/// The install manifest a CLI release publishes: its version and, per RID, the archive's sha256. The
/// archive URL is deliberately not read from it: it is built from the release URL, so a replaced
/// manifest can only name genuine release assets and the checksum and the bytes come from two hosts.
/// </summary>
public sealed partial record ReleaseManifest(string Version, IReadOnlyDictionary<string, string> Sha256ByRid) {
    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    public static bool IsValidVersion(string? version) => version is not null && VersionPattern().IsMatch(version);

    public static bool IsValidSha256(string? sha256) => sha256 is not null && Sha256Pattern().IsMatch(sha256);

    /// <summary>The manifest, or null when the body is not one: unparseable, or naming no valid version.
    /// A platform whose checksum is malformed is kept, so the caller can name the RID it fails for.</summary>
    public static ReleaseManifest? Parse(string json) {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return null; }

        if (root is not JsonObject obj
         || obj["version"] is not JsonValue v || !v.TryGetValue<string>(out var version) || !IsValidVersion(version))
            return null;

        var platforms = new Dictionary<string, string>(StringComparer.Ordinal);
        if (obj["platforms"] is JsonObject entries) {
            foreach (var (rid, entry) in entries) {
                if (entry is JsonObject e && e["sha256"] is JsonValue s && s.TryGetValue<string>(out var sha))
                    platforms[rid] = sha;
            }
        }

        return new ReleaseManifest(version, platforms);
    }
}
