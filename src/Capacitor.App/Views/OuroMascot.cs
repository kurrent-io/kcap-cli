using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Svg.Skia;
using Avalonia.Threading;

namespace Capacitor.App.Views;

/// Svg.Skia renders a static picture, so the class hooks in ouro.svg take each pose's transforms
/// and the baked frames are stepped here, only while IsPlaying and on screen.
public sealed class OuroMascot : Image {
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<OuroMascot, bool>(nameof(IsPlaying));

    static readonly (int Wing, int Hop, int Tail)[] Poses =
        [(60, 16, -12), (40, 10, -8), (15, 2, -3), (-10, -6, 3), (-35, -12, 8), (-60, -16, 12)];
    static readonly int[] Sequence = [0, 1, 2, 3, 4, 5, 5, 4, 3, 2, 1, 0];
    static IImage[]? _frames;

    readonly DispatcherTimer _timer;
    int _step;

    public OuroMascot() {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => Advance();
        Source = Frames[0];
    }

    protected override Type StyleKeyOverride => typeof(Image);

    public bool IsPlaying {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == IsPlayingProperty) Sync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnDetachedFromVisualTree(e);
        Sync();
    }

    void Sync() {
        if (IsPlaying && VisualRoot is not null) {
            _timer.Start();
            return;
        }
        _timer.Stop();
        _step = 0;
        Source = Frames[0];
    }

    void Advance() {
        _step = (_step + 1) % Sequence.Length;
        Source = Frames[Sequence[_step]];
    }

    static IImage[] Frames => _frames ??= Build();

    static IImage[] Build() {
        using var reader = new StreamReader(AssetLoader.Open(new Uri("avares://kcap-desktop/Assets/ouro.svg")));
        var svg = reader.ReadToEnd();
        return [.. Poses.Select(p => (IImage)new SvgImage {
            Source = SvgSource.LoadFromStream(new MemoryStream(Encoding.UTF8.GetBytes(svg
                .Replace("<g class=\"hop\">", $"<g transform=\"translate(0 {p.Hop})\">")
                .Replace("<g class=\"wing\">", $"<g transform=\"rotate({p.Wing})\">")
                .Replace("<g class=\"wing right\">", $"<g transform=\"rotate({-p.Wing})\">")
                .Replace("<g class=\"tail\">", $"<g transform=\"rotate({p.Tail})\">"))), null)
        })];
    }
}
