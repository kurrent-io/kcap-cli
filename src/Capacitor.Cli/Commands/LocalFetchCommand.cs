using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary><c>kcap local fetch</c>: downloads and verifies this kcap's bundle into the installation's cache, so the
/// install that follows takes it instead of downloading. The desktop app runs it with <c>--json</c> while the user is
/// still choosing an edition. It installs nothing and takes no lock.</summary>
sealed class LocalFetchCommand(ILocalStackFactory stacks) {
    public const int Fetched = 0;

    public const int Failed = 1;

    /// <summary>The installation already runs this version, so there is nothing to fetch.</summary>
    public const int NothingToDo = 3;

    /// <summary>No bundle is published for this version yet.</summary>
    public const int NotPublished = 4;

    static readonly string[] Flags = ["--json", "--no-update-check"];

    /// <summary>Overrides this kcap's own version, as <see cref="LocalBundleClient.VersionEnvVar"/> does.</summary>
    internal string Version { get; init; } = LocalBundleClient.RequestedVersion();

    internal LocalPlatform Platform { get; init; } = LocalPlatform.Current();

    public async Task<int> HandleAsync(string[] args) {
        var json = args.Contains("--json");

        void Emit(LocalInstallEvent e) {
            if (json) {
                Console.Out.WriteLine(LocalSetupJsonLines.Line(e));
                Console.Out.Flush();
            } else if (e is not LocalInstallEvent.DownloadProgress && LocalSetupTerminal.Line(e, Version) is { } line) {
                Console.Out.WriteLine($"  {line}");
            }
        }

        int Fail(LocalInstallFailureCode code, string message) {
            if (json) {
                Console.Out.WriteLine(LocalSetupJsonLines.Failed(code, message));
                Console.Out.Flush();
            } else {
                Console.Error.WriteLine($"  Error: {message}");
            }

            return code == LocalInstallFailureCode.BundleNotPublished ? NotPublished : Failed;
        }

        if (args.Skip(2).FirstOrDefault(a => !Flags.Contains(a)) is { } unknown)
            return Fail(LocalInstallFailureCode.InvalidArguments, $"Unknown option for kcap local fetch: {unknown}");

        if (!ReleaseManifest.IsValidVersion(Version))
            return Fail(LocalInstallFailureCode.InvalidArguments,
                $"This kcap ({Version}) is not a release build, so it names no bundle. Set {LocalBundleClient.VersionEnvVar} to a published version.");

        if (!Platform.SupportsLocalEdition)
            return Fail(LocalInstallFailureCode.UnsupportedPlatform, "The local edition runs on Apple silicon Macs and x64 Windows for now.");

        if (LocalStateFiles.ReadRegistry(stacks.Root) is StateRead<InstallRegistry>.Present { Value.Active.Version: var installed }
         && installed == Version && LocalBin.Has(stacks.Root, Version)) {
            Emit(new LocalInstallEvent.NothingToDo(UpgradeSkip.Current, Version, installed));
            return NothingToDo;
        }

        using var deferral = InteractiveLifetime.Defer();

        try {
            await stacks.Stager().FetchAsync(Version, new SyncProgress<LocalInstallEvent>(Emit), deferral.Token);

            return Fetched;
        } catch (OperationCanceledException) when (deferral.Token.IsCancellationRequested) {
            return Fail(LocalInstallFailureCode.Cancelled, "The download was cancelled.");
        } catch (LocalBundleException ex) {
            return Fail(ex.Code, ex.Message);
        } catch (LocalInstallFailure ex) {
            return Fail(ex.Code, ex.Message);
        } catch (Exception ex) when (ex is LocalLifecycleException or IOException or UnauthorizedAccessException) {
            return Fail(LocalInstallFailureCode.DownloadFailed, ex.Message);
        }
    }
}
