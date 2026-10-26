using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Docker.DotNet;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Host ports for every container a test publishes, Testcontainers' resource reaper included. Docker's own choice, and
/// a port-0 bind, both come from Linux's ephemeral range (32768–60999), which a loaded runner's outbound sockets and
/// their TIME_WAITs crowd, so a start fails with "address already in use" however often it is retried there. Below
/// that range only an explicit bind takes a port, so a pick is checked free here and walks on from where the last one
/// ended, which keeps two starts in one process from racing for the same port.
/// </summary>
public static class ContainerHostPorts {
    public const int First = 20000;
    public const int Last  = 32767;

    const int Span = Last - First + 1;

    /// <summary>How many attempts a container start gets while each failure is a host-port clash.</summary>
    const int MaxAttempts = 10;

    static int _cursor = Random.Shared.Next(Span);

    /// <summary>Marked obsolete, yet the reaper's port binding still reads it; the reaper's own pin fails the day a
    /// Testcontainers upgrade stops honouring it.</summary>
    [ModuleInitializer]
    internal static void PublishTheResourceReaperHere() {
#pragma warning disable CS0618
        TestcontainersSettings.ResourceReaperPublicHostPort = _ => Next();
#pragma warning restore CS0618
    }

    /// <summary>The next port in the range that nothing on this machine holds.</summary>
    public static ushort Next() {
        for (var tried = 0; tried < Span; tried++) {
            var port = First + (int)((uint)Interlocked.Increment(ref _cursor) % Span);
            if (IsFree(port)) return (ushort)port;
        }

        throw new InvalidOperationException($"Every host port from {First} to {Last} is held");
    }

    /// <summary>Starts a container <paramref name="build"/> publishes on a fresh <see cref="Next"/> pick each time,
    /// rebuilding it after each clash with another process that took the picked port first.</summary>
    public static async Task<T> StartAsync<T>(Func<T> build, CancellationToken ct = default) where T : IContainer {
        for (var attempt = 1; ; attempt++) {
            var container = build();

            try {
                await container.StartAsync(ct);

                return container;
            } catch (Exception ex) when (IsClash(ex) && attempt < MaxAttempts) {
                await Console.Error.WriteLineAsync($"{container.Image.FullName}: start attempt {attempt} hit a host-port clash, retrying: {ex.Message}");
                await container.DisposeAsync();
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            } catch {
                await container.DisposeAsync();
                throw;
            }
        }
    }

    /// <summary>"address already in use" when a process holds the picked port, "port is already allocated" when another
    /// container published it first.</summary>
    internal static bool IsClash(Exception ex) =>
        ex is DockerApiException { StatusCode: HttpStatusCode.InternalServerError } api
     && (api.ResponseBody.Contains("address already in use", StringComparison.Ordinal)
      || api.ResponseBody.Contains("port is already allocated", StringComparison.Ordinal));

    // Docker publishes on IPv6 as well as IPv4, so a port held on either is taken. A host without IPv6 refuses that
    // bind for its own reasons, which say nothing about the port.
    static bool IsFree(int port) {
        try {
            using var v4 = new TcpListener(IPAddress.Any, port);
            v4.Start();
        } catch (SocketException) {
            return false;
        }

        if (!Socket.OSSupportsIPv6) return true;

        try {
            using var v6 = new TcpListener(IPAddress.IPv6Any, port);
            v6.Server.DualMode = false;
            v6.Start();
        } catch (SocketException ex) when (ex.SocketErrorCode != SocketError.AddressAlreadyInUse) {
            return true;
        } catch (SocketException) {
            return false;
        }

        return true;
    }
}
