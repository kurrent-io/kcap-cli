namespace Capacitor.Cli.Core.Resume;

/// <summary>The session a daemon is about to resume after a restart, for <c>BeginSourceResume</c>. One record, so the
/// call can grow without breaking a bound server.</summary>
public sealed record SourceResumeBeginRequest(string SourceSessionId);
