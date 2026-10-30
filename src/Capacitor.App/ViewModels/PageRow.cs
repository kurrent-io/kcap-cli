using Capacitor.Cli.Core.Http;

namespace Capacitor.App.ViewModels;

/// One page a session published, as the Artefacts tab lists it.
public sealed class PageRow(string artefactId, string title, int latestVersion, DateTimeOffset updatedAt, string url, bool isOwner) {
    public string ArtefactId { get; } = artefactId;
    public string Title { get; } = title;
    public int LatestVersion { get; } = latestVersion;
    public DateTimeOffset UpdatedAt { get; } = updatedAt;
    public string Url { get; } = url;
    public bool IsOwner { get; } = isOwner;
    public string UpdatedLabel { get; private init; } = "";

    public static PageRow From(ArtefactDto dto, DateTimeOffset now) =>
        new(dto.ArtefactId, dto.Title, dto.LatestVersion, dto.UpdatedAt, dto.Url, dto.IsOwner) {
            UpdatedLabel = RelativeTime.Format(dto.UpdatedAt, now),
        };
}
