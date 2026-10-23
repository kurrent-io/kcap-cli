using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Capacitor.App.Views.Onboarding;

/// <summary>
/// The History page's track: skip, only me, shared — or, on the local edition, which has no other
/// readers, skip and import (<see cref="LastStop"/> 1). Each stop includes the one before it, so
/// stops up to the chosen one are drawn passed, and the step from skip to the first import stop
/// stays dashed until it is crossed. <see cref="Stop"/> -1 is "mixed": no thumb and nothing passed,
/// and any stop is a change.
/// </summary>
public sealed class ImportTrack : Control {
    public static readonly StyledProperty<int> StopProperty =
        AvaloniaProperty.Register<ImportTrack, int>(nameof(Stop), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> LastStopProperty =
        AvaloniaProperty.Register<ImportTrack, int>(nameof(LastStop), 2, coerce: (_, value) => Math.Clamp(value, 1, 2));

    const double TrackWidth = 228;
    const double Thumb      = 13;
    const double Ring       = 9;
    const double Height_    = 32;

    IBrush IdleBrush   => Brush("OnboardingRailIdleBrush");
    IBrush StopBrush   => Brush("OnboardingRailStopBrush");
    IBrush PassedBrush => Brush("OnboardingRailPassedBrush");
    IBrush ThumbBrush  => Brush("OnboardingAccentBrush");
    IBrush GlowBrush   => Brush("OnboardingAccentWashBrush");

    IBrush Brush(string key) => this.TryFindResource(key, out var value) && value is IBrush brush ? brush : Brushes.Transparent;

    static ImportTrack() {
        AffectsRender<ImportTrack>(StopProperty, LastStopProperty);
        FocusableProperty.OverrideDefaultValue<ImportTrack>(true);
        CursorProperty.OverrideDefaultValue<ImportTrack>(new Cursor(StandardCursorType.Hand));
    }

    public int Stop {
        get => GetValue(StopProperty);
        set => SetValue(StopProperty, value);
    }

    public int LastStop {
        get => GetValue(LastStopProperty);
        set => SetValue(LastStopProperty, value);
    }

    double X(int stop) => Thumb / 2 + stop * (TrackWidth - Thumb) / LastStop;

    protected override Size MeasureOverride(Size availableSize) => new(TrackWidth, Height_);
    protected override AutomationPeer OnCreateAutomationPeer() => new ImportTrackAutomationPeer(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == StopProperty && ControlAutomationPeer.FromElement(this) is ImportTrackAutomationPeer peer)
            peer.NotifyStopChanged(change.GetOldValue<int>(), change.GetNewValue<int>());
    }

    public override void Render(DrawingContext context) {
        var y = Height_ / 2;

        for (var i = 1; i <= LastStop; i++) {
            var from    = X(i - 1) + Ring / 2;
            var to      = X(i) - Ring / 2;
            var passed  = Stop >= i;
            if (i == 1 && !passed) {
                // The boundary: dashed, 6 on and 6 off, until crossed.
                for (var x = from; x < to; x += 12)
                    context.FillRectangle(StopBrush, new Rect(x, y - 0.75, Math.Min(6, to - x), 1.5));
            } else {
                context.FillRectangle(passed ? PassedBrush : IdleBrush, new Rect(from, y - 0.75, to - from, 1.5));
            }
        }

        for (var i = 0; i <= LastStop; i++) {
            var pen = new Pen(Stop >= i ? PassedBrush : StopBrush, 1.5);
            context.DrawEllipse(null, pen, new Point(X(i), y), Ring / 2 - 0.75, Ring / 2 - 0.75);
        }

        if (Stop >= 0 && Stop <= LastStop) {
            var center = new Point(X(Stop), y);
            context.DrawEllipse(GlowBrush, null, center, Thumb / 2 + 3, Thumb / 2 + 3);
            context.DrawEllipse(ThumbBrush, null, center, Thumb / 2, Thumb / 2);
        }

        if (IsFocused) {
            context.DrawRectangle(new Pen(ThumbBrush, 1), new Rect(-2, -2, TrackWidth + 4, Height_ + 4), 4);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        SetFromPointer(e.GetPosition(this).X);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) SetFromPointer(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        var current = Stop < 0 ? 1 : Math.Min(Stop, LastStop);
        int? next = e.Key switch {
            Key.Left or Key.Down => Math.Max(0, current - 1),
            Key.Right or Key.Up  => Math.Min(LastStop, current + 1),
            Key.Home             => 0,
            Key.End              => LastStop,
            _                    => null,
        };
        if (next is not { } value) return;
        Stop      = value;
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e) {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e) {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    void SetFromPointer(double x) {
        var stop = (int)Math.Round((x - Thumb / 2) / ((TrackWidth - Thumb) / LastStop), MidpointRounding.AwayFromZero);
        Stop = Math.Clamp(stop, 0, LastStop);
    }
}
