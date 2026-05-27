using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorPathsTests {
    [Test]
    public async Task Mac_default_dir_under_application_support() {
        var p = CursorPaths.Resolve(home: "/Users/me", platform: OsPlatform.MacOs);
        await Assert.That(p.UserDir).IsEqualTo("/Users/me/Library/Application Support/Cursor/User");
        await Assert.That(p.WorkspaceStorageDir).IsEqualTo("/Users/me/Library/Application Support/Cursor/User/workspaceStorage");
        await Assert.That(p.GlobalStateDb).IsEqualTo("/Users/me/Library/Application Support/Cursor/User/globalStorage/state.vscdb");
    }

    [Test]
    public async Task Linux_default_dir_under_config() {
        var p = CursorPaths.Resolve(home: "/home/me", platform: OsPlatform.Linux);
        await Assert.That(p.UserDir).IsEqualTo("/home/me/.config/Cursor/User");
    }

    [Test]
    public async Task Windows_default_dir_under_appdata() {
        var p = CursorPaths.Resolve(home: @"C:\Users\me", platform: OsPlatform.Windows, appData: @"C:\Users\me\AppData\Roaming");
        await Assert.That(p.UserDir).IsEqualTo(@"C:\Users\me\AppData\Roaming\Cursor\User");
    }
}
