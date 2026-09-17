using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>What <c>declare_plan_document</c> sends and what it read to build it. The body is the
/// wire request; the rest annotates the tool result, since the server's response cannot know
/// whether a snapshot was attached or why not. <paramref name="SnapshotOmitted"/> is null when
/// the content rode along.</summary>
sealed record PlanDocumentDeclaration(JsonObject Body, string WirePath, string? WorkspaceRoot, long ContentBytes, string? SnapshotOmitted) {
    public bool SnapshotAttached => SnapshotOmitted is null;
}
