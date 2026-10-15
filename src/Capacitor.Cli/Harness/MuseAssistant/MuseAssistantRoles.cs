namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>The roles a grant covers. Anything else — <c>developer</c> above all — moves the watermark
/// and nothing else.</summary>
public static class MuseAssistantRoles {
    public static readonly string[] Selected = ["user", "assistant", "commentary"];

    public static bool IsSelected(string role) => Array.IndexOf(Selected, role) >= 0;
}
