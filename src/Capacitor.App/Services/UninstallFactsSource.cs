using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Services;

/// Gathers what the uninstall confirmation names. A fact that cannot be read is logged and left
/// out, and the terminal PATH gets a deadline, so neither a failure nor a slow login shell keeps
/// the confirmation from showing. A list that cannot be read is null, so the confirmation can tell
/// "none" from "unknown".
public sealed record UninstallFactsSource(
    Func<string?> LocalServerUrl,
    string AttachedDaemon,
    Func<DaemonActivity?> Activity,
    Func<IReadOnlyList<string>> LiveDaemons,
    Func<string?> HostedServerUrl,
    Func<CancellationToken, Task<string?>> TerminalPath,
    string BundleRoot,
    TimeSpan TerminalPathTimeout,
    TimeProvider Time,
    TextWriter Log,
    Func<IReadOnlyList<string>>? PathProfiles = null) {
    public async Task<UninstallFacts> GatherAsync(CancellationToken ct) {
        var terminalPath = await ReadTerminalPathAsync(ct).ConfigureAwait(false);

        return new UninstallFacts(
            Read(LocalServerUrl, "the local server", null),
            AttachedDaemon,
            Read(Activity, "the daemon's activity", null),
            Read<IReadOnlyList<string>?>(LiveDaemons, "the running daemons", null),
            Read(HostedServerUrl, "the server", null),
            terminalPath is null
                ? null
                : Read<IReadOnlyList<OtherKcapInstall>?>(() => OtherKcapInstalls.Find(terminalPath, BundleRoot), "other kcap installs", null),
            PathProfiles is null ? [] : Read<IReadOnlyList<string>?>(PathProfiles, "shell profiles", null));
    }

    async Task<string?> ReadTerminalPathAsync(CancellationToken ct) {
        using var deadline = new CancellationTokenSource(TerminalPathTimeout, Time);
        using var bound    = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try {
            return await TerminalPath(bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            Log.WriteLine("kcap: the terminal PATH took too long to read for the uninstall");
            return null;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Log.WriteLine($"kcap: could not read the terminal PATH for the uninstall: {ex.Message}");
            return null;
        }
    }

    T Read<T>(Func<T> read, string what, T fallback) {
        try {
            return read();
        } catch (Exception ex) {
            Log.WriteLine($"kcap: could not read {what} for the uninstall: {ex.Message}");
            return fallback;
        }
    }
}
