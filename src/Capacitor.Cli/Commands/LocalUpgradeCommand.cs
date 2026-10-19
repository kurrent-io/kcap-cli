using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary><c>kcap local upgrade</c>: moves the stack forward to this kcap's version. <c>kcap update</c> runs it with
/// the new binary, and the desktop app runs it with <c>--json</c> after its own update; a hook never does.</summary>
sealed class LocalUpgradeCommand(ILocalStackFactory stacks) {
    /// <summary>The stack runs this kcap's version.</summary>
    public const int Upgraded = 0;

    /// <summary>Nothing was switched: the old version is still selected, and running again if it was.</summary>
    public const int Failed = 1;

    /// <summary>The new version is selected and left stopped, or its switch is journaled for the next command.</summary>
    public const int FailedAfterSwitch = 2;

    /// <summary>Already at this version, newer than it, or not installed.</summary>
    public const int NothingToDo = 3;

    /// <summary>No bundle is published for this version yet, so nothing changed. A CLI release can reach its channel
    /// before its bundle does; <c>kcap update</c> treats this as a note, not a failure.</summary>
    public const int NotPublished = 4;

    static readonly string[] Flags = ["--json", "--if-installed", "--no-update-check"];

    /// <summary>Overrides this kcap's own version, as <see cref="LocalBundleClient.VersionEnvVar"/> does.</summary>
    internal string Version { get; init; } = LocalBundleClient.RequestedVersion();

    public async Task<int> HandleAsync(string[] args) {
        var json        = args.Contains("--json");
        var ifInstalled = args.Contains("--if-installed");

        void Emit(LocalInstallEvent e) {
            if (json) {
                Console.Out.WriteLine(LocalSetupJsonLines.Line(e));
                Console.Out.Flush();
            } else if (!(ifInstalled && e is LocalInstallEvent.NothingToDo { Reason: UpgradeSkip.NotInstalled })
                    && LocalSetupTerminal.Line(e, Version) is { } line) {
                Console.Out.WriteLine($"  {line}");
            }
        }

        int Fail(LocalInstallFailureCode code, string message, bool switched = false) {
            if (json) {
                Console.Out.WriteLine(LocalSetupJsonLines.Failed(code, message, switched));
                Console.Out.Flush();
            } else {
                Console.Error.WriteLine($"  Error: {message}");
            }

            return switched ? FailedAfterSwitch : code == LocalInstallFailureCode.BundleNotPublished ? NotPublished : Failed;
        }

        if (args.Skip(2).FirstOrDefault(a => !Flags.Contains(a)) is { } unknown)
            return Fail(LocalInstallFailureCode.InvalidArguments, $"Unknown option for kcap local upgrade: {unknown}");

        if (!ReleaseManifest.IsValidVersion(Version))
            return Fail(LocalInstallFailureCode.InvalidArguments,
                $"This kcap ({Version}) is not a release build, so it names no bundle. Set {LocalBundleClient.VersionEnvVar} to a published version.");

        LocalStack stack;

        try {
            stack = stacks.Create();
        } catch (LocalInstallFailure unsupported) when (unsupported.Code == LocalInstallFailureCode.UnsupportedPlatform) {
            // No installation can exist on a platform the local edition does not run on.
            Emit(new LocalInstallEvent.NothingToDo(UpgradeSkip.NotInstalled, Version, null));
            return NothingToDo;
        } catch (LocalInstallFailure failure) {
            return Fail(failure.Code, failure.Message);
        }

        // Deferred so an interrupt still reports how the upgrade ended; only the download stops for it.
        using var deferral = InteractiveLifetime.Defer();

        var upgrade = new LocalUpgrade(stack, new SyncProgress<LocalInstallEvent>(Emit));

        try {
            var result = await upgrade.RunAsync(stacks.Stager, Version, deferral.Token);

            return result is LocalInstallEvent.Upgraded ? Upgraded : NothingToDo;
        } catch (LocalUpgradeFailure failure) {
            return Fail(failure.Code, failure.Message, failure.Switched);
        } catch (LocalLifecycleException ex) when (ex.InnerException is IOException) {
            return Fail(LocalInstallFailureCode.LifecycleBusy, ex.Message);
        } catch (LocalLifecycleException ex) {
            return Fail(LocalInstallFailureCode.RecoveryFailed, ex.Message);
        } catch (LocalInstallFailure failure) {
            return Fail(failure.Code, failure.Message);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A journal or registry write: the journal it may have left is finished by the next lifecycle command.
            return Fail(LocalInstallFailureCode.StartFailed, ex.Message, upgrade.Switched);
        }
    }
}
