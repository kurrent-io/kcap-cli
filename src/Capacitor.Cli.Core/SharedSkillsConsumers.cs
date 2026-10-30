using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts;

namespace Capacitor.Cli.Core;

/// <summary>
/// The integrations that rely on the shared <c>~/.agents/skills</c> tree, recorded beside it. Every
/// harness that reads the tree discovers every skill in it, so none can opt out for itself alone: the
/// skills kcap owns stay while any consumer remains and go with the last one.
/// </summary>
/// <remarks>
/// A tree installed before this record existed has none. <c>inferLegacy</c> then names the
/// integrations still configured on the machine, which stand in for the record until it is written.
/// </remarks>
public static class SharedSkillsConsumers {
    public const string FileName = ".kcap-consumers.json";

    /// <summary>The consumer an explicit <c>kcap plugin install --skills</c> records.</summary>
    public const string Explicit = "skills";

    public static IReadOnlySet<string> Read(string skillsDir, Func<IEnumerable<string>> inferLegacy) {
        if (ReadRecorded(skillsDir) is { } recorded) return recorded;

        return AgentsSkillsInstaller.IsInstalled(skillsDir)
            ? inferLegacy().ToFrozenSet(StringComparer.Ordinal)
            : FrozenSet<string>.Empty;
    }

    /// <summary>The recorded consumers, or null when there is no readable record.</summary>
    public static IReadOnlySet<string>? ReadRecorded(string skillsDir) {
        var path = Path.Combine(skillsDir, FileName);

        if (!File.Exists(path)) return null;

        try {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.Arr("consumers") is not { } entries) return null;

            var consumers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray()) {
                if (!entry.IsString) return null;
                consumers.Add(entry.GetString()!);
            }

            return consumers.ToFrozenSet(StringComparer.Ordinal);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    public static bool Write(string skillsDir, IEnumerable<string> consumers) {
        var entries = consumers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(c => (JsonNode?)JsonValue.Create(c)).ToArray();

        try {
            Directory.CreateDirectory(skillsDir);
            File.WriteAllText(Path.Combine(skillsDir, FileName), new JsonObject { ["consumers"] = new JsonArray(entries) }.ToJsonString());

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>Drops <paramref name="consumer"/>; when nobody is left, removes the skills kcap owns.</summary>
    public static SharedSkillsRelease Release(string skillsDir, string consumer, Func<IEnumerable<string>> inferLegacy) {
        var remaining = Read(skillsDir, inferLegacy)
            .Where(c => c != consumer)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (remaining.Count == 0) return new SharedSkillsRelease([], Clear(skillsDir));

        Write(skillsDir, remaining);

        return new SharedSkillsRelease(remaining, new RemovalResult(false, false));
    }

    /// <summary>Forgets every consumer and removes the skills kcap owns, keeping any the user edited.</summary>
    public static RemovalResult Clear(string skillsDir) {
        try { File.Delete(Path.Combine(skillsDir, FileName)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Removal below still runs; a leftover record names consumers of a tree that is gone.
        }

        return AgentsSkillsInstaller.Remove(skillsDir);
    }
}
