using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Hidden command spawned by the npm wrapper right after <c>kcap update</c> installs a new binary: sends
/// <see cref="ServerProbe"/> so the server's endpoint-agnostic version-observer middleware sees the new
/// <see cref="HttpClientExtensions.CliVersionHeader"/> immediately. Never prints, always returns 0.
/// </summary>
public sealed class ReportVersionCommand(CapacitorServer server, ICapacitorHttpClient http) {
    public async Task<int> HandleAsync() {
        await ServerProbe.SendAsync(server, http);

        return 0;
    }
}
