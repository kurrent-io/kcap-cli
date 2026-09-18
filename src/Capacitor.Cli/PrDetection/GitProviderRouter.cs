using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.PrDetection;

internal enum GitProviderKind { GitHub, GitLab, Unknown }

/// <summary>
/// Maps a remote host to a provider. SaaS hosts route directly; a custom host is
/// probed once via `gh auth status --json hosts` (GitHub if listed, else best-effort
/// GitLab), and the answer is remembered.
///
/// <para>Injected, never constructed at a call site: one per process is what makes remembering
/// worth anything. The watcher re-detects every 60s for as long as it runs, so a custom host would
/// otherwise pay for that probe on every refresh.</para>
///
/// <para>Public only because two hook commands are, and a public constructor cannot take a less
/// accessible parameter; everything it does stays internal.</para>
/// </summary>
public sealed class GitProviderRouter {
    readonly ConcurrentDictionary<string, GitProviderKind> _memo = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<GitProviderKind> ResolveAsync(string? host, string cwd, TimeSpan cap, CommandRunner run) {
        if (string.IsNullOrEmpty(host)) return GitProviderKind.Unknown;
        if (host == "github.com") return GitProviderKind.GitHub;
        if (host == "gitlab.com") return GitProviderKind.GitLab;

        if (_memo.TryGetValue(host, out var cached)) return cached;

        var kind = await ProbeAsync(host, cwd, cap, run);
        _memo[host] = kind;
        return kind;
    }

    static async Task<GitProviderKind> ProbeAsync(string host, string cwd, TimeSpan cap, CommandRunner run) {
        // `gh auth status --json hosts` forces exit 0 even on auth failure, so decide
        // from the JSON payload, not the exit code.
        var json = await run("gh", "auth status --json hosts", cwd, cap);
        if (json is not null) {
            try {
                if (JsonNode.Parse(json)?["hosts"] is JsonObject hosts && hosts.ContainsKey(host)) {
                    return GitProviderKind.GitHub;
                }
            } catch { /* fall through */ }
        }
        // Not a known GitHub host → assume GitLab and let the detector no-op if unauthenticated.
        return GitProviderKind.GitLab;
    }
}
