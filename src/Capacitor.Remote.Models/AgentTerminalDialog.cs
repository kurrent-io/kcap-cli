using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A select dialog a hosted agent's terminal is showing, as the daemon read it off the screen. The
/// dialog reads keystrokes, so <see cref="Selected"/> is where its cursor sits; options are empty when
/// the rows could not be read as one choice and <see cref="Screen"/> is all there is to show.
public sealed record AgentTerminalDialog {
    [JsonPropertyName("heading")]  public required string Heading { get; init; }
    [JsonPropertyName("body")]     public string Body { get; init; } = "";
    [JsonPropertyName("options")]  public string[] Options { get; init; } = [];
    [JsonPropertyName("selected")] public int Selected { get; init; } = -1;
    [JsonPropertyName("screen")]   public string Screen { get; init; } = "";
}
