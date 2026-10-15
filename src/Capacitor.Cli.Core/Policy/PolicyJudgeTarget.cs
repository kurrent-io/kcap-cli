namespace Capacitor.Cli.Core.Policy;

/// <summary>The one string a person would recognise a call by, for a declared refusal. The judge
/// sets a refusal against an action by tool and target, so the target is read off the same
/// canonical action the rules see.</summary>
public static class PolicyJudgeTarget {
    public static string Of(CanonicalAction a) => a.Kind switch {
        ActionKind.Shell => a.Command ?? "",
        ActionKind.FileEdit or ActionKind.FileRead => string.Join(" ", a.Paths),
        ActionKind.Network => a.Url ?? (a.Port is { } port ? $"{a.Host}:{port}" : a.Host ?? ""),
        ActionKind.McpTool => $"{a.Server}/{a.Tool}",
        _ => a.RawPayloadJson ?? "",
    };
}
