using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>A session whose agent process is gone, with the vendor and server its hooks reported to,
/// when the claim recorded them.</summary>
sealed record ExitedSession(SessionId Session, string? Vendor, string? Server);
