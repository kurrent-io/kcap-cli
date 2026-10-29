using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// Reads and writes <c>install.json</c> and <c>journal.json</c>. Reads take no lock and are safe at
/// any time; writes belong to <see cref="LifecycleLease"/>, which holds the lifecycle lock.
///
/// <para>Both files are written durably and owner-only, replacing whatever sits at the path rather
/// than writing through a link. The journal's <c>phase</c> and <c>server_start</c> are read by the
/// server's bootstrap: <c>server_start</c> absent or <c>"allow"</c> starts it, <c>"fenced"</c> starts it fenced, and
/// anything else refuses.</para>
/// </summary>
public static partial class LocalStateFiles {
    public const int CurrentSchema = 1;

    const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static StateRead<InstallRegistry> ReadRegistry(LocalRoot root) =>
        Read(root.RegistryPath, LocalStateJsonContext.Default.RegistryWire, ToRegistry);

    public static StateRead<LifecycleJournalEntry> ReadJournal(LocalRoot root) =>
        Read(root.JournalPath, LocalStateJsonContext.Default.JournalWire, ToJournal);

    internal static void PublishRegistry(LocalRoot root, InstallRegistry registry) =>
        Write(root.RegistryPath, JsonSerializer.SerializeToUtf8Bytes(FromRegistry(registry), LocalStateJsonContext.Default.RegistryWire));

    internal static void WriteJournal(LocalRoot root, LifecycleJournalEntry entry) =>
        Write(root.JournalPath, JsonSerializer.SerializeToUtf8Bytes(FromJournal(entry), LocalStateJsonContext.Default.JournalWire));

    internal static void ClearJournal(LocalRoot root) {
        File.Delete(root.JournalPath);
        FaultPoints.Hit($"unlink:{root.JournalPath}");

        if (File.Exists(root.JournalPath) || Directory.Exists(root.JournalPath))
            throw new LocalLifecycleException($"The lifecycle journal {root.JournalPath} could not be removed.");

        DurableDirectory.Sync(root.Directory);
    }

    static void Write(string path, byte[] bytes) {
        for (var attempt = 1; ; attempt++) {
            try {
                AtomicFile.Replace(path, bytes, OperatingSystem.IsWindows() ? null : OwnerReadWrite, followLink: false, durable: true);
                break;
            } catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException && attempt < 10) {
                // A reader's open handle refuses the replace on Windows until it closes.
                Thread.Sleep(20 * attempt);
            }
        }

        DurableDirectory.Sync(Path.GetDirectoryName(path)!);
    }

    static StateRead<T> Read<TWire, T>(string path, JsonTypeInfo<TWire> info, Func<TWire, T?> map)
        where TWire : class
        where T : class {
        byte[] bytes;

        try {
            // Share-all, so a reader never refuses the writer's replace on Windows.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        } catch (FileNotFoundException) {
            return new StateRead<T>.Absent();
        } catch (DirectoryNotFoundException) {
            return new StateRead<T>.Absent();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new StateRead<T>.Unreadable(path, ex.Message);
        }

        try {
            if (JsonSerializer.Deserialize(bytes, info) is not { } wire)
                return new StateRead<T>.Unreadable(path, "it is empty");

            return map(wire) is { } value
                ? new StateRead<T>.Present(value)
                : new StateRead<T>.Unreadable(path, Describe(wire));
        } catch (JsonException ex) {
            return new StateRead<T>.Unreadable(path, ex.Message);
        }
    }

    static string Describe(object wire) =>
        wire switch {
            RegistryWire { Schema: > CurrentSchema } or JournalWire { Schema: > CurrentSchema } =>
                "it was written by a newer kcap; update kcap",
            _ => "a required field is missing or invalid",
        };

    static InstallRegistry? ToRegistry(RegistryWire wire) {
        if (wire.Schema is < 1 or > CurrentSchema) return null;

        LocalInstallation? active = null;

        if (wire.Active is { } a) {
            if (a.Id is not { Length: > 0 } id || a.ServerUrl is not { Length: > 0 } url || a.Version is not { Length: > 0 } version || a.Port is not { } port)
                return null;

            active = new(id, port, url, version);
        }

        // Required even when empty: a registry that lost its retired list would let a purged port be reused.
        if (wire.Retired is null) return null;

        var retired = new List<RetiredInstallation>();

        foreach (var r in wire.Retired) {
            if (r is null || r.Id is not { Length: > 0 } id || r.ServerUrl is not { Length: > 0 } url || r.Port is not { } port) return null;

            retired.Add(new(id, port, url));
        }

        return new(active, retired);
    }

    static LifecycleJournalEntry? ToJournal(JournalWire wire) {
        if (wire.Schema is < 1 or > CurrentSchema) return null;
        if (wire.Operation is not { Length: > 0 } operation || wire.Phase is not { Length: > 0 } phase || wire.StartedAt is not { } startedAt)
            return null;
        if (wire.ServerStart switch { Allow => ServerStart.Allow, Fenced => ServerStart.Fenced, Deny => ServerStart.Deny, _ => (ServerStart?)null } is not { } serverStart)
            return null;

        return new(operation, phase, serverStart, wire.InstallationId, startedAt, wire.Details ?? new Dictionary<string, string>());
    }

    static RegistryWire FromRegistry(InstallRegistry registry) =>
        new() {
            Schema  = CurrentSchema,
            Active  = registry.Active is { } a ? new() { Id = a.Id, Port = a.Port, ServerUrl = a.ServerUrl, Version = a.Version } : null,
            Retired = [.. registry.Retired.Select(r => new InstallationWire { Id = r.Id, Port = r.Port, ServerUrl = r.ServerUrl })],
        };

    static JournalWire FromJournal(LifecycleJournalEntry entry) =>
        new() {
            Schema         = CurrentSchema,
            Operation      = entry.Operation,
            Phase          = entry.Phase,
            ServerStart    = entry.ServerStart switch {
                ServerStart.Allow  => Allow,
                ServerStart.Fenced => Fenced,
                _                  => Deny,
            },
            InstallationId = entry.InstallationId,
            StartedAt      = entry.StartedAt,
            Details        = new(entry.Details),
        };

    const string Allow = "allow";
    const string Fenced = "fenced";
    const string Deny  = "deny";

    internal sealed class RegistryWire {
        public int                    Schema  { get; set; }
        public InstallationWire?      Active  { get; set; }
        public List<InstallationWire?>? Retired { get; set; }
    }

    internal sealed class InstallationWire {
        public string? Id        { get; set; }
        public int?    Port      { get; set; }
        public string? ServerUrl { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Version   { get; set; }
    }

    internal sealed class JournalWire {
        public int                         Schema         { get; set; }
        public string?                     Operation      { get; set; }
        public string?                     Phase          { get; set; }
        public string?                     ServerStart    { get; set; }
        public string?                     InstallationId { get; set; }
        public DateTimeOffset?             StartedAt      { get; set; }
        public Dictionary<string, string>? Details        { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
    [JsonSerializable(typeof(RegistryWire))]
    [JsonSerializable(typeof(JournalWire))]
    internal partial class LocalStateJsonContext : JsonSerializerContext;
}
