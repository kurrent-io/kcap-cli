using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Config;

public static class ConfigMigration {
    public record MigrationResult(ProfileConfig Config, bool WasMigrated, bool ShouldPersist);

    static MigrationResult FreshDefault() =>
        new(new() { Profiles = new() { ["default"] = new() } }, WasMigrated: true, ShouldPersist: false);

    public static MigrationResult MigrateIfNeeded(string json) {
        JsonNode? parsed;

        try {
            parsed = JsonNode.Parse(json);
        } catch (JsonException) {
            return FreshDefault();
        }

        if (parsed is not JsonObject node)
            return FreshDefault();

        // Check if already V2
        if (node["version"]?.GetValue<int>() is 2) {
            var v2 = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

            return new(v2, WasMigrated: false, ShouldPersist: false);
        }

        // V1 → V2: read old flat fields, build default profile
        var v1 = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.LegacyV1Config)
         ?? new LegacyV1Config();

        // STJ source-gen does not apply the record member-initializer default
        // (`= true`) for a JSON property absent from the payload — a v1 config
        // lacking "update_check" deserializes v1.UpdateCheck to false, even
        // though the v1 default was true (see UpdateChannelConfigTests for the
        // same quirk on Profile.UpdateChannel). Read the raw node instead so
        // "absent" and "explicitly false" are distinguished correctly.
        var updateCheck = node["update_check"]?.GetValue<bool>() ?? true;

        var defaultProfile = new Profile {
            ServerUrl         = v1.ServerUrl,
            Daemon            = v1.Daemon,
            DefaultVisibility = v1.DefaultVisibility,
            UpdateCheck       = updateCheck,
            ExcludedRepos     = v1.ExcludedRepos
        };

        var config = new ProfileConfig {
            ActiveProfile   = "default",
            Profiles        = new() { ["default"] = defaultProfile },
            ProfileBindings = []
        };

        return new(config, WasMigrated: true, ShouldPersist: true);
    }
}
