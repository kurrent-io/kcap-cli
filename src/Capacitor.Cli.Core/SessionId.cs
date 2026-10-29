namespace Capacitor.Cli.Core;

/// <summary>
/// A session id in the dashless form the server keys on, safe as a file name.
/// </summary>
public sealed record SessionId {
    public string Value { get; }

    SessionId(string value) => Value = value;

    public static SessionId? Parse(string? raw) =>
        raw?.Replace("-", "") is { Length: > 0 } value
     && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
     && value.Trim('.').Length > 0
            ? new SessionId(value)
            : null;

    public override string ToString() => Value;
}
