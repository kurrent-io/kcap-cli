using System.Text.Json;

namespace Capacitor.App.Services.LocalSetup;

/// One stdout line of `kcap setup --local --json`. The `v` field is not checked: a newer CLI's line
/// is still read for the events the app knows.
public sealed record LocalSetupLine(string Event, JsonElement Root) {
    public static LocalSetupLine? Parse(string text) {
        try {
            // The element must outlive the document, so it is cloned out.
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return root.Str("event") is { } name ? new LocalSetupLine(name, root.Clone()) : null;
        } catch (JsonException) {
            return null;
        }
    }
}
