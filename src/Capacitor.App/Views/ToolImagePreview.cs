using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// Thumbnail for a local image. A click grows it unless ExpandsOnClick is off; a missing file draws nothing.
public sealed class ToolImagePreview : Button {
    const double Thumb = 140;
    const double Expanded = 520;
    protected override Type StyleKeyOverride => typeof(Button);
    ToolPreviewImages? _loaded;
    int _generation;
    internal Task Loading { get; private set; } = Task.CompletedTask;

    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<ToolImagePreview, string?>(nameof(Path));

    public static readonly StyledProperty<bool> ExpandsOnClickProperty =
        AvaloniaProperty.Register<ToolImagePreview, bool>(nameof(ExpandsOnClick), defaultValue: true);

    public static readonly DirectProperty<ToolImagePreview, bool> UnavailableProperty =
        AvaloniaProperty.RegisterDirect<ToolImagePreview, bool>(nameof(Unavailable), o => o.Unavailable);

    bool _unavailable = true;

    /// True when there is no path, or its load produced no image; false while a load is pending.
    public bool Unavailable {
        get => _unavailable;
        private set => SetAndRaise(UnavailableProperty, ref _unavailable, value);
    }

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
            if (!ExpandsOnClick) return;
            _expanded = !_expanded;
            _image.MaxHeight = _expanded ? Expanded : Thumb;
            _image.MaxWidth = _expanded ? 720 : 480;
        };
    }

    public bool ExpandsOnClick {
        get => GetValue(ExpandsOnClickProperty);
        set => SetValue(ExpandsOnClickProperty, value);
    }

    public string? Path {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == PathProperty) Loading = LoadAsync(change.GetNewValue<string?>());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        Loading = LoadAsync(Path);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnDetachedFromVisualTree(e);
        _generation++;
        Release();
    }

    void Release() {
        _image.Source = null;
        _loaded?.Dispose();
        _loaded = null;
        IsVisible = false;
    }

    async Task LoadAsync(string? path) {
        var generation = ++_generation;
        Release();
        _expanded = false;
        _image.MaxHeight = Thumb;
        _image.MaxWidth = 480;
        Unavailable = string.IsNullOrEmpty(path);
        if (string.IsNullOrEmpty(path)) return;
        var loaded = await Task.Run(() => ToolPreviewImages.Acquire(path)).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => {
            if (generation != _generation || path != Path) {
                loaded?.Dispose();
                return;
            }
            _loaded = loaded;
            _image.Source = loaded?.Image;
            IsVisible = _image.Source is not null;
            Unavailable = !IsVisible;
        });
    }
}
