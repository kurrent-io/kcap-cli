using System.Runtime.InteropServices;
using Capacitor.App.Services.Update;

namespace Capacitor.App.Tests.Unit;

public class UpdateFeedTests {
    [Test]
    public async Task Default_is_the_kurrent_desktop_feed_for_this_platform() {
        var channel = UpdateFeed.Channel(OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), RuntimeInformation.OSArchitecture);

        await Assert.That(UpdateFeed.Resolve(_ => null)).IsEqualTo($"https://www.kurrent.io/download/desktop/{channel}/");
    }

    [Test]
    [Arguments(false, false, Architecture.Arm64, "osx-arm64")]
    [Arguments(true, false, Architecture.X64, "win-x64")]
    [Arguments(true, false, Architecture.Arm64, "win-arm64")]
    [Arguments(false, true, Architecture.X64, "linux-x64")]
    public async Task Channel_matches_the_release_rid(bool isWindows, bool isLinux, Architecture architecture, string expected) {
        await Assert.That(UpdateFeed.Channel(isWindows, isLinux, architecture)).IsEqualTo(expected);
    }

    /// The macOS feed URL is what every shipped macOS build already polls; it must not move.
    [Test]
    public async Task The_macos_feed_url_is_unchanged() {
        await Assert.That(UpdateFeed.BaseUrlFor("osx-arm64")).IsEqualTo("https://www.kurrent.io/download/desktop/osx-arm64/");
    }

    [Test]
    public async Task Override_variable_replaces_the_feed_url() {
        await Assert.That(UpdateFeed.Resolve(k => k == "KCAP_APP_UPDATE_URL" ? " http://127.0.0.1:8080/feed/ " : null))
            .IsEqualTo("http://127.0.0.1:8080/feed/");
    }

    [Test]
    public async Task Blank_override_is_ignored() {
        await Assert.That(UpdateFeed.Resolve(_ => "   ")).IsEqualTo(UpdateFeed.BaseUrl);
    }
}
