using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Docker's own host-port choice, and a port-0 bind, share Linux's ephemeral range with a loaded runner's sockets, so
/// every published container port takes its host port from <c>ContainerHostPorts.Next()</c>. Pinned as a guard because
/// a binding on Docker's choice passes everywhere a machine is quiet and fails only on a busy runner. The KurrentDB and
/// Postgres builders publish their port on Docker's choice unless told otherwise, so each builder's own chain needs a
/// picked binding in the statement that constructs it.
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
    [Arguments("KurrentDbBuilder b = new(image); b = b.WithPortBinding(ContainerHostPorts.Next(), 2113); b.Build()")]
    [Arguments("KurrentDbBuilder Unbuilt() => new KurrentDbBuilder(image); var c = new ContainerBuilder(i).WithPortBinding(ContainerHostPorts.Next(), 80).Build();")]
    [Arguments("Pair(new KurrentDbBuilder(a), new PostgreSqlBuilder(b).WithPortBinding(ContainerHostPorts.Next(), 5432).Build())")]
    [Arguments("new KurrentDbBuilder(image).WithEnvironment(\"NOTE\", \".WithPortBinding(ContainerHostPorts.Next(), 2113)\").Build()")]
    public async Task Pattern_flags_a_port_docker_chooses(string code) =>
        await Assert.That(PublishesOnDockersChoice(code)).IsTrue();

    [Test]
    [Arguments(".WithPortBinding(ContainerHostPorts.Next(), Port)")]
    [Arguments("new KurrentDbBuilder(image).WithPortBinding(ContainerHostPorts.Next(), 2113).Build()")]
    public async Task Pattern_passes_a_port_picked_below_the_ephemeral_range(string code) =>
        await Assert.That(PublishesOnDockersChoice(code)).IsFalse();

    [Test]
    public async Task No_test_publishes_a_container_port_on_dockers_choice() {
        var root = RepoRoot();

        var offenders = Directory.EnumerateFiles(Path.Combine(root, "test"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .Where(path => PublishesOnDockersChoice(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        await Assert.That(offenders).IsEmpty()
            .Because("bind each published port with .WithPortBinding(ContainerHostPorts.Next(), containerPort)");
    }

    static bool PublishesOnDockersChoice(string source) {
        var code = SourceCode.OutsideStrings(source);

        return DockerChosenBinding().IsMatch(code)
            || PublishingBuilder().Matches(code).Any(builder => !PickedBinding().IsMatch(ChainOf(code, builder)));
    }

    /// <summary>The code from a builder's construction to whichever comes first: its <c>.Build()</c>, the end of its
    /// statement, or the next builder's construction. A binding added in a later statement does not count.</summary>
    static string ChainOf(string code, Match builder) {
        var start = builder.Index + builder.Length;

        int[] ends = [
            Build().Match(code, start) is { Success: true } build ? build.Index : code.Length,
            code.IndexOf(';', start) is >= 0 and var statement ? statement : code.Length,
            PublishingBuilder().Match(code, start) is { Success: true } next ? next.Index : code.Length
        ];

        return code[start..ends.Min()];
    }

    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException($"Could not locate repo root walking up from {here}");
    }
}
