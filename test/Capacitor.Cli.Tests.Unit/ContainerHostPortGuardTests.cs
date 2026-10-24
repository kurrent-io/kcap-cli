using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Docker's own host-port choice, and a port-0 bind, share Linux's ephemeral range with a loaded runner's sockets, so
/// every published container port takes its host port from <c>ContainerHostPorts.Next()</c>. Pinned as a guard because
/// a binding on Docker's choice passes everywhere a machine is quiet and fails only on a busy runner. The KurrentDB and
/// Postgres builders publish their port on Docker's choice unless told otherwise, so each builder's own chain, up to
/// its <c>.Build()</c>, needs a picked binding.
/// </summary>
public partial class ContainerHostPortGuardTests {
    [GeneratedRegex(@"\.WithPortBinding\s*\((?!\s*ContainerHostPorts\.Next\s*\(\s*\)\s*,)")]
    private static partial Regex DockerChosenBinding();

    [GeneratedRegex(@"new\s+(?:KurrentDb|PostgreSql)Builder\s*\(|(?:KurrentDb|PostgreSql)Builder\s+\w+\s*=\s*new\s*\(")]
    private static partial Regex PublishingBuilder();

    [GeneratedRegex(@"\.WithPortBinding\s*\(\s*ContainerHostPorts\.Next\s*\(\s*\)\s*,")]
    private static partial Regex PickedBinding();

    [GeneratedRegex(@"\.Build\s*\(\s*\)")]
    private static partial Regex Build();

    [Test]
    [Arguments(".WithPortBinding(4100, assignRandomHostPort: true)")]
    [Arguments(".WithPortBinding(4100, true)")]
    [Arguments(".WithPortBinding(hostPort, Port)")]
    [Arguments(".WithPortBinding(OAuthLoginFlow.GetAvailablePort(), Port)")]
    [Arguments("new KurrentDbBuilder(image).Build()")]
    [Arguments("KurrentDbBuilder b = new(image); b.Build()")]
    [Arguments("new PostgreSqlBuilder(image).Build(); new ContainerBuilder(i).WithPortBinding(ContainerHostPorts.Next(), 80).Build()")]
    public async Task Pattern_flags_a_port_docker_chooses(string code) =>
        await Assert.That(PublishesOnDockersChoice(code)).IsTrue();

    [Test]
    [Arguments(".WithPortBinding(ContainerHostPorts.Next(), Port)")]
    [Arguments("new KurrentDbBuilder(image).WithPortBinding(ContainerHostPorts.Next(), 2113).Build()")]
    [Arguments("KurrentDbBuilder b = new(image); b = b.WithPortBinding(ContainerHostPorts.Next(), 2113); b.Build()")]
    public async Task Pattern_passes_a_port_picked_below_the_ephemeral_range(string code) =>
        await Assert.That(PublishesOnDockersChoice(code)).IsFalse();

    [Test]
    public async Task No_test_publishes_a_container_port_on_dockers_choice() {
        var root = RepoRoot();

        var offenders = Directory.EnumerateFiles(Path.Combine(root, "test"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .Where(path => path != ThisFile())
            .Where(path => PublishesOnDockersChoice(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        await Assert.That(offenders).IsEmpty()
            .Because("bind each published port with .WithPortBinding(ContainerHostPorts.Next(), containerPort)");
    }

    static bool PublishesOnDockersChoice(string code) =>
        DockerChosenBinding().IsMatch(code)
     || PublishingBuilder().Matches(code).Any(builder => !PickedBinding().IsMatch(ChainOf(code, builder)));

    /// <summary>The code from a builder's construction to its first <c>.Build()</c>, or to the end of the file.</summary>
    static string ChainOf(string code, Match builder) {
        var start = builder.Index + builder.Length;
        var build = Build().Match(code, start);

        return code[start..(build.Success ? build.Index : code.Length)];
    }

    static string ThisFile([CallerFilePath] string here = "") => here;

    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException($"Could not locate repo root walking up from {here}");
    }
}
