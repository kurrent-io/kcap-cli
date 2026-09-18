using System.Collections.Frozen;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

/// <summary>How each vendor's CLI starts an interactive session with an initial prompt, as its own
/// --help documents it. The prompt is always one argv element.</summary>
internal sealed record HandoffLaunchRecipe(HarnessId Vendor, IReadOnlyList<string> LeadingArgs) {
    public static readonly IReadOnlyDictionary<HarnessId, HandoffLaunchRecipe> All = new Dictionary<HarnessId, HandoffLaunchRecipe> {
        [HarnessId.Claude]      = new(HarnessId.Claude,      []),
        [HarnessId.Codex]       = new(HarnessId.Codex,       []),
        [HarnessId.Cursor]      = new(HarnessId.Cursor,      []),
        [HarnessId.Copilot]     = new(HarnessId.Copilot,     ["-i"]),
        [HarnessId.Gemini]      = new(HarnessId.Gemini,      ["-i"]),
        [HarnessId.Kiro]        = new(HarnessId.Kiro,        ["chat"]),
        [HarnessId.Pi]          = new(HarnessId.Pi,          []),
        [HarnessId.OpenCode]    = new(HarnessId.OpenCode,    ["--prompt"]),
        [HarnessId.Antigravity] = new(HarnessId.Antigravity, ["-i"]),
    }.ToFrozenDictionary();

    public IReadOnlyList<string> Argv(string prompt) => [.. LeadingArgs, prompt];
}
