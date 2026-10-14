namespace Capacitor.Cli;

/// <summary>The nearest coding agent above a process: its vendor and its pid.</summary>
readonly record struct CodingAgentAncestor(string Vendor, int Pid);
