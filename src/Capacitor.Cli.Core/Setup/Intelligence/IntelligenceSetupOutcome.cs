namespace Capacitor.Cli.Core.Setup.Intelligence;

public abstract record IntelligenceSetupOutcome {
    IntelligenceSetupOutcome() { }

    public sealed record Applied(IntelligenceSetupResponse Response) : IntelligenceSetupOutcome;

    /// <summary>The server refused the answer and stored nothing; <paramref name="Error"/> is its code.</summary>
    public sealed record Refused(string Error) : IntelligenceSetupOutcome;

    /// <summary>The answer may not have reached the server.</summary>
    public sealed record Failed(string Problem) : IntelligenceSetupOutcome;
}
