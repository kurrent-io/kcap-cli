using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Records what the setup Import step asked for instead of running an import, and answers with the
/// outcome the test is pinning. <see cref="Calls"/> is what proves a skip decision skipped.
/// </summary>
sealed class FakeImportRunner : ISetupImportRunner {
    readonly Func<ImportInvocation, Task<int>> _behaviour;

    FakeImportRunner(Func<ImportInvocation, Task<int>> behaviour) => _behaviour = behaviour;

    public static FakeImportRunner Succeeding()            => new(_ => Task.FromResult(0));
    public static FakeImportRunner Returning(int exitCode)  => new(_ => Task.FromResult(exitCode));
    public static FakeImportRunner Throwing(Exception boom) => new(_ => throw boom);

    public ImportInvocation? Captured { get; private set; }

    public int Calls { get; private set; }

    public Task<int> RunAsync(ImportInvocation invocation) {
        Captured = invocation;
        Calls++;

        return _behaviour(invocation);
    }
}
