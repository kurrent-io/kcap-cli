using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

sealed class FixedScope(string? repo, string? machine) : ISessionStartMemoryScopeResolver {
    public Task<SessionStartMemoryScope> ResolveAsync(string? cwd, TimeSpan budget, CancellationToken ct) =>
        Task.FromResult(new SessionStartMemoryScope(repo, machine));
}
