namespace Capacitor.App.Services;

public sealed record UploadOutcome(UploadKind Kind, IReadOnlyList<string> Ids, string? Reason) {
    public static UploadOutcome Unauthorized(string reason) => new(UploadKind.Unauthorized, [], reason);
    public static UploadOutcome Rejected(string reason) => new(UploadKind.Rejected, [], reason);
    public static UploadOutcome Unreachable(string reason) => new(UploadKind.Unreachable, [], reason);
}
