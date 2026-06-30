namespace Capacitor.Cli.Core.Curation;

public static class CuratedTargets {
    public const string ClaudeMdName = "CLAUDE.md";
    public const string AgentsMdName = "AGENTS.md";

    public static IReadOnlyList<string> Resolve(
            string repoRoot, bool claudeMdExists, bool agentsMdExists, bool hasContent) {
        var claude = Path.Combine(repoRoot, ClaudeMdName);
        var agents = Path.Combine(repoRoot, AgentsMdName);

        if (!hasContent) {
            // Nothing to write: only touch files that already exist (to remove a stale block).
            var existing = new List<string>();
            if (claudeMdExists) existing.Add(claude);
            if (agentsMdExists) existing.Add(agents);
            return existing;
        }

        if (claudeMdExists && agentsMdExists) return [claude, agents];
        if (claudeMdExists)                   return [claude];
        if (agentsMdExists)                   return [agents];
        return [agents];   // neither exists → create the cross-harness default
    }
}
