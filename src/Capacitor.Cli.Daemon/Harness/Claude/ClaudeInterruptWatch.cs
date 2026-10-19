using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>
/// Tells a PTY-hosted Claude agent's turn ended by interrupt. Claude Code fires no Stop hook when the
/// user presses Esc, so the hook relay never says the agent is waiting; the transcript's interrupt
/// marker is the only record of it. Only lines appended after the watch starts are read.
/// </summary>
internal static class ClaudeInterruptWatch {
    public static async Task RunAsync(string transcriptPath, Action onInterrupted, TimeProvider time, TimeSpan every, CancellationToken ct) {
        var tail = new JsonlTail(transcriptPath);
        tail.ReadAppended();
        using var timer = new PeriodicTimer(every, time);
        try {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) {
                if (EndsInterrupted(tail.ReadAppended().Lines)) onInterrupted();
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        }
    }

    /// <summary>True when the last main-agent user or assistant record among <paramref name="lines"/>
    /// is an interrupt marker: anything after it means the conversation moved on.</summary>
    internal static bool EndsInterrupted(IEnumerable<string> lines) {
        bool? interrupted = null;
        foreach (var line in lines) {
            if (Turn(line) is { } marker) interrupted = marker;
        }

        return interrupted == true;
    }

    /// <summary>Whether a line is an interrupt marker, false for any other main-agent user or
    /// assistant record, null for everything else.</summary>
    static bool? Turn(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (type is not ("user" or "assistant")) return null;
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return null;
            if (type == "assistant") return false;

            return ClaudeHarnessPayload.IsInterruptMarker(SoleText(root));
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>A user message's text when it is one string or one text block; null otherwise.</summary>
    static string? SoleText(JsonElement root) {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 1) return null;
        var block = content[0];

        return block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
            && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
    }
}
