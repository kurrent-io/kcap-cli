using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

/// <summary>Machine-readable payload for <c>kcap import --discover --json</c>.</summary>
/// <remarks>
/// A separate shape from <see cref="ImportDiscoverySummary"/> rather than serializing that directly:
/// the summary is free to change with the import pipeline, whereas this is a contract someone else's
/// code reads. Mirrors <c>kcap daemon service status --json</c> — snake_case, source-generated because
/// the CLI is NativeAOT. Indented rather than compact, because a person reads this too.
/// </remarks>
/// <param name="UnmatchedFolders">The folders behind <paramref name="UnmatchedSessions"/>, each one a value
/// <c>kcap import --folder</c> and <c>--skip-folder</c> accept; a null folder counts sessions with none recorded.</param>
public sealed record ImportDiscoveryJson(
    IReadOnlyList<ImportDiscoveryRepoJson>   Repos,
    int                                      UnmatchedSessions,
    IReadOnlyList<ImportDiscoveryWindowJson> Windows,
    IReadOnlyList<ImportDiscoveryFolderJson> UnmatchedFolders);

/// <param name="Windows">This repository's sessions in each window, in the order of the report's own
/// <see cref="ImportDiscoveryJson.Windows"/>.</param>
/// <param name="Vendors">The same figures for each harness that wrote sessions here.</param>
public sealed record ImportDiscoveryRepoJson(
    string Owner, string Name, int Sessions, string? LastSessionAt, IReadOnlyList<ImportDiscoveryWindowJson> Windows,
    IReadOnlyList<ImportDiscoveryVendorJson> Vendors);

/// <param name="Since">ISO date, or null for "everything".</param>
public sealed record ImportDiscoveryWindowJson(string? Since, int Sessions);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(ImportDiscoveryJson))]
public partial class ImportDiscoveryJsonContext : JsonSerializerContext;

/// <summary>Pure renderer, kept apart from I/O so it is directly testable.</summary>
public static class ImportDiscoveryRender {
    public static string ToJson(ImportDiscoverySummary summary) =>
        JsonSerializer.Serialize(
            new ImportDiscoveryJson(
                [.. summary.Repos.Select(r => new ImportDiscoveryRepoJson(
                    r.Owner, r.Name, r.SessionCount, r.LastSessionAt?.UtcDateTime.ToString("O"),
                    WindowsOf(summary, r.SessionsByWindow),
                    [.. (r.ByVendor ?? []).Select(v => new ImportDiscoveryVendorJson(
                        v.Vendor.VendorId, v.SessionCount, v.LastSessionAt?.UtcDateTime.ToString("O"),
                        WindowsOf(summary, v.SessionsByWindow)))]))],
                summary.UnmatchedCount,
                [.. summary.ByWindow.Select(w => new ImportDiscoveryWindowJson(
                    w.Since?.ToString("yyyy-MM-dd"), w.SessionCount))],
                [.. summary.UnmatchedFolders.Select(f => new ImportDiscoveryFolderJson(
                    f.Folder, f.Sessions,
                    f.Folder is { } named && summary.UnmatchedFolderWindows.TryGetValue(named, out var windows)
                        ? WindowsOf(summary, windows)
                        : null))]),
            ImportDiscoveryJsonContext.Default.ImportDiscoveryJson);

    static IReadOnlyList<ImportDiscoveryWindowJson> WindowsOf(
            ImportDiscoverySummary summary, IReadOnlyDictionary<string, int> sessionsByWindow) =>
        [.. summary.ByWindow.Select(w => new ImportDiscoveryWindowJson(
            w.Since?.ToString("yyyy-MM-dd"), sessionsByWindow.GetValueOrDefault(w.Key)))];

    public static string ToText(ImportDiscoverySummary summary) {
        var lines = new List<string> { "Discovered sessions:" };

        foreach (var r in summary.Repos) {
            var last = r.LastSessionAt is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd") : "unknown";

            lines.Add($"  {r.Owner}/{r.Name}  {r.SessionCount} session{(r.SessionCount == 1 ? "" : "s")}, last {last}");
        }

        if (summary.Repos.Count == 0) lines.Add("  (no sessions could be attributed to a repository)");

        if (summary.UnmatchedCount > 0) {
            lines.Add($"  couldn't match to a repository: {summary.UnmatchedCount}");
            lines.AddRange(UnmatchedFolderLines(summary.UnmatchedFolders));
        }

        lines.Add("");
        lines.Add("By window:");

        foreach (var w in summary.ByWindow) {
            lines.Add($"  {(w.Since is { } s ? $"since {s:yyyy-MM-dd}" : "everything"),-22}{w.SessionCount}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    const int ListedFolders = 10;

    /// <summary>Each folder behind the unmatched count: a folder is matched through a git checkout
    /// with a remote, so naming it is what tells the user which ones to remap or fix.</summary>
    internal static IEnumerable<string> UnmatchedFolderLines(IReadOnlyList<ImportUnmatchedFolder> folders) {
        var named = folders.Where(f => f.Folder is not null).ToList();

        foreach (var f in named.Take(ListedFolders))
            yield return $"    {f.Folder}  ({f.Sessions} session{(f.Sessions == 1 ? "" : "s")})";

        if (named.Count > ListedFolders)
            yield return $"    ... and {named.Count - ListedFolders} more folders ({named.Skip(ListedFolders).Sum(f => f.Sessions)} sessions)";

        if (folders.FirstOrDefault(f => f.Folder is null) is { } unknown)
            yield return $"    no folder recorded  ({unknown.Sessions} session{(unknown.Sessions == 1 ? "" : "s")})";

        if (named.Count > 0)
            yield return "    A folder matches when it is a git checkout with a remote. Map a moved one with `kcap remap <from> <to>`.";
    }
}
