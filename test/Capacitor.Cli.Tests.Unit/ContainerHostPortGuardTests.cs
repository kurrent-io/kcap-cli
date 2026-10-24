using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Docker's own host-port choice, and a port-0 bind, share Linux's ephemeral range with a loaded runner's sockets, so
/// every published container port takes its host port from <c>ContainerHostPorts.Next()</c>. Pinned as a guard because
/// a binding on Docker's choice passes everywhere a machine is quiet and fails only on a busy runner.
/// </summary>
public partial class ContainerHostPortGuardTests {
    [GeneratedRegex(@"\.WithPortBinding\s*\((?!\s*ContainerHostPorts\.Next\s*\(\s*\)\s*,)")]
    private static partial Regex DockerChosenBinding();

    [Test]
    [Arguments(".WithPortBinding(4100, assignRandomHostPort: true)")]
    [Arguments(".WithPortBinding(4100, true)")]
    [Arguments(".WithPortBinding(hostPort, Port)")]
    [Arguments(".WithPortBinding(OAuthLoginFlow.GetAvailablePort(), Port)")]
    public async Task Pattern_flags_a_port_docker_chooses(string code) =>
        await Assert.That(DockerChosenBinding().IsMatch(code)).IsTrue();

    [Test]
    public async Task Pattern_passes_a_port_picked_below_the_ephemeral_range() =>
        await Assert.That(DockerChosenBinding().IsMatch(".WithPortBinding(ContainerHostPorts.Next(), Port)")).IsFalse();

    [Test]
    public async Task No_test_publishes_a_container_port_on_dockers_choice() {
        var root = RepoRoot();

        var offenders = Directory.EnumerateFiles(Path.Combine(root, "test"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .Where(path => path != ThisFile())
            .Where(path => DockerChosenBinding().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        await Assert.That(offenders).IsEmpty()
            .Because("bind each published port with .WithPortBinding(ContainerHostPorts.Next(), containerPort)");
    }

    static string ThisFile([CallerFilePath] string here = "") => here;

    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException($"Could not locate repo root walking up from {here}");
    }
}
