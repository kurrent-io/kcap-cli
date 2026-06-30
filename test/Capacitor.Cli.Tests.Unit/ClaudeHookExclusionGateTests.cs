using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers the repo/path exclusion gate (<see cref="ClaudeHookCommand.IsSessionExcludedAsync"/>)
/// that guards the permission-request watcher self-heal — so a permission prompt in an
/// excluded project does not start a transcript-uploading watcher that session-start
/// intentionally skipped.
/// </summary>
public class ClaudeHookExclusionGateTests {
    static string Body(string cwd) => new JsonObject { ["cwd"] = cwd }.ToJsonString();

    [Test]
    public async Task ExcludedPath_ReturnsTrue() {
        var excludedDir = Path.Combine(Path.GetTempPath(), $"kcap-excl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(excludedDir);

        try {
            var profile  = new Profile { ExcludedPaths = [excludedDir] };
            var body     = Body(Path.Combine(excludedDir, "project"));

            var excluded = await ClaudeHookCommand.IsSessionExcludedAsync(
                profile, body, Stopwatch.GetTimestamp(), "permission-request");

            await Assert.That(excluded).IsTrue();
        } finally {
            Directory.Delete(excludedDir, recursive: true);
        }
    }

    [Test]
    public async Task NonExcludedPath_ReturnsFalse() {
        var excludedDir = Path.Combine(Path.GetTempPath(), $"kcap-excl-{Guid.NewGuid():N}");
        var otherDir    = Path.Combine(Path.GetTempPath(), $"kcap-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(excludedDir);
        Directory.CreateDirectory(otherDir);

        try {
            var profile  = new Profile { ExcludedPaths = [excludedDir] };
            var body     = Body(Path.Combine(otherDir, "project"));

            var excluded = await ClaudeHookCommand.IsSessionExcludedAsync(
                profile, body, Stopwatch.GetTimestamp(), "permission-request");

            await Assert.That(excluded).IsFalse();
        } finally {
            Directory.Delete(excludedDir, recursive: true);
            Directory.Delete(otherDir, recursive: true);
        }
    }

    [Test]
    public async Task NullProfile_ReturnsFalse() {
        var excluded = await ClaudeHookCommand.IsSessionExcludedAsync(
            profile: null, Body("/tmp/anything"), Stopwatch.GetTimestamp(), "permission-request");

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task ProfileWithoutExclusions_ReturnsFalse() {
        var excluded = await ClaudeHookCommand.IsSessionExcludedAsync(
            new Profile(), Body("/tmp/anything"), Stopwatch.GetTimestamp(), "permission-request");

        await Assert.That(excluded).IsFalse();
    }
}
