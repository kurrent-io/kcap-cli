using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Tests.Unit.Commands;

sealed class StubAccountProbe(ProbeResult result) : IAccountProbe {
    public static readonly StubAccountProbe SignedIn =
        new(new ProbeResult(SignInStates.SignedIn, new AccountIdentity("me@example.com", null, null, "max", null), null, null, null));

    public static readonly StubAccountProbe Failing = new(new ProbeResult(null, null, "claude not found on PATH", null, null));

    public Task<ProbeResult> ProbeAsync(VendorAccount account, CancellationToken ct) => Task.FromResult(result);
}
