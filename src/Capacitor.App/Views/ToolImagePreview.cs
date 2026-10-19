using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;

namespace Capacitor.App.Views;

/// Thumbnail for a Read of a local image. A click grows it; a missing file draws nothing.
public sealed class ToolImagePreview : Button {
    const double Thumb = 140;
    const double Expanded = 520;
    const int DecodeWidth = 960;
    const long MaxBytes = 20 * 1024 * 1024;

    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<ToolImagePreview, string?>(nameof(Path));

    readonly Image _image = new() {
        MaxHeight = Thumb,
        MaxWidth = 480,
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
    };

    bool _expanded;

    public ToolImagePreview() {
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        Cursor = new Cursor(StandardCursorType.Hand);
        Content = _image;
        Click += (_, _) => {
            _expanded = !_expanded;
            _image.MaxHeight = _expanded ? Expanded : Thumb;
            _image.MaxWidth = _expanded ? 720 : 480;
        };
    }

    public string? Path {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == PathProperty) Load(change.GetNewValue<string?>());
    }

    void Load(string? path) {
        _image.Source = null;
        IsVisible = false;
        _expanded = false;
        _image.MaxHeight = Thumb;
        _image.MaxWidth = 480;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try {
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > MaxBytes) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) {
                var source = SvgSource.LoadFromStream(stream, null);
                if (source is null) return;
                _image.Source = new SvgImage { Source = source };
            } else {
                _image.Source = Bitmap.DecodeToWidth(stream, DecodeWidth);
            }
            IsVisible = _image.Source is not null;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: image preview failed: {ex.Message}");
        }
    }
}
