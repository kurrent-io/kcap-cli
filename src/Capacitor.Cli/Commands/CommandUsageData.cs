using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Commands;

/// <summary>What a kcap command does with usage data nobody has chosen to share or not.</summary>
static class CommandUsageData {
    /// <summary>Withheld on a machine with the local edition. Held by a <c>kcap setup</c> that may still end up local,
    /// until it settles: the cloud edition sends what was held, the local edition only once the user opts in.
    /// Everywhere else, the notice and opt-out.</summary>
    public static UnsavedConsent For(string command, string[] args, ConfigRoot config, LocalRoot root, bool localEditionAvailable) {
        if (UsageDataEdition.IsLocal(root)) return UnsavedConsent.Withhold;

        return command == "setup" && localEditionAvailable && MayChooseLocal(args) && !UsageDataEdition.HasCloudProfile(config, root)
            ? UnsavedConsent.Pending
            : UnsavedConsent.Share;
    }

    /// <summary><c>--local</c>, or a run that names no server or workspace and so may be offered the local edition.</summary>
    internal static bool MayChooseLocal(string[] args) {
        if (args.Contains("--local")) return true;

        string[] chosen = ["--server-url", "--org", "--slug", "--discover", "--no-prompt", MuseAssistantPosture.Flag];

        return !args.Skip(1).Any(a => chosen.Any(flag => a == flag || a.StartsWith(flag + "=", StringComparison.Ordinal)))
            && !(args.Length > 1 && !args[1].StartsWith('-'));
    }
}
