namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The ancestry walk against a real process table rather than a synthetic one. Separate from
/// <see cref="ProcessHelpersTests"/> because starting a child puts this class in the shared
/// subprocess pool, which the pure tests there have no reason to sit in.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class ProcessHelpersSubprocessTests {
    [Test]
    public async Task Resolves_a_live_process_installed_under_a_versions_directory() {
        // A binary at <root>/claude/versions/<version>, where every name source reads the version
        // string, must still resolve for vendor "claude". Linux only: the proof is the /proc
        // reading, and copying a system binary to run it is not portable to a signed platform.
        if (!OperatingSystem.IsLinux()) {
            return;
        }

        if (LocateSleep() is not { } sleep) {
            Skip.Test("no sleep binary on this host");

            return;
        }

        using var tmp = new TempDir();

        var dir    = tmp.CreateDir("claude", "versions");
        var binary = dir.PathTo("9.9.9");

        File.Copy(sleep, binary);
        File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        using var child = System.Diagnostics.Process.Start(binary, "30")!;

        try {
            var resolved = ProcessHelpers.ResolveCodingAgentPid(child.Id, "claude", ProcessHelpers.GetProcessInfo);

            await Assert.That(resolved).IsEqualTo(child.Id);
        } finally {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
        }
    }

    /// <summary>
    /// A <c>sleep</c> to copy, or null on a host that has none. Any small binary would do — the
    /// test copies rather than symlinks so that <c>/proc/&lt;pid&gt;/exe</c> reports the versioned
    /// path instead of resolving back to the original.
    /// </summary>
    static string? LocateSleep() {
        string[] wellKnown = ["/usr/bin/sleep", "/bin/sleep"];

        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, "sleep"));

        return wellKnown.Concat(onPath).FirstOrDefault(File.Exists);
    }
}
