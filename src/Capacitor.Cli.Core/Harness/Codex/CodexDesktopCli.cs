using System.Text.Json;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>
/// The Codex CLI the ChatGPT desktop app ships inside its bundle, as a package whose
/// <c>codex-package.json</c> names the entrypoint relative to the package. The bundled binary finds
/// its own helpers (<c>rg</c>) through that manifest, so a launch needs nothing on PATH.
/// </summary>
public static class CodexDesktopCli {
    static readonly string Package = Path.Combine("ChatGPT.app", "Contents", "Resources", "codex-cli");

    const string Manifest          = "codex-package.json";
    const string DefaultEntrypoint = "bin/codex";

    static readonly BinaryProbe Launchable = BinaryProbe.Searching(null);

    /// <summary>The bundled entrypoint, or null.</summary>
    public static string? Find(DesktopAppProbe apps) =>
        apps.Directories(Package).Select(Entrypoint).FirstOrDefault(exe => exe is not null);

    static string? Entrypoint(string package) {
        var relative = ReadEntrypoint(Path.Combine(package, Manifest)) ?? DefaultEntrypoint;
        if (Path.IsPathRooted(relative)) return null;

        var root = Path.GetFullPath(package) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(package, relative));

        // The manifest is the app's, but what it names is what the daemon spawns: stay inside the bundle.
        return full.StartsWith(root, StringComparison.Ordinal) ? Launchable.Resolve(full) : null;
    }

    static string? ReadEntrypoint(string manifest) {
        try {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));

            return doc.RootElement.Str("entrypoint") is { Length: > 0 } entry ? entry : null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }
}
