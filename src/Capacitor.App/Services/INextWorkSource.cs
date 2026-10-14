using Capacitor.Cli.Core.NextWork;

namespace Capacitor.App.Services;

public interface INextWorkSource {
    Task<NextWorkRead> ReadAsync(CancellationToken ct);
}
