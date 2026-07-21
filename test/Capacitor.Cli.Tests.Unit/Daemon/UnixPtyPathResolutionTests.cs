using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Q1: <see cref="UnixPtyProcess.ResolveExecutableAbsolutePath"/> must mirror the child's own
/// exec-time PATH resolution — the native child does <c>chdir(cwd)</c> before exec, so an EMPTY
/// PATH field (POSIX current directory) and any RELATIVE field resolve against <c>cwd</c>, not
/// the daemon's cwd, and must NOT be silently dropped (the old <c>RemoveEmptyEntries</c> split
/// discarded exactly those fields, so a command living only in <c>cwd</c> went unfound here while
/// the child would have exec'd it fine). POSIX-PATH semantics, so gated off Windows (which never
/// uses this Unix resolver).
/// </summary>
public class UnixPtyPathResolutionTests {
    static IReadOnlyDictionary<string, string> EnvWithPath(string? path) {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (path is not null) d["PATH"] = path;
        return d;
    }

    [Test]
    public async Task Absolute_command_is_returned_as_is() {
        if (OperatingSystem.IsWindows()) return;
        var r = UnixPtyProcess.ResolveExecutableAbsolutePath("/usr/bin/tool", "/some/cwd", EnvWithPath(null));
        await Assert.That(r).IsEqualTo("/usr/bin/tool");
    }

    [Test]
    public async Task Slashed_relative_command_resolves_against_cwd() {
        if (OperatingSystem.IsWindows()) return;
        var cwd = Directory.CreateTempSubdirectory("kcap-resolve-").FullName;
        try {
            var r = UnixPtyProcess.ResolveExecutableAbsolutePath("sub/tool", cwd, EnvWithPath(null));
            await Assert.That(r).IsEqualTo(Path.GetFullPath(Path.Combine(cwd, "sub/tool")));
        } finally { Directory.Delete(cwd, true); }
    }

    [Test]
    public async Task Bare_command_found_in_an_absolute_path_dir() {
        if (OperatingSystem.IsWindows()) return;
        var dir  = Directory.CreateTempSubdirectory("kcap-resolve-").FullName;
        var tool = Path.Combine(dir, "mytool");
        File.WriteAllText(tool, "");
        try {
            var r = UnixPtyProcess.ResolveExecutableAbsolutePath("mytool", "/no/such/cwd", EnvWithPath(dir));
            await Assert.That(r).IsEqualTo(Path.GetFullPath(tool));
        } finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Empty_path_field_resolves_against_cwd_not_dropped() {
        if (OperatingSystem.IsWindows()) return;
        // A command living ONLY in cwd must be found via an EMPTY PATH field (POSIX cwd) — the
        // case the old RemoveEmptyEntries split silently discarded.
        var cwd  = Directory.CreateTempSubdirectory("kcap-resolve-").FullName;
        var tool = Path.Combine(cwd, "cwdtool");
        File.WriteAllText(tool, "");
        try {
            // Leading empty field (":/definitely/not/here") — the empty field IS cwd, and cwd wins.
            var r = UnixPtyProcess.ResolveExecutableAbsolutePath("cwdtool", cwd, EnvWithPath(":/definitely/not/here"));
            await Assert.That(r).IsEqualTo(Path.GetFullPath(tool));
        } finally { Directory.Delete(cwd, true); }
    }

    [Test]
    public async Task Relative_path_field_resolves_against_cwd() {
        if (OperatingSystem.IsWindows()) return;
        var cwd    = Directory.CreateTempSubdirectory("kcap-resolve-").FullName;
        var binDir = Path.Combine(cwd, "bin");
        Directory.CreateDirectory(binDir);
        var tool = Path.Combine(binDir, "reltool");
        File.WriteAllText(tool, "");
        try {
            // Relative PATH element "bin" resolves against cwd (not the daemon's own cwd).
            var r = UnixPtyProcess.ResolveExecutableAbsolutePath("reltool", cwd, EnvWithPath("bin"));
            await Assert.That(r).IsEqualTo(Path.GetFullPath(tool));
        } finally { Directory.Delete(cwd, true); }
    }

    [Test]
    public async Task Not_found_anywhere_throws() {
        if (OperatingSystem.IsWindows()) return;
        var cwd = Directory.CreateTempSubdirectory("kcap-resolve-").FullName;
        try {
            var threw = false;
            try { UnixPtyProcess.ResolveExecutableAbsolutePath("nope-" + Guid.NewGuid().ToString("N")[..8], cwd, EnvWithPath("/no/such/dir")); }
            catch (InvalidOperationException) { threw = true; }
            await Assert.That(threw).IsTrue();
        } finally { Directory.Delete(cwd, true); }
    }
}
