using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// <c>DaemonRunner.BindVendorOverrides</c> ranges over the vendors rather than naming each variable, so
/// one cannot arrive with an override <see cref="HarnessOverrides"/> declares and nothing applies.
/// These drive the real binder through a reader the test supplies: the environment is never touched, so
/// none of this needs exclusion.
/// </summary>
public class VendorOverrideBindingTests {
    static Func<string, string?> Reads(params (string Var, string Value)[] set) {
        var values = set.ToDictionary(e => e.Var, e => e.Value);

        return name => values.GetValueOrDefault(name);
    }

    /// <summary>The mapping stated a SECOND time, independently of <see cref="HarnessOverrides"/> and
    /// the appliers under test — a table derived from either could not detect a mis-wiring between
    /// them. The bare command is <see cref="DaemonConfig"/>'s default, which the binder writes over.
    /// </summary>
    static (HarnessId Vendor, string Variable, Func<DaemonConfig, string?> Read, string Bare)[] Paths() => [
        (HarnessId.Claude,      "KCAP_CLAUDE_PATH",      c => c.ClaudePath,      "claude"),
        (HarnessId.Codex,       "KCAP_CODEX_PATH",       c => c.CodexPath,       "codex"),
        (HarnessId.Cursor,      "KCAP_CURSOR_PATH",      c => c.CursorPath,      "cursor-agent"),
        (HarnessId.Copilot,     "KCAP_COPILOT_PATH",     c => c.CopilotPath,     "copilot"),
        (HarnessId.Gemini,      "KCAP_GEMINI_PATH",      c => c.GeminiPath,      "gemini"),
        (HarnessId.Kiro,        "KCAP_KIRO_PATH",        c => c.KiroPath,        "kiro-cli"),
        (HarnessId.Pi,          "KCAP_PI_PATH",          c => c.PiPath,          "pi"),
        (HarnessId.OpenCode,    "KCAP_OPENCODE_PATH",    c => c.OpenCodePath,    "opencode"),
        (HarnessId.Antigravity, "KCAP_ANTIGRAVITY_PATH", c => c.AntigravityPath, "agy")
    ];

    /// <inheritdoc cref="Paths"/>
    static (HarnessId Vendor, string Variable, Func<DaemonConfig, string?> Read)[] Models() => [
        (HarnessId.Cursor,      "KCAP_CURSOR_MODEL",      c => c.CursorModel),
        (HarnessId.Kiro,        "KCAP_KIRO_MODEL",        c => c.KiroModel),
        (HarnessId.Pi,          "KCAP_PI_MODEL",          c => c.PiModel),
        (HarnessId.OpenCode,    "KCAP_OPENCODE_MODEL",    c => c.OpenCodeModel),
        (HarnessId.Antigravity, "KCAP_ANTIGRAVITY_MODEL", c => c.AntigravityModel)
    ];

    static HarnessId[] EveryVendor => Enum.GetValues<HarnessId>();

    /// <summary>
    /// Each vendor's path variable lands on that vendor's own property and no other.
    ///
    /// <para>Asserting the untouched siblings is the half that matters: a copy-paste aliasing two
    /// vendors onto one property still moves the named property, so an assertion that only reads it
    /// back passes while one vendor's override silently redirects another's launches.</para>
    /// </summary>
    [Test]
    public async Task Each_vendors_path_variable_reaches_that_vendors_own_property() {
        foreach (var (vendor, variable, read, _) in Paths()) {
            var config    = new DaemonConfig();
            var untouched = new DaemonConfig();

            DaemonRunner.BindVendorOverrides(config, EveryVendor, Reads((variable, $"/opt/{vendor}/bin")));

            await Assert.That(read(config)).IsEqualTo($"/opt/{vendor}/bin")
                .Because($"{variable} is {vendor}'s binary override");

            foreach (var (other, _, readOther, _) in Paths().Where(p => p.Vendor != vendor))
                await Assert.That(readOther(config)).IsEqualTo(readOther(untouched))
                    .Because($"{variable} must leave {other}'s binary at its default");
        }
    }

    /// <inheritdoc cref="Each_vendors_path_variable_reaches_that_vendors_own_property"/>
    [Test]
    public async Task Each_vendors_model_variable_reaches_that_vendors_own_property() {
        foreach (var (vendor, variable, read) in Models()) {
            var config    = new DaemonConfig();
            var untouched = new DaemonConfig();

            DaemonRunner.BindVendorOverrides(config, EveryVendor, Reads((variable, $"{vendor}-model")));

            await Assert.That(read(config)).IsEqualTo($"{vendor}-model")
                .Because($"{variable} is {vendor}'s model override");

            foreach (var (other, _, readOther) in Models().Where(m => m.Vendor != vendor))
                await Assert.That(readOther(config)).IsEqualTo(readOther(untouched))
                    .Because($"{variable} must leave {other}'s model at its default");
        }
    }

    /// <summary>Exactly the vendors with a model to set declare a model variable — a variable naming
    /// a vendor that takes no model is a knob the README documents and nothing reads.</summary>
    [Test]
    public async Task Only_the_vendors_with_a_model_accessor_name_a_model_variable() {
        var declared = EveryVendor.Where(v => v.ModelEnvVar is not null).Order();

        await Assert.That(declared).IsEquivalentTo(Models().Select(m => m.Vendor).Order());
    }

    /// <summary>Every override name is one vendor's alone: two sharing a name would make a single
    /// exported value move two vendors at once.</summary>
    [Test]
    public async Task Every_vendor_names_its_own_override_variables() {
        var names = EveryVendor.Select(v => v.PathEnvVar)
            .Concat(EveryVendor.Select(v => v.ModelEnvVar).OfType<string>())
            .ToArray();

        await Assert.That(names).IsNotEmpty();
        await Assert.That(names.Distinct().Count()).IsEqualTo(names.Length);
        await Assert.That(names.Any(string.IsNullOrWhiteSpace)).IsFalse();
    }

    /// <summary>Unset and empty are both "not overridden" — an exported-but-empty variable names no
    /// binary, and taking it would leave the daemon spawning "".</summary>
    [Test]
    [Arguments("")]
    [Arguments(null)]
    public async Task A_variable_that_names_nothing_leaves_every_default_standing(string? value) {
        var config = new DaemonConfig();
        var read   = value is null ? _ => null : Reads([.. Paths().Select(p => (p.Variable, value))]);

        DaemonRunner.BindVendorOverrides(config, EveryVendor, read);

        foreach (var (vendor, _, readPath, bare) in Paths())
            await Assert.That(readPath(config)).IsEqualTo(bare)
                .Because($"{vendor} keeps the command its harness ships");
    }
}
