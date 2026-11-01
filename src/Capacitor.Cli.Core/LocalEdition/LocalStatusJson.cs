using System.Text.Json;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>What <c>kcap local status --json</c> prints: the verdict the plain command prints, and the condition a
/// client acts on instead of probing the server itself. Snake case on the wire.</summary>
/// <param name="Condition">A <see cref="LocalServerCondition"/> by its wire name.</param>
/// <param name="HolderPid">The process the OS names on the port, with <paramref name="HolderCommand"/>, when the
/// condition is <see cref="LocalServerCondition.PortTaken"/>.</param>
/// <param name="Answer">What answered on the port as another program, when the OS names none.</param>
public sealed record LocalStatusJson(
    bool Ready, string Headline, string Condition, string[] Notes,
    int? Port = null, int? HolderPid = null, string? HolderCommand = null, string? Answer = null) {
    public static LocalStatusJson From(bool ready, string headline, IEnumerable<string> notes, LocalServerReading reading) =>
        new(ready, headline, reading.Condition.Wire, [.. notes], reading.Port, reading.Holder?.Pid, reading.Holder?.Command, reading.Answer);

    public string Render() => JsonSerializer.Serialize(this, LocalStatusJsonContext.Default.LocalStatusJson);

    /// <summary>Null for output that is not one status object, or that names a condition this build does not know.</summary>
    public static LocalServerReading? Parse(string text) {
        LocalStatusJson? status;

        try {
            status = JsonSerializer.Deserialize(text.Trim(), LocalStatusJsonContext.Default.LocalStatusJson);
        } catch (JsonException) {
            return null;
        }

        if (status is null || LocalServerConditions.FromWire(status.Condition) is not { } condition) return null;

        var holder = status is { HolderPid: { } pid, HolderCommand: { } command } ? new PortHolder(pid, command) : null;

        return new(condition, status.Port, holder, status.Answer);
    }
}
