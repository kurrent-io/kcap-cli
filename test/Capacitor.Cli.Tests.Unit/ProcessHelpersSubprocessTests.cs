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

        if (LocateShell() is not { } shell) {
            Skip.Test("no sh binary on this host");

            return;
        }

        using var tmp = new TempDir();

        var dir    = tmp.CreateDir("claude", "versions");
        var binary = dir.CopyExecutable(shell, "9.9.9");

        // The trailing builtin keeps the shell from exec-ing the sleep in place, so the live process's exe stays the versioned path.
        using var child = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(binary) { ArgumentList = { "-c", "sleep 30; :" } })!;

        try {
            var resolved = ProcessHelpers.ResolveCodingAgentPid(child.Id, "claude", ProcessHelpers.GetProcessInfo);

            await Assert.That(resolved).IsEqualTo(child.Id);
        } finally {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
        }
    }

    /// <summary>
    /// An <c>sh</c> to copy, or null on a host that has none. The test copies rather than symlinks so
    /// that <c>/proc/&lt;pid&gt;/exe</c> reports the versioned path instead of resolving back to the
    /// original. A shell rather than a coreutils tool: uutils coreutils name the utility after the
    /// path they were run as, so a copy named after a version exits before the process table is read.
    /// </summary>
    static string? LocateShell() {
        string[] wellKnown = ["/bin/sh", "/usr/bin/sh"];

        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, "sh"));

        return wellKnown.Concat(onPath).FirstOrDefault(File.Exists);
    }
}
