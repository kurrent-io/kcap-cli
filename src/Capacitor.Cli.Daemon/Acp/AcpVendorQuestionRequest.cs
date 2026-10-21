namespace Capacitor.Cli.Daemon.Acp;

/// <summary>The questions a vendor asks in one request, and the session they belong to.</summary>
internal sealed record AcpVendorQuestionRequest(string SessionId, IReadOnlyList<AcpVendorQuestion> Questions);
