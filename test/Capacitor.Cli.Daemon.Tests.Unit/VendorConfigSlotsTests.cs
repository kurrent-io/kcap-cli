using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// <see cref="VendorConfigSlots"/> is the one place naming where a vendor's binary and model live in
/// <see cref="DaemonConfig"/>. Both directions go through it — the boot binder writes, a launch
/// reads — so a row pointed at the wrong property would send an operator's override to one vendor
/// and spawn it for another, with nothing in between to disagree.
/// </summary>
public class VendorConfigSlotsTests {
    /// <summary>The mapping stated a SECOND time, independently of the slots under test: a table
    /// derived from them could not detect a mis-pointed row. The bare command is what the vendor's
    /// harness ships, which is also <see cref="DaemonConfig"/>'s default.</summary>
    static (HarnessId Vendor, Func<DaemonConfig, string> Read, string Bare)[] Paths() => [
        (HarnessId.Claude,      c => c.ClaudePath,      "claude"),
        (HarnessId.Codex,       c => c.CodexPath,       "codex"),
        (HarnessId.Cursor,      c => c.CursorPath,      "cursor-agent"),
        (HarnessId.Copilot,     c => c.CopilotPath,     "copilot"),
        (HarnessId.Gemini,      c => c.GeminiPath,      "gemini"),
        (HarnessId.Kiro,        c => c.KiroPath,        "kiro-cli"),
        (HarnessId.Pi,          c => c.PiPath,          "pi"),
        (HarnessId.OpenCode,    c => c.OpenCodePath,    "opencode"),
        (HarnessId.Antigravity, c => c.AntigravityPath, "agy")
    ];

    /// <inheritdoc cref="Paths"/>
    static (HarnessId Vendor, Func<DaemonConfig, string?> Read)[] Models() => [
        (HarnessId.Cursor,      c => c.CursorModel),
        (HarnessId.Kiro,        c => c.KiroModel),
        (HarnessId.Pi,          c => c.PiModel),
        (HarnessId.OpenCode,    c => c.OpenCodeModel),
        (HarnessId.Antigravity, c => c.AntigravityModel)
    ];

    static HarnessId[] EveryVendor => Enum.GetValues<HarnessId>();

    /// <summary>
    /// A slot's write lands on the property the slot's own read observes, and on no other vendor's.
    ///
    /// <para>Asserting the untouched siblings is the half that matters: a row aliasing two vendors
    /// onto one property still round-trips through itself, so a read-back-what-you-wrote assertion
    /// passes while one vendor's override silently redirects another's launches.</para>
    /// </summary>
    [Test]
    public async Task A_path_slot_reads_back_the_property_it_writes_and_no_other() {
        foreach (var (vendor, read, _) in Paths()) {
            var config    = new DaemonConfig();
            var untouched = new DaemonConfig();

            config.PathSlot(vendor).Write($"/opt/{vendor}/bin");

            await Assert.That(read(config)).IsEqualTo($"/opt/{vendor}/bin")
                .Because($"{vendor}'s slot must name {vendor}'s own binary property");
            await Assert.That(config.PathSlot(vendor).Read()).IsEqualTo($"/opt/{vendor}/bin");

            foreach (var (other, readOther, _) in Paths().Where(p => p.Vendor != vendor))
                await Assert.That(readOther(config)).IsEqualTo(readOther(untouched))
                    .Because($"writing {vendor}'s slot must leave {other}'s binary at its default");
        }
    }

    /// <inheritdoc cref="A_path_slot_reads_back_the_property_it_writes_and_no_other"/>
    [Test]
    public async Task A_model_slot_reads_back_the_property_it_writes_and_no_other() {
        foreach (var (vendor, read) in Models()) {
            var config    = new DaemonConfig();
            var untouched = new DaemonConfig();

            config.ModelSlot(vendor)!.Write($"{vendor}-model");

            await Assert.That(read(config)).IsEqualTo($"{vendor}-model")
                .Because($"{vendor}'s slot must name {vendor}'s own model property");
            await Assert.That(config.ModelSlot(vendor)!.Read()).IsEqualTo($"{vendor}-model");

            foreach (var (other, readOther) in Models().Where(m => m.Vendor != vendor))
                await Assert.That(readOther(config)).IsEqualTo(readOther(untouched))
                    .Because($"writing {vendor}'s slot must leave {other}'s model at its default");
        }
    }

    /// <summary>Every vendor has somewhere to put a binary, and it starts at the command that
    /// vendor's harness ships — a slot is what makes the override reachable, and the default is what
    /// a machine with no override spawns.</summary>
    [Test]
    public async Task Every_vendor_starts_at_the_command_its_harness_ships() {
        var config = new DaemonConfig();

        foreach (var (vendor, _, bare) in Paths())
            await Assert.That(config.PathSlot(vendor).Read()).IsEqualTo(bare);
    }

    /// <summary>
    /// Exactly the vendors with a model slot declare a model variable.
    ///
    /// <para>A variable with no slot fails the daemon's boot; a slot with no variable is a place
    /// nothing can reach. The binder relies on this equivalence — it looks the slot up only after
    /// the variable named something — so this is what keeps its refusal unreachable.</para>
    /// </summary>
    [Test]
    public async Task A_vendor_has_a_model_slot_exactly_when_it_names_a_model_variable() {
        var config = new DaemonConfig();

        foreach (var vendor in EveryVendor)
            await Assert.That(config.ModelSlot(vendor) is not null).IsEqualTo(vendor.ModelEnvVar is not null)
                .Because($"{vendor} must either have both a model variable and a slot, or neither");
    }
}
