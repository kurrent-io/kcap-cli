using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

/// <summary>
/// How far an import delivered a transcript: <see cref="Lines"/> complete lines read and accepted, after
/// which the server answered <see cref="Watermark"/> for its last line. The two differ when trailing lines
/// produce no event, which the server's watermark cannot see.
/// </summary>
internal sealed record TranscriptImportMark(
    [property: JsonPropertyName("lines")]     int Lines,
    [property: JsonPropertyName("watermark")] int Watermark
);
