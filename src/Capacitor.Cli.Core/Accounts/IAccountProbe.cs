namespace Capacitor.Cli.Core.Accounts;

public interface IAccountProbe {
    Task<ProbeResult> ProbeAsync(VendorAccount account, CancellationToken ct);
}
