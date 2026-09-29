using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A machine with exactly these harnesses installed.</summary>
sealed class HarnessRegistryStub(params HarnessId[] present) : IHarnessDetection {
    public bool Detected(HarnessId id) => present.Contains(id);
}
