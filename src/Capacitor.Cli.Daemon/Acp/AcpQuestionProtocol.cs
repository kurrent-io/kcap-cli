using System.Text.Json;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// A vendor's own way of asking the user questions, outside ACP's <c>elicitation/create</c>: the client
/// method it sends, how to read its request, and how to reply. <see cref="Reply"/> takes null when the
/// user declined, or when the request could not be asked at all.
/// </summary>
internal sealed record AcpQuestionProtocol(
        string                                                    Method,
        Func<JsonElement, AcpVendorQuestionRequest?>              Read,
        Func<IReadOnlyList<AcpVendorQuestionAnswer>?, JsonElement> Reply
    );
