using Kapacitor.Cli.Daemon.Services;

namespace Kapacitor.Cli.Tests.Unit;

/// <summary>
/// Covers the daemon-startup vendor probe (AI-652). The resolver is the
/// only place the daemon decides whether to advertise <c>claude</c> /
/// <c>codex</c> over <c>DaemonConnect</c>, so the launch dialog's vendor
/// filter is only ever as accurate as this lookup.
/// </summary>
public class CliResolverTests {
    [Test]
    public async Task ReturnsFalse_ForEmptyInput() {
        await Assert.That(CliResolver.Exists("")).IsFalse();
        await Assert.That(CliResolver.Exists("   ")).IsFalse();
    }

    [Test]
    public async Task ReturnsTrue_WhenAbsolutePathExists() {
        var tempFile = Path.Combine(Path.GetTempPath(), $"cli-resolver-test-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(tempFile, "#!/bin/sh\necho hi\n");

        try {
            await Assert.That(CliResolver.Exists(tempFile)).IsTrue();
        } finally {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ReturnsFalse_WhenAbsolutePathMissing() {
        var missing = Path.Combine(Path.GetTempPath(), $"cli-resolver-missing-{Guid.NewGuid():N}");

        await Assert.That(CliResolver.Exists(missing)).IsFalse();
    }

    [Test]
    public async Task ReturnsTrue_WhenBareCommandResolvesOnPath() {
        // Drop a fake "kapacitor-pathprobe-{guid}" binary into a temp dir and
        // prepend that dir to PATH so the resolver finds it. Touch the
        // executable bit on POSIX so the binary is plausibly runnable —
        // the resolver doesn't enforce it, but the file has to exist.
        var dir = Directory.CreateTempSubdirectory("cli-resolver-path-").FullName;
        var name = $"kapacitor-pathprobe-{Guid.NewGuid():N}";
        var binaryName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var binaryPath = Path.Combine(dir, binaryName);
        await File.WriteAllTextAsync(binaryPath, "");

        var savedPath = Environment.GetEnvironmentVariable("PATH");
        var sep       = OperatingSystem.IsWindows() ? ';' : ':';
        Environment.SetEnvironmentVariable("PATH", $"{dir}{sep}{savedPath}");

        try {
            await Assert.That(CliResolver.Exists(name)).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ReturnsFalse_WhenBareCommandNotOnPath() {
        var unlikely = $"kapacitor-not-installed-{Guid.NewGuid():N}";

        await Assert.That(CliResolver.Exists(unlikely)).IsFalse();
    }
}
