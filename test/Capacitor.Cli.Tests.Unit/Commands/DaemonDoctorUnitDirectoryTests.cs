using System.Runtime.Versioning;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Doctor advises on group-writable directories above the unit directory rather than failing on
/// them: whether a group-write bit matters depends on who else is in the group, which the bit does not say.
/// Install refuses only the world-writable form.</summary>
public class DaemonDoctorUnitDirectoryTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Names_each_group_writable_directory_on_the_path() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        var parent  = Tmp.CreateDir("parent").Path;
        var unitDir = Tmp.CreateDir("parent", "user").Path;
        var output  = new StringWriter();
        try {
            File.SetUnixFileMode(parent,
                UnixFileMode.UserRead  | UnixFileMode.UserWrite  | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);

            await DaemonCommands.ReportUnitDirectoryExposure(output, unitDir);

            var text = output.ToString();

            await Assert.That(text).Contains(parent);
            await Assert.That(text).Contains("group-writable");
            await Assert.That(text).Contains("chmod g-w")
                .Because("an advisory an operator cannot act on is noise");
        } finally {
            try {
                File.SetUnixFileMode(parent,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            } catch { /* best-effort */ }
        }
    }

    /// <summary>Silent when nothing on the path grants group write — otherwise the advisory fires on every
    /// healthy machine and stops being read.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Says_nothing_when_the_path_is_clean() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        var unitDir = Tmp.CreateDir("user").Path;
        var output  = new StringWriter();

        await DaemonCommands.ReportUnitDirectoryExposure(output, unitDir);

        await Assert.That(output.ToString()).IsEmpty();
    }
}
