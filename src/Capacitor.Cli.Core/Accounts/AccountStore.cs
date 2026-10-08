using System.Text.Json;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>The per-user list of vendor accounts. Beside the daemons directory and, like it, blind
/// to <c>KCAP_CONFIG_DIR</c>: two config roots on one machine must see one list, or each would wire
/// the same vendor settings file and undo the other.</summary>
public sealed class AccountStore(string directory) {
    const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode OwnerOnlyDir  = OwnerOnlyFile | UnixFileMode.UserExecute;

    public string Directory { get; } = directory;

    string RegistryPath => Path.Combine(Directory, "accounts.json");
    string LockPath     => Path.Combine(Directory, "accounts.lock");
    string HostPath     => Path.Combine(Directory, "host.json");

    public static AccountStore Beside(DaemonStore daemons) =>
        new(Path.Combine(Path.GetDirectoryName(daemons.Directory)!, "accounts"));

    /// <summary>Not re-entrant: never call <see cref="HostId"/> or <see cref="Mutate{T}"/> while holding it.</summary>
    public IDisposable Lock() => ConfigFileLock.Acquire(LockPath);

    public AccountRegistry Load() {
        if (!File.Exists(RegistryPath)) return new AccountRegistry();
        try {
            return JsonSerializer.Deserialize(File.ReadAllText(RegistryPath), AccountRegistryJsonContext.Default.AccountRegistry)
                ?? throw new InvalidDataException($"{RegistryPath} is empty.");
        } catch (JsonException ex) {
            throw new InvalidDataException($"{RegistryPath} is not a valid account registry.", ex);
        }
    }

    /// <summary>For hooks and watchers, which must never fail on the registry: null sends the caller to
    /// the environment-derived layout.</summary>
    public AccountRegistry? TryLoad() {
        try { return Load(); }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public T Mutate<T>(Func<AccountRegistry, (AccountRegistry Next, T Result)> change) {
        using var _ = Lock();
        var current = Load();
        var (next, result) = change(current);
        if (!ReferenceEquals(next, current)) Save(next with { Revision = current.Revision + 1 });
        return result;
    }

    public VendorAccount? Find(HarnessId vendor, string directory) =>
        Load().Accounts.FirstOrDefault(a => a.Vendor == vendor && AccountDirectory.Same(a.Directory, directory));

    public string HostId() {
        using var _ = Lock();
        if (File.Exists(HostPath)) {
            try {
                if (JsonSerializer.Deserialize(File.ReadAllText(HostPath), AccountRegistryJsonContext.Default.HostIdentity) is { HostId: { Length: > 0 } existing })
                    return existing;
            } catch (JsonException ex) {
                throw new InvalidDataException($"{HostPath} is not a valid host identity.", ex);
            }
            throw new InvalidDataException($"{HostPath} has no host_id.");
        }

        var created = new HostIdentity(Guid.NewGuid().ToString("N"));
        EnsureDirectory();
        AtomicFile.Replace(HostPath, JsonSerializer.Serialize(created, AccountRegistryJsonContext.Default.HostIdentity), OwnerOnlyFile);
        return created.HostId;
    }

    void Save(AccountRegistry registry) {
        EnsureDirectory();
        AtomicFile.Replace(RegistryPath, JsonSerializer.Serialize(registry, AccountRegistryJsonContext.Default.AccountRegistry), OwnerOnlyFile);
    }

    void EnsureDirectory() {
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(Directory);
        else {
            System.IO.Directory.CreateDirectory(Directory, OwnerOnlyDir);
            File.SetUnixFileMode(Directory, OwnerOnlyDir);
        }
    }
}
