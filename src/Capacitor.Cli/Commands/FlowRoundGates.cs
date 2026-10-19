using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Runs a flow's rounds one at a time per participant: the server refuses a round for a participant whose
/// previous round is still running, so two calls that arrive together must not both reach it.
/// </summary>
sealed class FlowRoundGates {
    readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>Waits for the participant's previous round, when <paramref name="tool"/> submits one. Dispose the
    /// result once the round's call has returned.</summary>
    public async Task<IDisposable?> EnterAsync(string? tool, JsonObject? arguments) {
        var participant = tool switch {
            "submit_review_round" => "reviewer",
            "send_to_participant" => Text(arguments, "participant"),
            _                     => null,
        };
        if (participant is null || Text(arguments, "flow_run_id") is not { } flowRunId) return null;

        var gate = _gates.GetOrAdd($"{flowRunId}\n{participant}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        return new Release(gate);
    }

    static string? Text(JsonObject? arguments, string key) =>
        arguments?[key] is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0 ? text : null;

    sealed class Release(SemaphoreSlim gate) : IDisposable {
        int _released;

        public void Dispose() {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
