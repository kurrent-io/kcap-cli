using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// What must hold before a local install starts. Nothing here locks, fetches or writes; a refusal throws
/// <see cref="LocalInstallFailure"/> naming what the user can change.
/// </summary>
sealed class LocalPreflight(
    LocalRoot root, ConfigRoot config, LocalPlatform platform, ILocalServerService service, int? socketLimit = null, ILocalClientCopies? copies = null) {
    /// <summary>Platform, socket paths, ancestors, journal and registry, then (unless a journal calls for
    /// recovery first) profiles, the empty slot and the config, then the version; the other kcap copies last, since
    /// asking them spawns each one. An old copy never blocks a recovery: <see cref="Recheck"/> refuses it after.</summary>
    public LocalPlan Check(string version) {
        CheckPlatform();
        CheckSockets();
        CheckAncestors();

        var plan = Decide(version, afterRecovery: false);
        if (plan.Hint != PlanHint.Recover) CheckClients();

        return plan;
    }

    /// <summary>Under the lease, after recovery: a journal still present is a bug in the caller.</summary>
    public LocalPlan Recheck(string version) {
        var plan = Decide(version, afterRecovery: true);
        CheckClients();

        return plan;
    }

    public static int SocketLimit(LocalPlatform p) => p.IsMacOS ? 103 : 107;

    /// <summary>Each of <see cref="LocalSockets.All"/> in UTF-8 bytes, the longer of its path under the root as
    /// configured, which is what the server binds, and under the root with symlinks resolved.</summary>
    public static IReadOnlyList<(string Path, int Bytes)> SocketPaths(LocalRoot root) {
        var configured = LocalSockets.All(new(Path.GetFullPath(root.Directory)));
        var resolved   = LocalSockets.All(new(ResolvedDirectory(root.Directory)));

        return [.. configured.Zip(resolved, (c, r) => {
            var (cBytes, rBytes) = (Encoding.UTF8.GetByteCount(c), Encoding.UTF8.GetByteCount(r));
            return rBytes >= cBytes ? (r, rBytes) : (c, cBytes);
        })];
    }

    LocalPlan Decide(string version, bool afterRecovery) {
        var hint = ReadHint(out var existing);

        if (hint == PlanHint.Recover) {
            if (afterRecovery) throw new InvalidOperationException($"The lifecycle journal {root.JournalPath} is still present after recovery.");

            CheckVersion(version);
            return new(hint, version, null);
        }

        var cfg = TryLoadConfig();

        if (cfg is not null) CheckProfiles(cfg);
        if (hint == PlanHint.Install) CheckSlot();

        if (cfg is null) throw Refuse(LocalInstallFailureCode.ConfigUnreadable, $"The configuration file {AppConfig.GetConfigPath(config)} cannot be read. Fix or remove it, then run setup again.");

        CheckVersion(version);

        return new(hint, version, existing);
    }

    void CheckPlatform() {
        if (platform.SupportsLocalEdition) return;
        if (platform.IsMacOS) throw Refuse(LocalInstallFailureCode.UnsupportedPlatform, "There is no Intel Mac build of the local edition; it needs an Apple silicon Mac.");
        if (platform.IsWindows) throw Refuse(LocalInstallFailureCode.UnsupportedPlatform, "There is no Arm build of the local edition for Windows; it needs an x64 PC.");
        if (platform.IsLinux && platform.IsMusl) throw Refuse(LocalInstallFailureCode.MuslUnsupported, "The musl builds of kcap do not support a local installation.");

        throw Refuse(LocalInstallFailureCode.UnsupportedPlatform, "The local edition runs on Apple silicon Macs and x64 Windows for now.");
    }

    void CheckClients() {
        if (copies?.Find().FirstOrDefault(c => !LocalClientFloor.Admits(c.Version)) is not { } old) return;

        var which = old.Kind == ClientCopyKind.DesktopApp ? $"The desktop app's kcap ({old.Path})" : $"The kcap on PATH ({old.Path})";
        var found = old.Version is { } v ? $"is {v}" : "did not report its version";
        var fix   = old.Kind == ClientCopyKind.DesktopApp ? "Update the desktop app" : "Update or remove it";

        throw Refuse(LocalInstallFailureCode.ClientBelowFloor,
            $"{which} {found}, and a local installation needs a kcap newer than {LocalClientFloor.NewestRefused}. {fix} before setting up a local installation.");
    }

    void CheckSockets() {
        var limit = socketLimit ?? SocketLimit(platform);
        var over = SocketPaths(root).Where(p => p.Bytes > limit).ToList();

        if (over.Count == 0) return;

        var listed = string.Join("; ", over.Select(p => $"{p.Path} is {p.Bytes} bytes"));
        var excess = over.Max(p => p.Bytes) - limit;

        throw Refuse(LocalInstallFailureCode.SocketPathTooLong,
            $"The local installation's sockets must have paths of at most {limit} bytes: {listed}. Set {LocalRoot.EnvVar} to a directory at least {excess} {(excess == 1 ? "byte" : "bytes")} shorter.");
    }

    void CheckAncestors() {
        if (OperatingSystem.IsWindows()) return;

        for (var dir = ResolvedDirectory(root.Directory); dir is not null; dir = Path.GetDirectoryName(dir)) {
            if (!Directory.Exists(dir)) continue;

            var mode = File.GetUnixFileMode(dir);
            if (!mode.HasFlag(UnixFileMode.OtherWrite) || mode.HasFlag(UnixFileMode.StickyBit)) continue;

            throw Refuse(LocalInstallFailureCode.UnsafeRoot, $"{dir} is writable by every user, so the local server refuses to start under it. Run chmod o-w on it, or set {LocalRoot.EnvVar} to a directory under your home.");
        }
    }

    PlanHint ReadHint(out LocalInstallation? existing) {
        existing = null;

        switch (LocalStateFiles.ReadJournal(root)) {
            case StateRead<LifecycleJournalEntry>.Present:
                return PlanHint.Recover;
            case StateRead<LifecycleJournalEntry>.Unreadable(var journalPath, var journalReason):
                throw Refuse(LocalInstallFailureCode.RegistryUnreadable, $"The setup journal {journalPath} cannot be read ({journalReason}). Fix or remove it, then run setup again.");
        }

        switch (LocalStateFiles.ReadRegistry(root)) {
            case StateRead<InstallRegistry>.Unreadable(var path, var reason):
                throw Refuse(LocalInstallFailureCode.RegistryUnreadable, $"The installation registry {path} cannot be read ({reason}). Fix or remove it, then run setup again.");
            case StateRead<InstallRegistry>.Present { Value.Active: { } active }:
                existing = active;
                return PlanHint.Reuse;
            default:
                return PlanHint.Install;
        }
    }

    ProfileConfig? TryLoadConfig() => ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var cfg) ? cfg : null;

    void CheckProfiles(ProfileConfig cfg) {
        var endpoints = LocalEndpoints.Read(root);

        if (LocalSetupState.FindForeignServerProfile(cfg, endpoints) is var (name, profile))
            throw Refuse(LocalInstallFailureCode.OtherServer, $"The profile \"{name}\" points at {profile.ServerUrl}, which is not this machine's local installation. Converting a machine that uses a cloud server arrives in a later release.");
    }

    void CheckSlot() {
        foreach (var name in new[] { "bin", "data", "run" }) {
            var path = Path.Combine(root.Directory, name);
            if (Path.Exists(path) || new DirectoryInfo(path).LinkTarget is not null)
                throw Leftover($"{path} already exists");
        }

        var owner = service.Owner(LocalServerUnit.ForInstallation(root, 0));

        if (Describe(owner.OnDisk) is { } onDisk) throw Leftover($"the {service.Describe} unit file {onDisk}");
        if (Describe(owner.Loaded) is { } loaded) throw Leftover($"the loaded {service.Describe} job {loaded}");
    }

    static string? Describe(OwnerReading reading) =>
        reading switch {
            OwnerReading.Root(var path)           => $"belongs to the installation at {path}",
            OwnerReading.Unreadable(var reason)   => $"cannot be read ({reason})",
            _                                     => null,
        };

    static LocalInstallFailure Leftover(string what) =>
        Refuse(LocalInstallFailureCode.LeftoverState, $"Found leftover state from another installation: {what}. Nothing was changed; remove it or uninstall that installation first.");

    static void CheckVersion(string version) {
        if (ReleaseManifest.IsValidVersion(version) && !version.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return;

        throw Refuse(LocalInstallFailureCode.InvalidArguments, $"\"{version}\" is not a version of the form 1.2.3 or 1.2.3-beta.1.");
    }

    static LocalInstallFailure Refuse(LocalInstallFailureCode code, string message) => new(code, message);

    /// <summary>The deepest existing ancestor resolved through symlinks, with the components that do not
    /// exist yet appended.</summary>
    static string ResolvedDirectory(string directory) {
        var full      = Path.GetFullPath(directory);
        var remainder = new List<string>();
        var existing  = full;

        while (!Path.Exists(existing) && Path.GetDirectoryName(existing) is { } parent) {
            remainder.Insert(0, Path.GetFileName(existing));
            existing = parent;
        }

        if (!CanonicalPath.TryResolve(existing, out var resolved) || string.IsNullOrEmpty(resolved))
            throw Refuse(LocalInstallFailureCode.UnsafeRoot, $"{existing} cannot be resolved (too many symbolic links). Set {LocalRoot.EnvVar} to a directory under your home.");

        return remainder.Count == 0 ? resolved : Path.Combine([resolved, .. remainder]);
    }
}
