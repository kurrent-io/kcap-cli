using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

static class AccountStateLabels {
    public static string For(HarnessId vendor, RecordingState state) => state switch {
        RecordingState.Recording                                => "recording",
        RecordingState.Installed when vendor is HarnessId.Codex => "hooks installed (trust in Codex)",
        RecordingState.Installed                                => "wired (starts on next launch)",
        RecordingState.Broken                                   => "broken — run kcap accounts rewire",
        _                                                       => "not wired",
    };
}
