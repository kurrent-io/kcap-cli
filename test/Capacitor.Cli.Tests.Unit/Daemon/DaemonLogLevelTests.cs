using Capacitor.Cli.Daemon;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Covers the daemon log-verbosity toggle (diagnostics): the
/// <c>--log-level</c> / <c>KCAP_DAEMON_LOG_LEVEL</c> string parse, and that the
/// rolling file logger actually honours the configured minimum level — the
/// per-tick DaemonPing RTT line logs at Debug, which the pre-toggle hardcoded
/// Information floor silently dropped.
/// </summary>
public class DaemonLogLevelTests {
    [Test]
    [Arguments("debug", LogLevel.Debug)]
    [Arguments("DEBUG", LogLevel.Debug)]
    [Arguments(" Debug ", LogLevel.Debug)]
    [Arguments("trace", LogLevel.Trace)]
    [Arguments("info", LogLevel.Information)]
    [Arguments("information", LogLevel.Information)]
    [Arguments("warn", LogLevel.Warning)]
    [Arguments("warning", LogLevel.Warning)]
    [Arguments("error", LogLevel.Error)]
    [Arguments("none", LogLevel.None)]
    public async Task ParseLogLevel_recognised_values(string input, LogLevel expected) {
        await Assert.That(DaemonRunner.ParseLogLevel(input)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("verbose")]
    [Arguments("nonsense")]
    public async Task ParseLogLevel_returns_null_for_unrecognised_so_caller_can_default(string? input) {
        await Assert.That(DaemonRunner.ParseLogLevel(input)).IsNull();
    }

    [Test]
    public async Task FileLogger_at_Debug_min_level_writes_debug_lines() {
        var path = Path.Combine(Path.GetTempPath(), "kcap-log-" + Guid.NewGuid().ToString("N")[..8] + ".log");

        try {
            using (var provider = new RollingFileLoggerProvider(path, minLevel: LogLevel.Debug)) {
                var logger = provider.CreateLogger("Test");
                logger.Log(LogLevel.Debug, "DaemonPing ok — 42 ms RTT");
            }

            var contents = await File.ReadAllTextAsync(path);
            await Assert.That(contents).Contains("DaemonPing ok");
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task FileLogger_at_default_Information_level_drops_debug_lines() {
        var path = Path.Combine(Path.GetTempPath(), "kcap-log-" + Guid.NewGuid().ToString("N")[..8] + ".log");

        try {
            using (var provider = new RollingFileLoggerProvider(path)) {
                var logger = provider.CreateLogger("Test");
                logger.Log(LogLevel.Debug, "should be dropped");
                logger.Log(LogLevel.Information, "should be kept");
            }

            var contents = await File.ReadAllTextAsync(path);
            await Assert.That(contents).DoesNotContain("should be dropped");
            await Assert.That(contents).Contains("should be kept");
        } finally {
            File.Delete(path);
        }
    }
}
