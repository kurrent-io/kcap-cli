using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>The three routes a Muse Assistant session lives on. Visibility is always private, whatever
/// the profile's default: a chat is personal, and the server treats an absent value as private too.</summary>
public sealed class MuseAssistantHttpPoster(ProfileContext profiles, ICapacitorHttpClient http, TimeProvider time) : IMuseAssistantPoster {
    /// <summary>Short because nothing is lost by giving up: the walk directory is kept and the next cron
    /// tick, minutes away, resumes from the last accepted step.</summary>
    static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(10);

    HttpClient? _client;

    public async Task<string?> SendAsync(MuseAssistantAction action, string chatId, CancellationToken ct) {
        var baseUrl = profiles.Resolution.ServerUrl!.TrimEnd('/');
        _client ??= await http.ForBackgroundAsync(ct);

        var (route, body) = action switch {
            MuseAssistantStartSession start => ("hooks/session-start/muse-assistant", StartBody(start.Session, chatId)),
            MuseAssistantSendLines lines    => ("hooks/transcript", TranscriptBody(lines)),
            MuseAssistantEndSession end     => ("hooks/session-end/muse-assistant", new JsonObject {
                ["session_id"] = end.Session.SessionId,
                ["ended_at"]   = Iso(end.EndedAtMs),
                ["reason"]     = "idle"
            }.ToJsonString()),
            _ => throw new InvalidOperationException($"{action.GetType().Name} has nothing to post")
        };

        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        try {
            using var response = await _client.PostWithRetryAsync($"{baseUrl}/{route}", content, time, RetryBudget, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return "the server refused the sign-in (401); run `kcap login --device`";

            return response.IsSuccessStatusCode ? null : $"{route} answered {(int)response.StatusCode}";
        } catch (HttpRequestException ex) {
            // A spent retry budget wraps the last transport fault; that inner message is the real cause.
            var cause = ex.InnerException is { } inner ? $" ({Clean(inner.Message)})" : "";

            return $"cannot reach {UnusableUrlDiagnostic.Sanitize(baseUrl)}: {Clean(ex.Message)}{cause}";
        }
    }

    // A message is printed on a line the cron log is parsed by: no control characters, bounded length.
    // A control character becomes a space so a line break cannot fuse two words.
    static string Clean(string message) {
        var text = new string(message.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

        return text.Length <= 200 ? text : string.Concat(text.AsSpan(0, 200), "…");
    }

    internal static string TranscriptBody(MuseAssistantSendLines lines) => JsonSerializer.Serialize(new TranscriptBatch {
        SessionId   = lines.SessionId,
        Lines       = lines.Lines,
        LineNumbers = lines.Seqs,
        Vendor      = "muse-assistant",
        Strict      = true
    }, CapacitorJsonContext.Default.TranscriptBatch);

    static string StartBody(MuseAssistantOpenSession session, string chatId) => new JsonObject {
        ["session_id"]         = session.SessionId,
        ["chat_id"]            = chatId,
        ["is_main"]            = session.IsMain,
        ["started_at"]         = Iso(session.StartedAtMs),
        ["default_visibility"] = "private",
        ["home_dir"]           = session.HomeDir
    }.ToJsonString();

    static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O");
}
