namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The local installation's root and the lifecycle files directly under it.
///
/// <para>A fixed location under the home directory that ignores <c>KCAP_CONFIG_DIR</c>, as
/// <see cref="DaemonStore"/> does: two config roots must not see two installations, or each would
/// install its own stack for the same user. The server resolves the same default and the same
/// override, so the two always agree on where the journal is.</para>
/// </summary>
public sealed class LocalRoot(string directory, UserHome? home = null) {
    /// <summary>Read only by <see cref="FromEnvironment"/>, at a process entry point, and by the server's
    /// bootstrap.</summary>
    public const string EnvVar = "KCAP_LOCAL_ROOT";

    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public string Directory { get; } = directory;

    /// <summary>The home this root was resolved against; levels above it are never synced.</summary>
    public UserHome? Home { get; } = home;

    public string RegistryPath => Path.Combine(Directory, "install.json");
    public string LockPath     => Path.Combine(Directory, "lifecycle.lock");
    public string JournalPath  => Path.Combine(Directory, "journal.json");

    /// <summary>The root for this process. Call once, in <c>Main</c> or the composition root.</summary>
    public static LocalRoot FromEnvironment(UserHome home) => Resolve(Environment.GetEnvironmentVariable(EnvVar), home);

    public static LocalRoot Resolve(string? configured, UserHome home) =>
        string.IsNullOrWhiteSpace(configured)
            ? new(Path.Combine(home.Path, ".config", "kcap", "local"), home)
            : new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured)), home);

    /// <summary>Creates the root owner-only, or makes an existing one so, and syncs every level of its
    /// path below the home (the filesystem root when there is none). A symlinked root is refused:
    /// the lock and the registry would live wherever the link points.</summary>
    public void EnsureCreated(UserHome? home = null) {
        if (new DirectoryInfo(Directory).LinkTarget is not null)
            throw new LocalLifecycleException($"{Directory} is a symlink; the local installation's root must be a real directory.");

        DurableDirectory.Ensure(Directory, DurableDirectory.AnchorFor(Directory, home ?? Home), OwnerOnly);

        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Directory, OwnerOnly);
    }
}
