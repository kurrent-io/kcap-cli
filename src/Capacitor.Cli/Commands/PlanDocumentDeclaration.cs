using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>What <c>declare_plan_document</c> sends and what it read to build it. The body is the
/// wire request; the rest annotates the tool result, since the server's response cannot know
/// whether a snapshot was attached or how large the file was.</summary>
sealed record PlanDocumentDeclaration(JsonObject Body, string WirePath, string? WorkspaceRoot, long ContentBytes, bool SnapshotAttached);
