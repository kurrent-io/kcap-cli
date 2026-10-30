using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Passes every resolved credential to <paramref name="observe"/> on its way to the caller.</summary>
sealed class ObservedCredentialSource(ICredentialSource inner, Action<CredentialState> observe) : ICredentialSource {
    public async Task<CredentialState> ResolveAsync(CancellationToken ct) => Seen(await inner.ResolveAsync(ct));

    public async Task<CredentialState> RotateAsync(string? refused, CancellationToken ct) => Seen(await inner.RotateAsync(refused, ct));

    CredentialState Seen(CredentialState state) {
        observe(state);
        return state;
    }
}
