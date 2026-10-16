using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

public class ClientAgentTests {
    [Test]
    [Arguments("1.2.3", "macos", Architecture.Arm64, ClientSurface.Desktop, "kcap/1.2.3 (macos-arm64; desktop)")]
    [Arguments("0.11.28", "linux", Architecture.X64, ClientSurface.Cli, "kcap/0.11.28 (linux-x64; cli)")]
    [Arguments("1.3.0-beta.2", "windows", Architecture.Arm64, ClientSurface.Cli, "kcap/1.3.0-beta.2 (windows-arm64; cli)")]
    public async Task Formats_the_user_agent_kcap_web_parses(
            string version, string os, Architecture arch, ClientSurface surface, string expected) {
        await Assert.That(ClientAgent.Format(version, os, arch, surface)).IsEqualTo(expected);
    }

    /// <summary>
    /// kcap-web's parser, restated: a build's own value must satisfy it, or every signup from this build
    /// reaches Slack as a plain CLI one with no machine. The commit suffix MinVer appends is what breaks it.
    /// </summary>
    [Test]
    public async Task This_builds_user_agent_matches_the_shape_kcap_web_accepts() {
        var shape = new Regex(@"^kcap/\d{1,4}\.\d{1,4}\.\d{1,6}(-[0-9A-Za-z.-]{1,40})? \([a-z]+-[a-z0-9]+; (desktop|cli)\)$");

        await Assert.That(shape.IsMatch(ClientAgent.Cli.UserAgent)).IsTrue().Because(ClientAgent.Cli.UserAgent);
        await Assert.That(ClientAgent.Desktop.UserAgent).EndsWith("; desktop)");
    }
}
