using System.Text.Json;
using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.RepoEvidence;

/// <summary>
/// The github.com pull request a Claude transcript line records the agent creating, read from the
/// <c>gitOperation</c> annotation Claude Code puts on a tool result. It holds whichever checkout
/// the PR came from — a `gh pr create --repo` run anywhere, a subagent's worktree, one of several
/// branches in a shared worktree — none of which a probe of a checked-out branch can see.
/// </summary>
public static class CreatedPullRequests {
    public static CreatedPullRequest? FromClaudeLine(string jsonlLine) {
        if (!jsonlLine.Contains("\"gitOperation\"", StringComparison.Ordinal)) return null;

        try {
            using var doc = JsonDocument.Parse(jsonlLine);

            if (doc.RootElement.Obj("toolUseResult")?.Obj("gitOperation")?.Obj("pr") is not { } pr
             || pr.Str("action") != "created"
             || pr.Str("url") is not { } url
             || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
             || !string.Equals(uri.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase)
             || !PrRefParser.TryParse(url, out var owner, out var name, out var number)
             || pr.Num("number") is { } declared && declared != number) {
                return null;
            }

            return new CreatedPullRequest(owner, name, number, url);
        } catch (JsonException) {
            return null;
        }
    }
}
