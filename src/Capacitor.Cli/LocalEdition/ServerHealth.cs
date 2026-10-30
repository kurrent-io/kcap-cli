namespace Capacitor.Cli.LocalEdition;

/// <summary>One answer of the local server's <c>/healthz/ready</c>.</summary>
abstract record ServerHealth {
    ServerHealth() { }

    /// <summary>Nothing answered, or the answer was not HTTP.</summary>
    public sealed record Unreachable(string Reason) : ServerHealth;

    /// <param name="Stores">Each health check by name (<c>kurrentdb</c>, <c>postgres</c>); empty when the body
    /// named none.</param>
    public sealed record Answered(bool Ready, IReadOnlyDictionary<string, StoreHealth> Stores, bool UpdatePending = false) : ServerHealth;
}
