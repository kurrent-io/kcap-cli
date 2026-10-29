using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Testcontainers parses an image string with a regex whose 5-second match timeout a starved runner can exceed; only
/// <c>new DockerImage(repository, tag: …)</c> skips the parse. So a builder or <c>WithImage</c> argument must be that
/// call, matched with string contents blanked so a literal containing <c>tag:</c> cannot pass for the named argument.
/// </summary>
public partial class ContainerImageStringGuardTests {
    [GeneratedRegex(@"new\s+(?:[\w.]+\.)?(?:\w*ContainerBuilder|KurrentDbBuilder|PostgreSqlBuilder)\s*\((?!\s*new\s+DockerImage\s*\()|(?:[\w.]+\.)?(?:\w*ContainerBuilder|KurrentDbBuilder|PostgreSqlBuilder)\??\s+\w+\s*=\s*new\s*\((?!\s*new\s+DockerImage\s*\()|\.WithImage\s*\((?!\s*new\s+DockerImage\s*\()")]
    private static partial Regex ImageString();

    [GeneratedRegex(@"new\s+DockerImage\s*\(")]
    private static partial Regex DockerImage();

    [GeneratedRegex(@"\G\btag\s*:")]
    private static partial Regex TagArgument();

    [Test]
    [Arguments("new ContainerBuilder(Image)")]
    [Arguments("new ContainerBuilder(\"ghcr.io/workos/emulate:0.15.0\")")]
    [Arguments("new ContainerBuilder($\"alpine:{tag}\")")]
    [Arguments("new ContainerBuilder(\"\"\"alpine:3\"\"\")")]
    [Arguments("new ContainerBuilder(\"tag:3\")")]
    [Arguments("new DockerImage(\"alpine:3\")")]
    [Arguments("new DockerImage(\"tag:3\")")]
    [Arguments(".WithImage(\"alpine:3\")")]
    [Arguments("ContainerBuilder b = new(\"alpine:3\");")]
    [Arguments("new KurrentDbBuilder(\"kurrentdb:26\")")]
    [Arguments("new PostgreSqlBuilder(\"postgres:18\")")]
    [Arguments("new DockerImage(Repository(\"tag: x\"))")]
    [Arguments("var s = \"\"\"\"x\"\"\"\"; new ContainerBuilder(\"alpine:3\"); var t = \"\"\"\"y\"\"\"\";")]
    [Arguments("Testcontainers.KurrentDb.KurrentDbBuilder b = new(\"kurrentdb:26\");")]
    public async Task Pattern_matches_an_untyped_image(string code) =>
        await Assert.That(UsesAnImageString(code)).IsTrue();

    [Test]
    [Arguments("new ContainerBuilder(new DockerImage(\"ghcr.io/workos/emulate\", tag: \"0.15.0\"))")]
    [Arguments(".WithImage(new DockerImage(\"alpine\", tag: \"3\"))")]
    [Arguments("new StringBuilder(\"text\")")]
    [Arguments("ContainerBuilder b = new(new DockerImage(\"alpine\", tag: \"3\"));")]
    [Arguments("new DockerImage(GetRepository(), tag: GetTag())")]
    [Arguments("new KurrentDbBuilder(new DockerImage(\"kurrentdb\", tag: \"26\"))")]
    public async Task Pattern_passes_a_typed_image(string code) =>
        await Assert.That(UsesAnImageString(code)).IsFalse();

    [Test]
    public async Task No_test_builds_a_container_from_an_untyped_image() {
        var root = RepoRoot();

        var offenders = Directory.EnumerateFiles(Path.Combine(root, "test"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .Where(path => UsesAnImageString(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        await Assert.That(offenders).IsEmpty()
            .Because("pass new DockerImage(repository, tag: …) so Testcontainers never runs its timed image-name regex on it");
    }

    static bool UsesAnImageString(string source) {
        var code = SourceCode.WithoutCommentsOrStrings(source);

        return ImageString().IsMatch(code) || DockerImage().Matches(code).Any(call => !NamesItsTag(code, call.Index + call.Length));
    }

    /// <summary>Whether the <c>DockerImage</c> call whose arguments start at <paramref name="start"/> passes <c>tag:</c>
    /// as one of its own arguments, not inside a nested call's.</summary>
    static bool NamesItsTag(string code, int start) {
        var depth = 0;

        for (var i = start; i < code.Length; i++) {
            switch (code[i]) {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}' when depth == 0: return false;
                case ')' or ']' or '}': depth--; break;
                default:
                    if (depth == 0 && TagArgument().IsMatch(code[i..Math.Min(code.Length, i + 8)])) return true;
                    break;
            }
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
