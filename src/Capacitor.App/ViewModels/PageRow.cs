using Capacitor.App.Services;
using Capacitor.Cli.Core.Http;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One published page in the Artefacts tab. It opens at the URL the server listed, never one built
/// here: an id alone says nothing about whether this viewer may open it.
public sealed class PageRow(string id, string title, string url, string updatedLabel) : ReactiveObject {
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Url { get; } = url;
    public string UpdatedLabel { get; } = updatedLabel;
    /// The version the list named, which an open in-app viewer reloads past.
    public int LatestVersion { get; private init; }

    bool _isSelected;
    public bool IsSelected { get => _isSelected; internal set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

    /// Null for an entry with no id or no web link, which is left out of the list.
    public static PageRow? From(ArtefactDto dto, DateTimeOffset now) {
        if (string.IsNullOrWhiteSpace(dto.ArtefactId) || !IsWebLink(dto.Url)) return null;
        var title = string.IsNullOrWhiteSpace(dto.Title) ? "Untitled page" : dto.Title;
        return new(dto.ArtefactId, title, dto.Url, $"Updated {RelativeTime.Format(dto.UpdatedAt, now)}") { LatestVersion = dto.LatestVersion };
    }

    static bool IsWebLink(string? url) =>
        LinkPolicy.IsOpenable(url) && new Uri(url!).Scheme is "https" or "http";
}
