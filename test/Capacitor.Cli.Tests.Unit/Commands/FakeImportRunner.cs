using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Records what the setup Import step asked for instead of running an import, and answers with the
/// outcome the test is pinning. <see cref="Calls"/> is what proves a skip decision skipped.
/// </summary>
sealed class FakeImportRunner : ISetupImportRunner {
    readonly Func<ImportInvocation, SetupImportRun> _run;
    Func<ProfileContext, SetupImportDiscovery>      _discover = _ => new(null, null);

    FakeImportRunner(Func<ImportInvocation, SetupImportRun> run) => _run = run;

    public static FakeImportRunner Succeeding(int selected = 3, bool remainder = true) => new(_ => {
        var ids = Enumerable.Range(0, selected).Select(i => $"s{i}").ToList();

        return new SetupImportRun(0,
            new ImportRunSelection([.. ids, "rest"], ids, remainder),
            new ImportCommand.ImportRunOutcome(ZeroCounts, 0, new ImportRunPartition(ids, [], [])),
            null);
    });

    public static FakeImportRunner Returning(int exitCode) => new(_ => new SetupImportRun(
        exitCode, ImportRunSelection.Empty,
        new ImportCommand.ImportRunOutcome(ZeroCounts, 0, ImportRunPartition.Empty), null));

    public static FakeImportRunner Faulting(Exception boom) => new(_ => new SetupImportRun(1, null, null, boom));
    public static FakeImportRunner Of(Func<ImportInvocation, SetupImportRun> run) => new(run);

    public FakeImportRunner Discovering(ImportCommand.ImportDiscoveryResult result) { _discover = _ => new(result, null); return this; }
    public FakeImportRunner DiscoveryFaulting(Exception boom)                      { _discover = _ => new(null, boom);   return this; }

    public ImportInvocation? Captured { get; private set; }

    public int Calls { get; private set; }
    public int DiscoverCalls { get; private set; }

    public Task<SetupImportDiscovery> DiscoverAsync(ProfileContext profiles) { DiscoverCalls++; return Task.FromResult(_discover(profiles)); }

    public Task<SetupImportRun> RunAsync(ImportInvocation invocation) {
        Captured = invocation;
        Calls++;

        return Task.FromResult(_run(invocation));
    }

    internal static readonly ImportCommand.FinalCounts ZeroCounts = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, RanBackground: false, RequestedSummaries: false);
}
