using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Codex;

/// `extensions.codex.exec` on a shell result, from `event_msg.exec_command_end`.
public sealed record CodexExecResultExtension {
    public int?         ExitCode   { get; init; }
    public double?      DurationMs { get; init; }
    public string?      Stdout     { get; init; }
    public string?      Stderr     { get; init; }
    public JsonElement? ParsedCmd  { get; init; }
    public string?      Source     { get; init; }
}
