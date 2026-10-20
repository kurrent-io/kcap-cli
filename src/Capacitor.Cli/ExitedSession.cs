using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>A session whose agent process is gone, with where its claim recorded it reports, when it did.</summary>
sealed record ExitedSession(SessionId Session, ClaimOrigin? Origin);
