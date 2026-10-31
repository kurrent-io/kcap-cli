using Capacitor.App.Services.Onboarding;

namespace Capacitor.App.Tests.Unit;

/// How setup names this machine in its sentences: "Found on …", "Could not read history on …".
public class WizardMachineLabelTests {
    [Test]
    [Arguments("Studio.local", null, "studio")]
    [Arguments("DESKTOP-4F2KQ9B", "DESKTOP-4F2KQ9B", "desktop-4f2kq9b")]
    [Arguments("build-box", "build-box.corp.example.com", "build-box")]
    public async Task A_person_given_name_is_said_in_lower_case(string machineName, string? hostName, string expected) {
        await Assert.That(WizardComposition.MachineLabel(machineName, hostName)).IsEqualTo(expected);
    }

    /// Windows reports a NetBIOS name cut to 15 characters; the host name it was cut from is the real one.
    [Test]
    public async Task A_name_cut_to_fifteen_characters_takes_the_host_name_it_was_cut_from() {
        await Assert.That(WizardComposition.MachineLabel("STUDIO-WORKSTAT", "studio-workstation-2")).IsEqualTo("studio-workstation-2");
    }

    /// A sandbox or a container is named by a generated id, which reads as an error code in a sentence.
    [Test]
    [Arguments("73792209-5FAB-4", "73792209-5fab-4c1e-9d0a-3b2f6e7a8c11")]
    [Arguments("73792209-5FAB-4", null)]
    [Arguments("3f9a1c2b7d4e", null)]
    public async Task A_generated_name_is_said_as_this_machine(string machineName, string? hostName) {
        await Assert.That(WizardComposition.MachineLabel(machineName, hostName)).IsEqualTo(WizardComposition.UnnamedMachine);
    }

    /// Only digits mixed into hex mark a name as generated: a word that happens to be hex is a name.
    [Test]
    [Arguments("deadbeef")]
    [Arguments("cafe-babe")]
    public async Task A_hex_word_without_digits_stays_a_name(string machineName) {
        await Assert.That(WizardComposition.MachineLabel(machineName)).IsEqualTo(machineName);
    }
}
