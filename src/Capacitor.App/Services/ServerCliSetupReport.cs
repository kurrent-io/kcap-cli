using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services;

/// <summary>
/// Tells the server this user has a local install, which is what clears the web dashboard's
/// "run kcap setup" welcome modal. The app's onboarding never runs <c>kcap setup</c>, so without
/// this an app-only user is told to install a CLI they already have.
/// </summary>
public static class ServerCliSetupReport {
    /// True when the server accepted the report. Every failure is a miss, never a throw: the report
    /// only changes what the dashboard nudges, and the next start or sign-in sends it again.
    public static Func<CancellationToken, Task<bool>> Sender(
            ICapacitorHttpClient? http, ProfileContext? profiles, string? version) => async ct => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return false;

        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                // A server that needs no sign-in has no user to record the install against.
                if (status is not AuthStatus.Ok) return false;

                using var body = new StringContent(
                    CliSetupReport.Body(version, joinId: null), System.Text.Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(CliSetupReport.Url(serverUrl), body, ct).ConfigureAwait(false);

                return response.IsSuccessStatusCode;
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return false;
        } catch (Exception) {
            return false;
        }
    };
}
