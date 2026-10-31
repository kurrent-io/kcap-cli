namespace Capacitor.Cli.Commands;

/// One file change a session recorded. <paramref name="Kind"/> is <c>Write</c>, <c>Edit</c> or <c>Delete</c>;
/// <paramref name="Failed"/> means the tool call that made it reported a failure, so the change did not apply.
record PlanWorkRow(string Kind, string? Path, string? AgentId, DateTimeOffset Timestamp, bool Failed);
