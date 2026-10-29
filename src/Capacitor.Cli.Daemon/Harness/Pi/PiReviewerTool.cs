namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>One tool a Pi reviewer is offered. <see cref="ServerName"/> and <see cref="McpName"/> are
/// null for a tool the reviewer extension implements itself.</summary>
internal sealed record PiReviewerTool(string PiName, string? ServerName, string? McpName);
