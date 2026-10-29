using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// WorkOS Emulate in a container, run without <c>--interactive</c> so its authorize endpoint
/// redirects straight back with a code for the first seeded user.
/// </summary>
public sealed class WorkOSEmulator : IAsyncDisposable {
    public const string ClientId       = "client_kcapdev";
    public const string OwnerId        = "user_01K0000000000000000000000A";
    public const string OrganizationId = "org_01K0000000000000000000000K";

    const int    Port      = 4100;
    const string SeedPath  = "/app/workos-emulate.config.yaml";

    const string Seed = """
        users:
          - id: user_01K0000000000000000000000A
            email: owner@kcap.test
            first_name: Olive
            last_name: Owner
            email_verified: true
          - id: user_01K0000000000000000000000B
            email: member@kcap.test
            first_name: Max
            last_name: Member
            email_verified: true
        organizations:
          - id: org_01K0000000000000000000000K
            name: Kcap Dev
            memberships:
              - email: owner@kcap.test
                role: owner
              - email: member@kcap.test
                role: member
        """;

    readonly IContainer _container;

    WorkOSEmulator(IContainer container, int hostPort) {
        _container = container;
        BaseUrl    = $"http://127.0.0.1:{hostPort}";
    }

    /// <summary>Where the emulator answers on this machine; loopback, as the CLI requires.</summary>
    public string BaseUrl { get; }

    /// <summary>Why no container can run here, or null when an engine answers.</summary>
    public static async Task<string?> UnavailableReasonAsync() {
        try {
            if (TestcontainersSettings.OS.DockerEndpointAuthConfig is not { } endpoint) return "no container engine is configured";

            using var client  = endpoint.GetDockerClientBuilder(Guid.Empty).Build();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await client.System.PingAsync(timeout.Token);

            return null;
        } catch (Exception e) {
            return $"no container engine answers ({e.GetType().Name})";
        }
    }

    public static async Task<WorkOSEmulator> StartAsync(CancellationToken ct) {
        // The host port stays fixed across a restart, because a stored token names the base URL; something that takes
        // the port while the emulator is stopped fails that one test. Passing any argument replaces the image's
        // defaults, and without --host 0.0.0.0 it listens on its own loopback.
        var container = await ContainerHostPorts.StartAsync(() => new ContainerBuilder(new DockerImage("ghcr.io/workos/emulate", tag: "0.15.0"))
            .WithPortBinding(ContainerHostPorts.Next(), Port)
            .WithResourceMapping(Encoding.UTF8.GetBytes(Seed), SeedPath)
            .WithCommand("--host", "0.0.0.0", "--seed", SeedPath)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(Port).ForPath("/health")))
            .Build(), ct);

        return new WorkOSEmulator(container, container.GetMappedPublicPort(Port));
    }

    /// <summary>Stops and starts the same container on the same host port: the emulator keeps all state in memory, so
    /// every token it issued is forgotten. The port is not retried, so one taken while it is stopped fails the
    /// start.</summary>
    public async Task RestartAsync(CancellationToken ct) {
        await _container.StopAsync(ct);
        await _container.StartAsync(ct);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
