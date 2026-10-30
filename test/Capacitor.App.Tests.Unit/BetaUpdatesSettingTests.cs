using Capacitor.App.Services;
using Capacitor.App.Services.Update;

namespace Capacitor.App.Tests.Unit;

public class BetaUpdatesSettingTests {
    [Test]
    public async Task No_choice_is_null_and_a_choice_survives_a_reload() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var setting = await BetaUpdatesSetting.LoadAsync(new AppStateStore(path));
        await Assert.That(setting.Choice).IsNull();

        await setting.SetAsync(true);

        var reloaded = await BetaUpdatesSetting.LoadAsync(new AppStateStore(path));
        await Assert.That(reloaded.Choice).IsTrue();
    }
}
