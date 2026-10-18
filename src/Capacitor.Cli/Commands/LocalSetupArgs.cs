namespace Capacitor.Cli.Commands;

/// <summary>What <c>kcap setup --local</c> was asked. Every other step's flags pass through to the terminal arm
/// untouched; under <c>--json</c> nothing else is accepted, because the wizard owns those steps.</summary>
sealed record LocalSetupArgs(bool Json, bool SingleUser, bool Yes, bool NoPrompt) {
    static readonly string[] Conflicting = [
        "--server-url", "--discover", "--org", "--slug", "--tier", "--invite-code", "--device", "--github", "--muse-assistant",
    ];

    static readonly string[] JsonAccepts = ["--local", "--json", "--single-user", "--yes", "--no-update-check"];

    /// <param name="args">The command line with the verb at index 0, as <c>HandleAsync</c> receives it.</param>
    /// <param name="kcapUrlEnv">The <c>KCAP_URL</c> environment value.</param>
    public static (LocalSetupArgs? Args, string? Error) Parse(string[] args, string? kcapUrlEnv) {
        var rest = args.Skip(1).ToArray();

        if (rest.Length > 0 && !rest[0].StartsWith('-'))
            return Refuse($"'{rest[0]}' names a server, and --local sets up its own on this machine. Drop one of them.");

        foreach (var arg in rest) {
            var flag = arg.Split('=', 2)[0];

            if (Conflicting.Contains(flag))
                return Refuse($"{flag} cannot be used with --local, which sets up its own server on this machine.");
        }

        if (!string.IsNullOrEmpty(kcapUrlEnv))
            return Refuse($"KCAP_URL is set ({kcapUrlEnv}), so every later step would talk to that server instead of the local one. Unset it to use --local.");

        var json = rest.Contains("--json");

        if (json && rest.FirstOrDefault(a => !JsonAccepts.Contains(a)) is { } extra)
            return Refuse($"{extra} cannot be used with --local --json, which accepts only --single-user, --yes and --no-update-check.");

        return (new(json, rest.Contains("--single-user"), rest.Contains("--yes"), json || rest.Contains("--no-prompt")), null);
    }

    static (LocalSetupArgs?, string?) Refuse(string error) => (null, error);
}
