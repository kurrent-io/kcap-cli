using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Skills;

/// <summary>Where a row's deletion authority comes from. A <see cref="Legacy"/> row is authorised
/// against the target's independently configured vendor legacy root — never against a repository
/// anchor, and never against the row's own recorded parent.</summary>
public enum SkillOrigin {
    [JsonStringEnumMemberName("repository")] Repository,

    [JsonStringEnumMemberName("legacy")] Legacy,
}
