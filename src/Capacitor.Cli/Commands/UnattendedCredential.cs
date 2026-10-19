using System.Net;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Commands;

/// <summary>
/// What a <c>kcap setup --no-prompt</c> run authenticates with before any person is asked: the machine credential when
/// one is set, else a token already stored for the chosen server.
/// </summary>
/// <remarks>
/// A machine credential that does not work is a failure, not a reason to fall back to a person: the operator who set
/// it meant this machine to have no one at it, and a device code would wait for a sign-in nobody will make.
/// </remarks>
sealed class UnattendedCredential(ChosenServerHttp http) {
    internal abstract record Outcome {
        public sealed record Machine : Outcome;

        public sealed record Stored : Outcome;

        public sealed record Rejected(string Problem) : Outcome;

        public sealed record SignInNeeded : Outcome;
    }

    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public async Task<Outcome> ProbeAsync(string serverUrl, ProfileContext chosen, TimeProvider time, CancellationToken ct = default) {
        var machine = http.Machine.Intended;

        await using var scoped = http.For(serverUrl, chosen);

        var (client, status, problem, _) = await scoped.GetRequiredService<ICapacitorHttpClient>().ForWaitAsync(ct);

        using (client) {
            if (status is not AuthStatus.Ok)
                return machine
                    ? new Outcome.Rejected(problem ?? $"no token could be obtained ({status})")
                    : new Outcome.SignInNeeded();

            HttpStatusCode? answer;

            try {
                using var response = await client.GetOnceAsync(
                    $"{AppConfig.NormalizeUrl(serverUrl)}{WhoamiCommand.ProbePath}", time, ProbeTimeout, ct);

                answer = response.StatusCode;
            } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) {
                answer = null;
            }

            // Only a 401 or 403 is the server's verdict on the credential; anything else failed to ask, and the
            // steps after this one report their own trouble with the server.
            var refused = answer is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

            return (machine, refused) switch {
                (true, true)  => new Outcome.Rejected($"{serverUrl} refused the machine's token (HTTP {(int)answer!.Value})"),
                (true, false) => new Outcome.Machine(),
                (false, true) => new Outcome.SignInNeeded(),
                _             => new Outcome.Stored(),
            };
        }
    }
}
