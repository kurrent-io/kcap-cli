using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the composer input frames. snake_case on the wire; every member always emitted.
public sealed record SendTextDto(string AgentId, string Text);

/// The attachment-bearing composer frame. Its own frame type rather than a trailing member on
/// SendTextDto: an older decoder would ignore the member and deliver the text without the files.
public sealed record SendTextWithAttachmentsDto(string AgentId, string Text, string[] AttachmentIds);

/// Reason is one of SendTextReasons when Ok is false; Outcome is one of SendTextOutcomes when Ok is true.
public sealed record SendTextAckDto(bool Ok, string? Reason, string? Error, string? Outcome = null);

public static class SendTextReasons {
    public const string Malformed          = "malformed";
    public const string TextEmpty          = "text_empty";
    public const string TooLarge           = "too_large";
    public const string NoSuchAgent        = "no_such_agent";
    public const string ProtectedKind      = "protected_kind";
    public const string NotRunning         = "not_running";
    public const string ReaperClaimed      = "reaper_claimed";
    public const string ReaperClaimedLate  = "reaper_claimed_late";
    public const string QueueFull          = "queue_full";
    public const string StopFailed         = "stop_failed";
    public const string DeliveryFailed     = "delivery_failed";
    /// Client-side only: the request or the reply never crossed the socket.
    public const string Transport          = "transport";
    public const string AttachmentsRefused = "attachments_refused";
}

public static class SendTextOutcomes {
    public const string Delivered = "delivered";
    public const string Stopped   = "stopped";
}

public static class InputWire {
    /// One frame is one buffer, where PTY input streams under the PTY's own flow control.
    public const int MaxTextBytes = 256 * 1024;

    /// STJ source-gen leaves a missing member null and `{}` decodes fine; emptiness is the handler's call.
    public static bool IsStructurallyValid(SendTextDto? dto) =>
        dto is not null && dto.AgentId is not null && dto.Text is not null;

    public const int  MaxAttachmentsPerPrompt = 10;
    /// The server's own per-file cap.
    public const long MaxAttachmentBytes      = 10L * 1024 * 1024;

    /// The server mints Guid "N": 32 hex digits, accepted in either case.
    public static bool IsValidAttachmentId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _);

    public static bool IsStructurallyValid(SendTextWithAttachmentsDto? dto) =>
        dto is not null && dto.AgentId is not null && dto.Text is not null && dto.AttachmentIds is not null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SendTextDto))]
[JsonSerializable(typeof(SendTextWithAttachmentsDto))]
[JsonSerializable(typeof(SendTextAckDto))]
public partial class InputIpcJsonContext : JsonSerializerContext;
