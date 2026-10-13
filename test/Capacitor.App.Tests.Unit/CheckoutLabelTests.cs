using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class CheckoutLabelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Label_is_the_checkout_leaf_and_main_checkout_for_the_root() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            await Assert.That(CheckoutLabel.Format("/repo/.claude/worktrees/wt-a", "/repo")).IsEqualTo("wt-a");
            await Assert.That(CheckoutLabel.Format("/elsewhere/", "/repo")).IsEqualTo("elsewhere");
            await Assert.That(CheckoutLabel.Format("/repo", "/repo")).IsEqualTo("main checkout");
            await Assert.That(CheckoutLabel.Format("/repo/", "/repo")).IsEqualTo("main checkout");
        });
    }

}
