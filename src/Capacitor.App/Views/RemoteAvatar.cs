using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// A picture laid over whatever placeholder sits beneath it: hidden until the image arrives, and
/// for good when it never does, so the placeholder is what a failed or offline load shows.
public sealed class RemoteAvatar : Image {
    public static readonly StyledProperty<string?> UrlProperty = AvaloniaProperty.Register<RemoteAvatar, string?>(nameof(Url));

    public string? Url {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public RemoteAvatar() {
        IsVisible = false;
        Stretch = Stretch.UniformToFill;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty) _ = LoadAsync(Url);
    }

    async Task LoadAsync(string? url) {
        Source = null;
        IsVisible = false;
        if (url is null) return;
        var image = await MarkdownImages.LoadAsync(url);
        // A recycled row may have been rebound while the fetch was in flight.
        if (url != Url || image is null) return;
        Source = image;
        IsVisible = true;
    }
}
