using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Commands;

/// <summary>The <c>list_start_agent_options</c> tool: which of the caller's daemons run on this machine,
/// with room and harnesses, read before a <c>start_agent</c> call so the agent can stop early or ask
/// the user which harness to start.</summary>
static class StartAgentOptionsTool {
    internal const string Name  = "list_start_agent_options";
    internal const string Route = "/api/daemons";

    internal const string StartADaemon = "The user starts one with `kcap daemon start -d`, then calls this again.";

    internal const string NoMachineId =
        "No daemon can start an agent here: no kcap daemon has ever run on this machine, so start_agent would be refused. " + StartADaemon;

    internal const string UnreadableAnswer =
        "Error: the daemon list (GET /api/daemons) could not be read. Nothing was started; retry the call.";

    internal const string AskForHarness =
        "If the user did not name the harness to start, ask them which one of the listed harnesses to use before calling start_agent, and pass it as vendor.";

    internal static McpTool Describe() => new(
        Name,
        "Before start_agent: list which of the user's daemons are connected on THIS machine, how many agent slots each has free, and which harnesses (vendors) each can start, plus the harness running this session. " +
        "start_agent only launches on a daemon on this machine, so call this first: when it says no daemon runs here, stop and tell the user how to start one instead of calling start_agent. " +
        "When the user did not name a harness, ask them to pick one of the listed harnesses. " +
        "Read-only and side-effect-free: this does NOT start anything.",
        new("object", new(), []),
        McpToolAnnotations.Read
    );

    internal static (string Text, bool IsError) Render(int status, string body, string? localMachineId, string? driverVendor) {
        if (status is < 200 or >= 300) return ($"Error: HTTP {status} — {body}", true);

        using var document = TryParse(body);
        if (document is null || !document.RootElement.IsArray) return (UnreadableAnswer, true);

        var driver = driverVendor is { Length: > 0 } ? driverVendor : "unknown";
        var text   = new StringBuilder($"harness running this session: {driver}\n");

        if (localMachineId is null) return (text.Append(NoMachineId).ToString(), false);

        var connected = document.RootElement.EnumerateArray()
            .Where(d => d.IsObject && d.Bool("connected") != false && d.Str("name") is { Length: > 0 })
            .ToList();

        var here = connected.Where(d => d.Str("machine_id") == localMachineId).ToList();

        if (here.Count == 0) {
            text.Append("No daemon of yours is connected on this machine, so start_agent would be refused. ").Append(StartADaemon);

            var elsewhere = connected.Count(d => d.Str("machine_id") is { Length: > 0 });
            var unplaced  = connected.Count - elsewhere;

            if (elsewhere > 0)
                text.Append($"\n{elsewhere} of the user's daemons run on other machines; start_agent cannot use them.");
            if (unplaced > 0)
                text.Append($"\n{unplaced} connected daemons do not report their machine, because they are too old: updating kcap and restarting the daemon fixes that.");

            return (text.ToString(), false);
        }

        text.Append("daemons on this machine:\n");

        foreach (var daemon in here) text.Append(DescribeDaemon(daemon)).Append('\n');

        if (here.Count > 1) text.Append("Several daemons run here: ask the user which one, and pass it as daemon.\n");

        return (text.Append(AskForHarness).ToString(), false);
    }

    static string DescribeDaemon(JsonElement daemon) {
        var line = new StringBuilder($"- {daemon.Str("name")}: ");

        if (daemon.Num("max_agents") is { } max && daemon.Num("active_agents") is { } active)
            line.Append(active >= max ? $"at capacity ({active} of {max} agent slots in use)" : $"{active} of {max} agent slots in use");
        else
            line.Append("agent slots not reported");

        line.Append("; harnesses: ").Append(daemon.Arr("supported_vendors") is { } vendors
            ? Names(vendors) is { Length: > 0 } names ? names : "none"
            : "not reported (the daemon is too old; updating kcap and restarting it fixes that)");

        return line.ToString();
    }

    static string Names(JsonElement array) =>
        string.Join(", ", array.EnumerateArray().Where(e => e.IsString).Select(e => e.GetString()).OfType<string>());

    static JsonDocument? TryParse(string body) {
        try {
            return JsonDocument.Parse(body);
        } catch (JsonException) {
            return null;
        }
    }
}
