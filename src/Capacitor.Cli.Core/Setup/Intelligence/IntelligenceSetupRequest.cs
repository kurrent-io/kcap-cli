namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>The body of the server's <c>POST /api/admin/intelligence</c>; snake_case on the wire.</summary>
public sealed record IntelligenceSetupRequest(
    string Mode, string? Harness = null, string? Vendor = null, string? Model = null, string? ApiKey = null, bool? ImportOutcomes = null) {
    /// <summary>The key stays out of logs and test output.</summary>
    public override string ToString() =>
        $"{nameof(IntelligenceSetupRequest)} {{ Mode = {Mode}, Harness = {Harness}, Vendor = {Vendor}, Model = {Model}, "
      + $"ApiKey = {(ApiKey is null ? "" : "***")}, ImportOutcomes = {ImportOutcomes} }}";
}
