using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class RepositoryDetectionCacheTests {
    [Test]
    public async Task GitCacheEntry_without_schema_version_deserializes_as_stale() {
        // A pre-upgrade entry (no schema_version, no host) must be treated as stale.
        const string legacy = """{"owner":"o","repo_name":"r","cached_at":"2020-01-01T00:00:00+00:00"}""";
        var entry = JsonSerializer.Deserialize(legacy, CapacitorJsonContext.Default.GitCacheEntry);
        await Assert.That(entry!.SchemaVersion).IsEqualTo(0);        // absent → default 0
        await Assert.That(entry.SchemaVersion == RepositoryDetection.CacheSchemaVersion).IsFalse();
    }

    [Test]
    public async Task GitCacheEntry_roundtrips_host_and_version() {
        var entry = new GitCacheEntry {
            RemoteUrl = "git@gitlab.com:group/project.git",
            Owner = "group", RepoName = "project", Host = "gitlab.com",
            SchemaVersion = RepositoryDetection.CacheSchemaVersion, CachedAt = DateTimeOffset.UnixEpoch
        };
        var json = JsonSerializer.Serialize(entry, CapacitorJsonContext.Default.GitCacheEntry);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.GitCacheEntry);
        await Assert.That(back!.Host).IsEqualTo("gitlab.com");
        await Assert.That(back.SchemaVersion).IsEqualTo(RepositoryDetection.CacheSchemaVersion);
    }
}
