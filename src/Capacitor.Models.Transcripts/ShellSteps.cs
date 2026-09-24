namespace Capacitor.Models.Transcripts;

/// <summary>The shell commands one transcript line started and the results it returned.</summary>
public sealed record ShellSteps(string? Cwd, DateTimeOffset? At, IReadOnlyList<ShellSteps.Invocation> Calls, IReadOnlyList<ShellSteps.Result> Results) {
    public sealed record Invocation(string Id, string Command);

    public sealed record Result(string CallId, string Output, bool IsError);
}
