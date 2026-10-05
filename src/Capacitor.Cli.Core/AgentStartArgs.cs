namespace Capacitor.Cli.Core;

/// Parses `agent start &lt;vendor&gt; [kcap flags] -- [agent args]`: kcap's own flags come
/// before <c>--</c>; everything after <c>--</c> is forwarded to the agent CLI verbatim.
public sealed class AgentStartArgs {
    public string   Vendor      { get; private set; } = "";
    public bool     Worktree    { get; private set; }
    public string?  DaemonName  { get; private set; }
    public bool     Detached    { get; private set; }
    public bool     Private     { get; private set; }
    public string?  Title       { get; private set; }
    public string[] Passthrough { get; private set; } = [];
    public string?  Error       { get; private set; }

    public static AgentStartArgs Parse(string[] args) {
        var r = new AgentStartArgs();

        if (args.Length == 0) {
            r.Error = "usage: kcap agent start <vendor> [--worktree] [--private] [--title <text>|--title=<text>] [--daemon <name>] [-d|--detach] [-- <agent args>]";

            return r;
        }

        var dash = Array.IndexOf(args, "--");
        var kcap = dash < 0 ? args : args[..dash];
        r.Passthrough = dash < 0 ? [] : args[(dash + 1)..];

        if (kcap.Length == 0) {
            r.Error = "missing <vendor>";

            return r;
        }

        r.Vendor = kcap[0];

        for (var i = 1; i < kcap.Length; i++) {
            switch (kcap[i]) {
                case "--worktree":        r.Worktree = true; break;
                case "-d" or "--detach":  r.Detached = true; break;
                case "--private":         r.Private  = true; break;
                case "--daemon":
                    if (i + 1 >= kcap.Length || string.IsNullOrEmpty(kcap[i + 1]) || kcap[i + 1].StartsWith('-')) {
                        r.Error = "--daemon requires a value";

                        return r;
                    }

                    r.DaemonName = kcap[++i];

                    break;
                case "--title":
                    if (i + 1 >= kcap.Length || KcapFlags.Contains(kcap[i + 1])) {
                        r.Error = "--title requires a value";

                        return r;
                    }

                    if (!r.TrySetTitle(kcap[++i])) return r;

                    break;
                case var flag when flag.StartsWith("--title=", StringComparison.Ordinal):
                    if (!r.TrySetTitle(flag["--title=".Length..])) return r;

                    break;
                default:
                    r.Error = $"unknown flag {kcap[i]} (agent args go after `--`)";

                    return r;
            }
        }

        return r;
    }

    // A title may itself start with '-', so only these are taken as a missing value.
    static readonly string[] KcapFlags = ["--", "--worktree", "--private", "--daemon", "-d", "--detach", "--title"];

    bool TrySetTitle(string value) {
        if (string.IsNullOrWhiteSpace(value)) {
            Error = "--title must not be blank";

            return false;
        }

        Title = value.Trim();

        return true;
    }

    public AgentStartTitle? StartTitle => AgentStartTitle.ForLocalStart(Title, Passthrough);
}
