using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;

namespace Capacitor.App.Tests.Unit;

/// Intent only: nothing here reaches the network or writes anything, so these run without the
/// headless session — the choice owns no commands and no Rx subscriptions.
public class ConnectChoiceViewModelTests {
    [Test]
    [Arguments("acme", "https://acme.kcap.ai")]
    [Arguments("  acme  ", "https://acme.kcap.ai")]
    [Arguments("https://acme.kcap.ai/sessions/42", "https://acme.kcap.ai")]
    [Arguments("acme.kcap.ai", "https://acme.kcap.ai")]
    [Arguments("http://localhost:5108", "http://localhost:5108")]
    public async Task Paste_stages_the_normalized_server(string typed, string expected) {
        var vm = new ConnectChoiceViewModel { Choice = ConnectChoice.Paste, ServerInputText = typed };

        await Assert.That(vm.Intent).IsEqualTo(new ConnectIntent.Paste(expected));
        await Assert.That(vm.Validate()).IsTrue();
        await Assert.That(vm.InputError).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("file:///tmp/nope")]
    [Arguments("ftp://acme.example")]
    public async Task Paste_with_an_unusable_server_fails_validation_with_an_inline_error(string typed) {
        var vm = new ConnectChoiceViewModel { Choice = ConnectChoice.Paste, ServerInputText = typed };

        await Assert.That(vm.Intent).IsNull();
        await Assert.That(vm.Validate()).IsFalse();
        await Assert.That(vm.InputError).IsEqualTo(ConnectChoiceViewModel.InvalidServerMessage);
    }

    [Test]
    public async Task Editing_the_input_clears_a_stale_inline_error() {
        var vm = new ConnectChoiceViewModel { Choice = ConnectChoice.Paste, ServerInputText = "file:///tmp/nope" };
        vm.Validate();

        vm.ServerInputText = "acme";

        await Assert.That(vm.InputError).IsNull();
    }

    [Test]
    public async Task A_fresh_choice_stages_browser_discovery() {
        var vm = new ConnectChoiceViewModel();

        await Assert.That(vm.Choice).IsEqualTo(ConnectChoice.Discover);
        await Assert.That(vm.Intent).IsEqualTo(new ConnectIntent.Discover(ForceDevice: false));
        await Assert.That(vm.Validate()).IsTrue();
    }

    [Test]
    public async Task A_device_code_choice_stages_device_discovery() {
        var vm = new ConnectChoiceViewModel { UseDeviceCode = true };

        await Assert.That(vm.Intent).IsEqualTo(new ConnectIntent.Discover(ForceDevice: true));
    }

    [Test]
    public async Task Prefill_switches_to_paste_and_populates_the_input() {
        var vm = new ConnectChoiceViewModel { Choice = ConnectChoice.Discover };

        vm.Prefill("acme");

        await Assert.That(vm.Choice).IsEqualTo(ConnectChoice.Paste);
        await Assert.That(vm.ServerInputText).IsEqualTo("acme");
        await Assert.That(vm.Intent).IsEqualTo(new ConnectIntent.Paste("https://acme.kcap.ai"));
    }
}
