namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>
/// Which Claude calls the policy judge may answer. The judge stands in for a human at a prompt
/// Claude would raise, so it is consulted only where that prompt exists: a judge allow where
/// Claude never asks overrides the mode instead — under <c>dontAsk</c> a hook allow runs a call
/// the mode would have refused.
/// </summary>
public static class ClaudeJudgeLane {
    /// <summary>Tools whose prompt is the human's own answer — a question to reply to, a plan to
    /// approve. An allow would answer it for them.</summary>
    public static bool IsHumanInteraction(string? toolName) =>
        toolName is ClaudeElicitation.ToolName or "ExitPlanMode";

    /// <summary>
    /// Whether a PreToolUse call in <paramref name="permissionMode"/> is one Claude may put to a
    /// human. <c>auto</c>, <c>dontAsk</c> and <c>bypassPermissions</c> never prompt at this point,
    /// and <c>plan</c> refuses changes itself; a prompt any of them does raise reaches
    /// PermissionRequest, which consults there. An absent mode is a build that predates the field;
    /// an unknown one is left to the human.
    /// </summary>
    public static bool PromptsAtPreToolUse(string? permissionMode) =>
        permissionMode is null or "default" or "acceptEdits";
}
