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
    // The (ushort, bool) overload with true, or assignRandomHostPort named, hands the host port back to Docker.
    [GeneratedRegex(@"\.WithPortBinding\s*\((?!\s*ContainerHostPorts\.Next\s*\(\s*\)\s*,(?!\s*(?:true\b|assignRandomHostPort\s*:)))")]
    private static partial Regex DockerChosenBinding();

    // A target-typed new counts as a builder only directly after the declaration or a top-level conditional's ? or :,
    // never after a colon inside an argument list, such as a named argument.
    [GeneratedRegex(@"new\s+(?:[\w.]+\.)?(?:KurrentDb|PostgreSql)Builder\s*\(|(?:[\w.]+\.)?(?:KurrentDb|PostgreSql)Builder\??\s+\w+\s*=\s*(?:(?:[^;=()]|\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\))*?[?:]\s*)?new\s*\(")]
    private static partial Regex PublishingBuilder();

    [GeneratedRegex(@"\G(?:new\s+(?:[\w.]+\.)?(?:KurrentDb|PostgreSql)Builder\s*\(|(?:[\w.]+\.)?(?:KurrentDb|PostgreSql)Builder\??\s+\w+\s*=\s*(?:(?:[^;=()]|\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\))*?[?:]\s*)?new\s*\()")]
    private static partial Regex PublishingBuilderHere();

    [GeneratedRegex(@"\.WithPortBinding\s*\(\s*ContainerHostPorts\.Next\s*\(\s*\)\s*,(?!\s*(?:true\b|assignRandomHostPort\s*:))")]
    private static partial Regex PickedBinding();

    [GeneratedRegex(@"\G\.Build\s*\(\s*\)")]
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
    [Arguments("var q = '\"'; new KurrentDbBuilder(image).Build(); var s = \"x\";")]
    [Arguments("/* \" */ new KurrentDbBuilder(image).Build(); var s = \"x\";")]
    [Arguments(".WithPortBinding(ContainerHostPorts.Next(), true)")]
    [Arguments("new Testcontainers.KurrentDb.KurrentDbBuilder(image).Build()")]
    [Arguments("KurrentDbBuilder? b = new(image); b.Build()")]
    [Arguments("new KurrentDbBuilder(image).WithCreateParameterModifier(_ => Side(new ContainerBuilder(i).WithPortBinding(ContainerHostPorts.Next(), 80))).Build()")]
    [Arguments("var s = \"\"\"\"x\"\"\"\"; new KurrentDbBuilder(image).Build(); var t = \"\"\"\"y\"\"\"\";")]
    [Arguments("(new KurrentDbBuilder(image), new ContainerBuilder(i).WithPortBinding(ContainerHostPorts.Next(), 80))")]
    [Arguments("var b = flag ? new KurrentDbBuilder(image) : new KurrentDbBuilder(image).WithPortBinding(ContainerHostPorts.Next(), 2113);")]
    [Arguments("KurrentDbBuilder b = flag ? new(image) : new KurrentDbBuilder(image).WithPortBinding(ContainerHostPorts.Next(), 2113);")]
    [Arguments("KurrentDbBuilder b = Ready(x) ? new(image) : Make();")]
    public async Task Pattern_flags_a_port_docker_chooses(string code) =>
        await Assert.That(PublishesOnDockersChoice(code)).IsTrue();

    [Test]
    [Arguments(".WithPortBinding(ContainerHostPorts.Next(), Port)")]
    [Arguments("new KurrentDbBuilder(image).WithPortBinding(ContainerHostPorts.Next(), 2113).Build()")]
    [Arguments("new KurrentDbBuilder(image).WithCreateParameterModifier(p => { p.Name = \"k\"; }).WithPortBinding(ContainerHostPorts.Next(), 2113).Build()")]
    [Arguments("// .WithPortBinding(2113, true)\n.WithPortBinding(ContainerHostPorts.Next(), Port)")]
    [Arguments("new KurrentDbBuilder(ImageFactory().Build()).WithPortBinding(ContainerHostPorts.Next(), 2113).Build()")]
    [Arguments("new KurrentDbBuilder(image).WithX(new PostgreSqlBuilder(p).WithPortBinding(ContainerHostPorts.Next(), 5432).Build()).WithPortBinding(ContainerHostPorts.Next(), 2113).Build()")]
    [Arguments("KurrentDbBuilder b = Create(options: new(image));")]
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
        var code = SourceCode.WithoutCommentsOrStrings(source);

        return DockerChosenBinding().IsMatch(code)
            || PublishingBuilder().Matches(code).Any(builder => !BindsItsOwnPort(code, builder));
    }

    /// <summary>Whether the builder's own chain, from its construction to its <c>.Build()</c> or the end of the expression
    /// holding it (a <c>;</c>, a <c>,</c>, a conditional's <c>?</c> or <c>:</c>, a sibling builder, or the bracket closing
    /// around it), carries a picked binding. Only a call chained on the builder itself counts, outside every bracket
    /// opened after it: a binding, a <c>.Build()</c> or another builder inside an argument or a lambda in the chain
    /// belongs to something else, and a binding added in a later statement does not count.</summary>
    static bool BindsItsOwnPort(string code, Match builder) {
        var start = builder.Index + builder.Length;

        // Counting from 1 inside the builder's constructor call.
        var depth = 1;

        for (var i = start; i < code.Length; i++) {
            switch (code[i]) {
                case '(' or '[' or '{': depth++; continue;
                case ')' or ']' or '}' when depth == 0: return false;
                case ')' or ']' or '}': depth--; continue;
                case ';' or ',' or ':' when depth == 0: return false;
                case '?' when depth == 0 && (i + 1 >= code.Length || code[i + 1] != '.'): return false;
            }

            if (depth != 0) continue;

            // A sibling builder at the same depth, such as the other branch of a conditional, has a chain of its own.
            if (PublishingBuilderHere().IsMatch(code[i..Math.Min(code.Length, i + 200)])) return false;

            if (code[i] != '.') continue;

            if (Build().IsMatch(code[i..Math.Min(code.Length, i + 16)])) return false;

            if (PickedBinding().Match(code, i) is { Success: true } binding && binding.Index == i) return true;
        }

        return false;
    }

    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException($"Could not locate repo root walking up from {here}");
    }
}
