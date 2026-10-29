namespace Capacitor.Cli.Core.Auth;

// The two self-serve plans kcap-web provisions. Team starts a 30-day trial; neither is billed.
public enum WorkspaceTier { Team, Free }

public static class WorkspaceTierExtensions {
    public static string Wire(this WorkspaceTier tier) => tier == WorkspaceTier.Team ? "team" : "free";
}
