using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.MessageSenders;

/// The HTTP channel. <paramref name="http"/> must already carry the caller's bearer. Degrades rather
/// than throws, except for the caller's own cancellation, which propagates.
public sealed class MessageSendersClient(HttpClient http, string serverUrl) {
    readonly string _base = serverUrl.TrimEnd('/');

    public async Task<MessageSendersRead> ReadAsync(string sessionId, MessageSendersRequest request, CancellationToken ct) {
        if (WorkContextIds.CanonicalSessionId(sessionId) is not { } id) return MessageSendersRead.Of(MessageSendersReadKind.Unavailable);

        try {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/sessions/{Uri.EscapeDataString(id)}/message-senders") {
                Content = JsonContent.Create(request, CapacitorJsonContext.Default.MessageSendersRequest),
            };
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            return (int)resp.StatusCode switch {
                401        => MessageSendersRead.Of(MessageSendersReadKind.SignedOut),
                403 or 404 => MessageSendersRead.Of(MessageSendersReadKind.Unavailable),
                >= 200 and < 300 when await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.MessageSendersResponseDto, ct).ConfigureAwait(false) is { Senders: { } senders }
                    => new(MessageSendersReadKind.Ready, senders),
                _ => MessageSendersRead.Of(MessageSendersReadKind.Unreachable),
            };
        } catch (Exception e) when (IsTransient(e, ct)) {
            return MessageSendersRead.Of(MessageSendersReadKind.Unreachable);
        }
    }

    static bool IsTransient(Exception e, CancellationToken ct) =>
        e is OperationCanceledException
            ? !ct.IsCancellationRequested
            : e is HttpRequestException or JsonException or NotSupportedException or IOException;
}
