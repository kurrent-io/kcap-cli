using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Daemon;

/// <summary>
/// Where each vendor's binary and model live in <see cref="DaemonConfig"/>.
///
/// <para>Switches over the closed harness set rather than a table, so a new vendor fails the build
/// here instead of arriving with an override <see cref="HarnessOverrides"/> declares and no place
/// to put it.</para>
/// </summary>
internal static class VendorConfigSlots {
    extension(DaemonConfig config) {
        /// <summary>Every vendor has one — a launch needs a command to spawn.</summary>
        public ConfigSlot<string> PathSlot(HarnessId vendor) => vendor switch {
            HarnessId.Claude      => new(() => config.ClaudePath,      v => config.ClaudePath      = v),
            HarnessId.Codex       => new(() => config.CodexPath,       v => config.CodexPath       = v),
            HarnessId.Cursor      => new(() => config.CursorPath,      v => config.CursorPath      = v),
            HarnessId.Copilot     => new(() => config.CopilotPath,     v => config.CopilotPath     = v),
            HarnessId.Gemini      => new(() => config.GeminiPath,      v => config.GeminiPath      = v),
            HarnessId.Kiro        => new(() => config.KiroPath,        v => config.KiroPath        = v),
            HarnessId.Pi          => new(() => config.PiPath,          v => config.PiPath          = v),
            HarnessId.OpenCode    => new(() => config.OpenCodePath,    v => config.OpenCodePath    = v),
            HarnessId.Antigravity => new(() => config.AntigravityPath, v => config.AntigravityPath = v),
        };

        /// <summary>Null for the vendors we hand no model at all, which then pick their own. Exactly
        /// the set <see cref="HarnessOverrides"/> names no model variable for — a vendor with
        /// one but not the other is a knob that does nothing, or a knob nobody can turn.</summary>
        public ConfigSlot<string?>? ModelSlot(HarnessId vendor) => vendor switch {
            HarnessId.Cursor      => new(() => config.CursorModel,      v => config.CursorModel      = v!),
            HarnessId.Kiro        => new(() => config.KiroModel,        v => config.KiroModel        = v),
            HarnessId.Pi          => new(() => config.PiModel,          v => config.PiModel          = v),
            HarnessId.OpenCode    => new(() => config.OpenCodeModel,    v => config.OpenCodeModel    = v),
            HarnessId.Antigravity => new(() => config.AntigravityModel, v => config.AntigravityModel = v),

            HarnessId.Claude or HarnessId.Codex or HarnessId.Copilot or HarnessId.Gemini => null,
        };
    }
}
