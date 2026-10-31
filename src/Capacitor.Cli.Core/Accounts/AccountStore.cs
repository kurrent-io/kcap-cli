using System.Text.Json;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>The per-user list of vendor accounts. Beside the daemons directory and, like it, blind
/// to <c>KCAP_CONFIG_DIR</c>: two config roots on one machine must see one list, or each would wire
/// the same vendor settings file and undo the other.</summary>
public sealed class AccountStore {
    const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode OwnerOnlyDir  = OwnerOnlyFile | UnixFileMode.UserExecute;

    public AccountStore(string directory) {
        Directory = directory;
        Readings  = new ReadingStore(Path.Combine(directory, "readings"), Path.Combine(directory, "readings.lock"), TryLoad, EnsureDirectory);
    }

    public string Directory { get; }

    public ReadingStore Readings { get; }

    public string RegistryPath => Path.Combine(Directory, "accounts.json");
    string LockPath     => Path.Combine(Directory, "accounts.lock");
    string HostPath     => Path.Combine(Directory, "host.json");

    public static AccountStore Beside(DaemonStore daemons) =>
        new(Path.Combine(Path.GetDirectoryName(daemons.Directory)!, "accounts"));

    /// <summary>Not re-entrant: never call <see cref="HostId"/>, <see cref="Mutate{T}"/> or <see cref="MutateWithReadings{T}"/> while holding it.</summary>
    public IDisposable Lock() => ConfigFileLock.Acquire(LockPath);

    public AccountRegistry Load() {
        if (!File.Exists(RegistryPath)) return new AccountRegistry();
        AccountRegistry registry;
        // Producers read without accounts.lock; a reader denying writers would fail a concurrent save's rename on Windows.
        try {
            registry = JsonSerializer.Deserialize(File.ReadAllTextShared(RegistryPath), AccountRegistryJsonContext.Default.AccountRegistry)
                ?? throw new InvalidDataException($"{RegistryPath} is empty.");
        } catch (JsonException ex) {
            throw new InvalidDataException($"{RegistryPath} is not a valid account registry.", ex);
        }

        var accounts = (registry.Accounts ?? []).Where(Usable).ToList();
        return accounts.Count == registry.Accounts?.Count ? registry : registry with { Accounts = accounts };
    }

    // A hand edit can leave an entry no path lookup can use; dropping it keeps every hook working.
    static bool Usable(VendorAccount? account) =>
        account is { Id.Length: > 0, Directory: { } dir } && !string.IsNullOrWhiteSpace(dir) && Path.IsPathRooted(dir) && Normalizable(dir);

    static bool Normalizable(string dir) {
        try {
            AccountDirectory.Normalize(dir);
            return true;
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) {
            return false;
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

    public T Mutate<T>(Func<AccountRegistry, (AccountRegistry Next, T Result)> change) => MutateWithRevision(change).Result;

    /// <summary>Saves only when <c>Next</c> is a different reference from the registry it was given; a change
    /// that keeps the reference writes nothing and reports the current revision.</summary>
    public (T Result, long Revision) MutateWithRevision<T>(Func<AccountRegistry, (AccountRegistry Next, T Result)> change) {
        using var _ = Lock();
        return MutateHeld(change);
    }

    /// <summary><see cref="MutateWithRevision{T}"/> for a caller already holding <see cref="Lock"/>.</summary>
    public (T Result, long Revision) MutateHeld<T>(Func<AccountRegistry, (AccountRegistry Next, T Result)> change) {
        var current = Load();
        var (next, result) = change(current);
        return (result, Commit(current, next).Revision);
    }

    /// <summary>Holds <c>accounts.lock</c> and then <c>readings.lock</c> from the first read to the last write,
    /// so no producer commits between <paramref name="change"/> reading a reading and <paramref name="afterSave"/>
    /// acting on it. <paramref name="change"/> only reads; every reading write or delete belongs in
    /// <paramref name="afterSave"/>, which runs with the registry as persisted and not at all when the save
    /// throws, so a failed save leaves the readings as they were.</summary>
    public (T Result, long Revision) MutateWithReadings<T>(
        Func<AccountRegistry, HeldReadings, (AccountRegistry Next, T Result)> change,
        Action<AccountRegistry, HeldReadings>?                                 afterSave = null
    ) {
        using var accountsLease = Lock();
        using var readingsLease = Readings.Lock();

        var held           = new HeldReadings(Readings);
        var current        = Load();
        var (next, result) = change(current, held);
        var saved          = Commit(current, next);
        afterSave?.Invoke(saved, held);
        return (result, saved.Revision);
    }

    AccountRegistry Commit(AccountRegistry current, AccountRegistry next) {
        if (ReferenceEquals(next, current)) return current;

        var saved = next with { Revision = current.Revision + 1 };
        Save(saved);
        return saved;
    }

    /// <summary>A fresh read for a caller holding <see cref="Lock"/>.</summary>
    public VendorAccount? FindHeld(string id) => Load().Accounts.FirstOrDefault(a => a.Id == id);

    public VendorAccount? Find(HarnessId vendor, string directory) =>
        Load().Accounts.FirstOrDefault(a => a.Vendor == vendor && AccountDirectory.Same(a.Directory, directory));

    public string HostId() {
        using var _ = Lock();
        if (File.Exists(HostPath)) {
            try {
                if (JsonSerializer.Deserialize(File.ReadAllTextShared(HostPath), AccountRegistryJsonContext.Default.HostIdentity) is { HostId: { Length: > 0 } existing })
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
