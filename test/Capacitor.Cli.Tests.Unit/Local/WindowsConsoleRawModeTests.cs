using System.Runtime.Versioning;
using Capacitor.Cli.Local;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Local;

/// A piped or redirected stdin has no console mode to change, so raw mode must be a harmless no-op
/// there — `kcap agent attach` fed by a script, and every CI run, take that path.
[SupportedOSPlatform("windows")]
public class WindowsConsoleRawModeTests {
    [Test, RunOn(OS.Windows)]
    public async Task Enabling_without_a_console_is_a_no_op_that_disposes_cleanly() {
        var token = WindowsConsoleRawMode.Enable();

        token.Dispose();
        token.Dispose();

        await Assert.That(token).IsNotNull();
    }
}
