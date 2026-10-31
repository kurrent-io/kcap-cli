namespace Capacitor.Cli.Core.Handoff;

/// <summary>A request to take over an ended source session for the successor this daemon runs.</summary>
public sealed record HandoffTakeoverBeginRequest(string SourceSessionId, string SuccessorSessionId);
