using System.Globalization;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Models.Transcripts.Harness.Codex;

namespace Capacitor.App.ViewModels;

/// What an expanded tool row shows, read from the call as recorded: its status, its command (or
/// patch, or input) and its output. Nothing is filled in — an absent part says it was not recorded,
/// and a part that was cut or redacted carries a note saying so.
public sealed class ToolInspection {
    /// Keeps one call's text from becoming a layout cost in a long chat.
    internal const int DisplayCap = 16_000;
    /// The capture redactor's stand-in for a secret value.
    internal const string RedactedMarker = "[REDACTED]";

    internal const string StatusHeading  = "Status";
    internal const string CommandHeading = "Command";
    internal const string PatchHeading   = "Patch";
    internal const string InputHeading   = "Input";
    internal const string OutputHeading  = "Output";

    internal const string NoInput  = "No input was recorded";
    internal const string NoOutput = "No output was recorded";
    internal const string NoResult = "No result yet";

    static readonly string[] PatchTextKeys = ["input", "command"];

    ToolInspection(IReadOnlyList<ToolInspectionSection> sections) => Sections = sections;

    public IReadOnlyList<ToolInspectionSection> Sections { get; }

    public static ToolInspection Build(string name, string? inputJson, string? result, ToolOutcome outcome, bool awaitingPermission) =>
        new([
            new ToolInspectionSection(StatusHeading, Status(outcome, awaitingPermission)),
            Input(name, inputJson),
            Output(result, outcome),
        ]);

    static string Status(ToolOutcome outcome, bool awaitingPermission) => outcome switch {
        ToolOutcome.Error                     => "Failed",
        ToolOutcome.Done                      => "Finished",
        _ when awaitingPermission             => "Waiting for permission",
        _                                     => "Running",
    };

    static ToolInspectionSection Input(string name, string? inputJson) {
        if (string.IsNullOrWhiteSpace(inputJson)) return new(InputHeading, NoInput, IsMissing: true);
        try {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            if (!root.IsObject) return Section(InputHeading, inputJson);
            if (ToolSummary.Names.TryGetValue(name, out var category) && category == ToolCategory.Command
                && ToolCommandText.From(root) is { } command && command.Trim().Length > 0)
                return Section(CommandHeading, command);
            foreach (var key in PatchTextKeys)
                if (root.Str(key) is { } patch && CodexPatch.Targets(patch) is not null) return Section(PatchHeading, patch);
            var listed = Listed(root);
            return listed.Length == 0 ? new(InputHeading, NoInput, IsMissing: true) : Section(InputHeading, listed);
        } catch (JsonException) {
            return Section(InputHeading, inputJson);
        }
    }

    static ToolInspectionSection Output(string? result, ToolOutcome outcome) {
        if (outcome == ToolOutcome.Running) return new(OutputHeading, NoResult, IsMissing: true);
        if (string.IsNullOrWhiteSpace(result)) return new(OutputHeading, NoOutput, IsMissing: true);
        return Section(OutputHeading, result, TranscriptEnvelopes.IsCapped(result)
            ? string.Create(CultureInfo.InvariantCulture, $"Cut to {TranscriptEnvelopes.ToolResultCap:N0} characters when it was recorded")
            : null);
    }

    static ToolInspectionSection Section(string heading, string text, string? recordedNote = null) {
        var notes = new List<string>(3);
        if (recordedNote is not null) notes.Add(recordedNote);
        if (text.Contains(RedactedMarker, StringComparison.Ordinal)) notes.Add("Secrets were redacted when it was recorded");
        if (text.Length > DisplayCap) {
            var cut = char.IsHighSurrogate(text[DisplayCap - 1]) ? DisplayCap - 1 : DisplayCap;
            text = text[..cut];
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"Showing the first {cut:N0} characters"));
        }
        return new(heading, text, Note: notes.Count == 0 ? null : string.Join(" · ", notes));
    }

    /// One argument per line, a string as written and anything else as its JSON. A multi-line
    /// string starts on the line after its name so its own lines keep their indentation.
    static string Listed(JsonElement input) {
        var sb = new StringBuilder();
        foreach (var property in input.EnumerateObject()) {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(property.Name).Append(':');
            if (property.Value.ValueKind != JsonValueKind.String) {
                sb.Append(' ').Append(property.Value.GetRawText());
                continue;
            }
            var value = property.Value.GetString() ?? "";
            sb.Append(value.Contains('\n') ? '\n' : ' ').Append(value);
        }
        return sb.ToString();
    }
}
