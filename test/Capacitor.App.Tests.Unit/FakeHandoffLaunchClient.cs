using System.Collections.Frozen;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// A launch client whose server lists handoff/1 and accepts every launch under one agent id.
sealed class FakeHandoffLaunchClient : ILaunchClient {
    public const string LaunchedId = "0123456789abcdef0123456789abcdef";

    public IReadOnlySet<string>? Features = FrozenSet.Create("handoff/1");
    public List<LaunchRequest> Requests { get; } = [];
    public LaunchOutcome Next = new(true, LaunchedId, null);
    public Action<LaunchRequest>? During;
    public Exception? Throw;

    public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) => Task.FromResult(Features);

    public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
        Requests.Add(request);
        During?.Invoke(request);
        return Throw is { } ex ? Task.FromException<LaunchOutcome>(ex) : Task.FromResult(Next);
    }
}
