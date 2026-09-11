using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// An AcpElicitationRequested push. Options may be empty: the answer is then free text.
public sealed record ServerElicitationRequest(
    string SessionId, string RequestId, string Prompt, IReadOnlyList<AcpInteractionOption> Options, bool IsMultiSelect);
