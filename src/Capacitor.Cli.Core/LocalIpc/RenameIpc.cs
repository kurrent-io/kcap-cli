using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the session rename frames. snake_case on the wire; every member always
/// emitted. ExpectedUserSubject is the account the client believes it is acting as; the daemon
/// refuses when its own account differs.
public sealed record SessionRenameDto(string AgentId, string Title, string? ExpectedUserSubject);

/// Title is the server's shown title when the daemon knows it, on success and refusal alike.
public sealed record SessionRenameAckDto(bool Ok, string? Reason, string? Title);

public static class SessionRenameReasons {
    public const string Malformed       = "malformed";
    public const string UnknownAgent    = "unknown_agent";
    public const string PrivateAgent    = "private_agent";
    public const string NoSession       = "no_session";
    public const string IdentityMismatch = "identity_mismatch";
    public const string NotOwned        = "not_owned";
    public const string NotFound        = "not_found";
    public const string Refused         = "refused";
    public const string Unauthenticated = "unauthenticated";
    public const string ServerError     = "server_error";
    /// Client-side only: the request or the reply never crossed the socket.
    public const string Transport       = "transport";
}

public static class RenameWire {
    /// The HelloReply capability that advertises the SessionRename handler.
    public const string Capability = "rename/1";

    public const int MaxTitleLength = 120;

    /// Trims and caps the title at <see cref="MaxTitleLength"/> UTF-16 units, never inside a
    /// surrogate pair; null when nothing is left to set.
    public static string? NormalizeTitle(string? title) {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length <= MaxTitleLength) return trimmed;
        var cut = char.IsHighSurrogate(trimmed[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength;
        return trimmed[..cut];
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SessionRenameDto))]
[JsonSerializable(typeof(SessionRenameAckDto))]
public partial class RenameIpcJsonContext : JsonSerializerContext;
