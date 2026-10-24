using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>Every container a test starts is published below Linux's ephemeral range, from which Docker's own choice
/// and a port-0 bind are drawn, on a port nothing here holds. The pick walks a process-wide cursor, so the test that
/// needs to know which port comes next runs alone.</summary>
public class ContainerHostPortsTests {
    const int EphemeralFloor = 32768;

    [Test]
    [NotInParallel]
    public async Task A_full_cycle_of_picks_stays_in_range_and_never_hands_out_a_held_port() {
        var held = ContainerHostPorts.Next() + 1;
        if (held > ContainerHostPorts.Last) held = ContainerHostPorts.First;

        using var holder = new TcpListener(IPAddress.Any, held);
        try {
            holder.Start();
        } catch (SocketException) {
            // something else already holds it, which serves as well
        }

        var drawn = Enumerable.Range(0, ContainerHostPorts.Last - ContainerHostPorts.First + 1)
            .Select(_ => (int)ContainerHostPorts.Next())
            .ToList();

        await Assert.That(drawn).DoesNotContain(held);
        await Assert.That(drawn.All(p => p is >= ContainerHostPorts.First and < EphemeralFloor)).IsTrue();
    }

    /// <summary>The resource reaper starts with the first container of the process, so it is running by the time the
    /// emulator is up.</summary>
    [Test]
    [Timeout(300_000)]
    public async Task The_emulator_and_the_resource_reaper_publish_below_the_ephemeral_range(CancellationToken ct) {
        Skip.When(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && !OperatingSystem.IsLinux(),
            "this CI runner cannot run Linux containers");

        var unavailable = await WorkOSEmulator.UnavailableReasonAsync();
        Skip.When(unavailable is not null, unavailable ?? "");

        await using var emulator = await WorkOSEmulator.StartAsync(ct);

        using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.Empty).Build();
        var reaper = await docker.Containers.InspectContainerAsync($"testcontainers-ryuk-{ResourceReaper.DefaultSessionId:D}", ct);
        var reaperPort = int.Parse(reaper.NetworkSettings.Ports["8080/tcp"][0].HostPort, CultureInfo.InvariantCulture);

        await Assert.That(new Uri(emulator.BaseUrl).Port).IsLessThan(EphemeralFloor);
        await Assert.That(reaperPort).IsLessThan(EphemeralFloor);
    }
}
