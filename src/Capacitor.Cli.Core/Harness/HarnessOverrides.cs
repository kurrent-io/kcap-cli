namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// The variables an operator exports to redirect a vendor. Ours, not the vendor's — <c>agy</c> has
/// never heard of <c>KCAP_ANTIGRAVITY_PATH</c> — which is why they sit here rather than on
/// <see cref="IHarness"/>, where everything is a fact about the vendor or the machine.
///
/// <para>In Core because three projects need the same names and two of them cannot see each other:
/// the daemon binds them into its config at boot, <see cref="GatedReviewers"/> carries the path
/// override onto each reviewer row, and the CLI captures two of them into a service unit.</para>
///
/// <para>Switches over the closed harness set rather than a table, so a new vendor fails the build
/// here — a knob the README documents and nothing reads is the failure this shape prevents.</para>
/// </summary>
public static class HarnessOverrides {
    extension(HarnessId id) {
        /// <summary>Points the daemon at this vendor's CLI when it is not on <c>PATH</c>.</summary>
        public string PathEnvVar => id switch {
            HarnessId.Claude      => "KCAP_CLAUDE_PATH",
            HarnessId.Codex       => "KCAP_CODEX_PATH",
            HarnessId.Cursor      => "KCAP_CURSOR_PATH",
            HarnessId.Copilot     => "KCAP_COPILOT_PATH",
            HarnessId.Gemini      => "KCAP_GEMINI_PATH",
            HarnessId.Kiro        => "KCAP_KIRO_PATH",
            HarnessId.Pi          => "KCAP_PI_PATH",
            HarnessId.OpenCode    => "KCAP_OPENCODE_PATH",
            HarnessId.Antigravity => "KCAP_ANTIGRAVITY_PATH",
        };

        /// <summary>Overrides the model a hosted agent of this vendor runs. Null where we hand the
        /// vendor no model at all and it picks its own.</summary>
        public string? ModelEnvVar => id switch {
            HarnessId.Cursor      => "KCAP_CURSOR_MODEL",
            HarnessId.Kiro        => "KCAP_KIRO_MODEL",
            HarnessId.Pi          => "KCAP_PI_MODEL",
            HarnessId.OpenCode    => "KCAP_OPENCODE_MODEL",
            HarnessId.Antigravity => "KCAP_ANTIGRAVITY_MODEL",

            HarnessId.Claude or HarnessId.Codex or HarnessId.Copilot or HarnessId.Gemini => null,
        };
    }
}
