using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
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
        var (held, holder) = HoldTheNextPort();
        using var _ = holder;

        var drawn = Enumerable.Range(0, ContainerHostPorts.Last - ContainerHostPorts.First + 1)
            .Select(_ => (int)ContainerHostPorts.Next())
            .ToList();

        await Assert.That(drawn).DoesNotContain(held);
        await Assert.That(drawn.All(p => p is >= ContainerHostPorts.First and < EphemeralFloor)).IsTrue();
    }

    /// <summary>Docker publishes on IPv6 as well as IPv4, so a port something holds on IPv6 alone is just as taken.</summary>
    [Test]
    [NotInParallel]
    public async Task A_port_held_on_ipv6_alone_is_passed_over() {
        Skip.When(!Socket.OSSupportsIPv6, "this host has no IPv6");

        var (held, holder) = HoldTheNextPort(ipv6Only: true);
        using var _ = holder;

        await Assert.That((int)ContainerHostPorts.Next()).IsNotEqualTo(held);
    }

    /// <summary>Docker refuses a taken host port in two words: the kernel's, when another process holds it, and its own,
    /// when another container already published it.</summary>
    [Test]
    [Arguments("failed to bind port 0.0.0.0:33201/tcp: Error starting userland proxy: listen tcp4 0.0.0.0:33201: bind: address already in use", true)]
    [Arguments("Bind for 0.0.0.0:20417 failed: port is already allocated", true)]
    [Arguments("could not kill container: abc is zombie and can not be killed", false)]
    public async Task Only_a_bind_refusal_is_a_host_port_clash(string message, bool clash) =>
        await Assert.That(ContainerHostPorts.IsClash(new DockerApiException(HttpStatusCode.InternalServerError,
            $"{{\"message\":\"driver failed programming external connectivity on endpoint gifted_wozniak (8b56): {message}\"}}"))).IsEqualTo(clash);

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

        await Assert.That(new Uri(emulator.BaseUrl).Port).IsLessThan(EphemeralFloor);

        // A host that turns the reaper off has none to inspect.
        if (!TestcontainersSettings.ResourceReaperEnabled) return;

        using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.Empty).Build();
        var reaper = await docker.Containers.InspectContainerAsync($"testcontainers-ryuk-{ResourceReaper.DefaultSessionId:D}", ct);
        var reaperPort = int.Parse(reaper.NetworkSettings.Ports["8080/tcp"][0].HostPort, CultureInfo.InvariantCulture);

        await Assert.That(reaperPort).IsLessThan(EphemeralFloor);
    }

    /// <summary>Holds the port one past the latest pick, which is the one the next pick lands on. A candidate something
    /// else already holds could be let go mid-test, so the test picks again until it holds one itself.</summary>
    static (int Port, TcpListener Holder) HoldTheNextPort(bool ipv6Only = false) {
        for (var attempt = 0; ; attempt++) {
            var port = ContainerHostPorts.Next() + 1;
            if (port > ContainerHostPorts.Last) port = ContainerHostPorts.First;

            var holder = new TcpListener(ipv6Only ? IPAddress.IPv6Any : IPAddress.Any, port);
            if (ipv6Only) holder.Server.DualMode = false;

            try {
                holder.Start();

                return (port, holder);
            } catch (SocketException) when (attempt < 20) {
                holder.Dispose();
            }
        }
    }
}
