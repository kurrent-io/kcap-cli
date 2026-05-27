namespace Kapacitor.Cli.Core;

public static class AgentsPaths {
    public static string Home          => Path.Combine(PathHelpers.HomeDirectory, ".agents");
    public static string UserSkillsDir => Path.Combine(Home, "skills");
}
