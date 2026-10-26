namespace Capacitor.Cli.Core.Accounts;

/// <summary><see cref="Vendor"/> is the launch token (<c>claude</c>, <c>codex</c>);
/// <see cref="RecordingState"/> is the recording-state name in snake_case (<c>recording</c>,
/// <c>installed</c>, <c>broken</c>, <c>not_wired</c>).</summary>
public sealed record AccountViewDto(string Id, string Vendor, string Directory, string DirectoryKey, string Label, string RecordingState);
