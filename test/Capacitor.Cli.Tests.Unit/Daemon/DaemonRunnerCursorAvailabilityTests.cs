// test/Capacitor.Cli.Tests.Unit/Daemon/DaemonRunnerCursorAvailabilityTests.cs
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// AI-689 A3: <c>DaemonRunner.RunAsync</c> silently omits an unavailable vendor from
/// <c>DaemonConfig.SupportedVendors</c> (correct — the launch dialog just won't offer it), but gave
/// operators no clue WHY when that vendor is Cursor. <see cref="DaemonRunner.ShouldWarnCursorUnavailable"/>
/// is the pure predicate extracted from that startup seam so it's testable without spinning up the
/// full DI host <c>RunAsync</c> builds — this only proves the predicate; the actual
/// <c>LogCursorUnavailable</c> Warning call at the <c>RunAsync</c> call site is not independently
/// unit-tested (would require a full host boot).
/// </summary>
public class DaemonRunnerCursorAvailabilityTests {
    /// <summary>Minimal <see cref="IHostedAgentRuntimeFactory"/> stand-in — only <see cref="Vendor"/>/<see cref="IsAvailable"/> matter here.</summary>
    sealed class FakeRuntimeFactory(string vendor, bool isAvailable) : IHostedAgentRuntimeFactory {
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended => false;

        public bool IsAvailable() => isAvailable;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("not exercised by this test");
    }

    [Test]
    public async Task ShouldWarnCursorUnavailable_CursorFactoryUnavailable_ReturnsTrue() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true),
            new FakeRuntimeFactory("cursor", isAvailable: false),
        ];

        await Assert.That(DaemonRunner.ShouldWarnCursorUnavailable(factories)).IsTrue();
    }

    [Test]
    public async Task ShouldWarnCursorUnavailable_CursorFactoryAvailable_ReturnsFalse() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true),
            new FakeRuntimeFactory("cursor", isAvailable: true),
        ];

        await Assert.That(DaemonRunner.ShouldWarnCursorUnavailable(factories)).IsFalse();
    }

    [Test]
    public async Task ShouldWarnCursorUnavailable_NoCursorFactoryRegistered_ReturnsFalse() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true),
            new FakeRuntimeFactory("codex", isAvailable: false),
        ];

        await Assert.That(DaemonRunner.ShouldWarnCursorUnavailable(factories)).IsFalse();
    }
}
