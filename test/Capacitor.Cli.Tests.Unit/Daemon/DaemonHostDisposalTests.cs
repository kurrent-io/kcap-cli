// test/Capacitor.Cli.Tests.Unit/Daemon/DaemonHostDisposalTests.cs
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Pty.Unix;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Regression coverage for the production shutdown hang: a DI-owned <see cref="UnixSpawnerThread"/>
/// starts a foreground (<c>IsBackground = false</c>) OS thread that parks on its queue until
/// <see cref="UnixSpawnerThread.Dispose"/> runs. <see cref="IHost.StopAsync"/> only stops registered
/// <see cref="IHostedService"/>s — it never disposes the ServiceProvider — so a shutdown path that
/// calls <c>StopAsync</c> without also disposing the host leaves the thread (and therefore the whole
/// process) alive forever. These tests build a minimal host with the same
/// <c>AddSingleton&lt;UnixSpawnerThread&gt;()</c> registration <c>DaemonRunner.RunAsync</c> uses and
/// drive the exact StopAsync/dispose sequence to prove: (1) StopAsync alone is not enough, and
/// (2) <see cref="DaemonRunner.DisposeHostAsync"/> — the fix — retires the thread.
/// </summary>
public class DaemonHostDisposalTests {
    [Test]
    public async Task StopAsync_alone_leaves_the_spawner_thread_alive() {
        if (OperatingSystem.IsWindows()) return;

        var host    = BuildHost();
        var spawner = host.Services.GetRequiredService<UnixSpawnerThread>();

        await host.StartAsync();
        await Assert.That(spawner.IsThreadAlive).IsTrue();

        await host.StopAsync();

        // The bug: StopAsync stops IHostedServices, not plain AddSingleton<T> IDisposables.
        // Without a subsequent host disposal the foreground thread is still parked on its
        // BlockingCollection, exactly as it is in production between WaitForShutdownAsync
        // returning and a StopAsync-only cleanup path.
        await Assert.That(spawner.IsThreadAlive).IsTrue();

        // Clean up so the test process itself can exit.
        await DaemonRunner.DisposeHostAsync(host);
    }

    [Test]
    public async Task DisposeHostAsync_after_StopAsync_retires_the_spawner_thread() {
        if (OperatingSystem.IsWindows()) return;

        var host    = BuildHost();
        var spawner = host.Services.GetRequiredService<UnixSpawnerThread>();

        await host.StartAsync();
        await Assert.That(spawner.IsThreadAlive).IsTrue();

        await host.StopAsync();
        await DaemonRunner.DisposeHostAsync(host);

        // The fix: disposing the host disposes the ServiceProvider, which disposes the
        // UnixSpawnerThread singleton, which calls CompleteAdding() and joins the thread.
        await Assert.That(spawner.IsThreadAlive).IsFalse();
    }

    static IHost BuildHost() {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<UnixSpawnerThread>();
        return builder.Build();
    }
}
