using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Capacitor.App.Views;

/// One phase for every "checks running" marker. A style animation starts when its control
/// appears, so the sidebar, reader and rail would each fade on their own offset; these read the
/// same clock instead and stay in step wherever they are shown.
public static class PulseClock {
    const double PeriodSeconds = 2.4;
    const double MinOpacity = 0.4;

    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<Visual, bool>("IsActive", typeof(PulseClock));

    static readonly HashSet<Visual> Active = [];
    static readonly long Origin = TimeProvider.System.GetTimestamp();
    static DispatcherTimer? _timer;

    static PulseClock() {
        IsActiveProperty.Changed.AddClassHandler<Visual>((visual, e) => {
            if (e.NewValue is true) {
                visual.AttachedToVisualTree += OnAttached;
                visual.DetachedFromVisualTree += OnDetached;
                if (TopLevel.GetTopLevel(visual) is not null) Start(visual);
            } else {
                visual.AttachedToVisualTree -= OnAttached;
                visual.DetachedFromVisualTree -= OnDetached;
                Stop(visual);
            }
        });
    }

    public static bool GetIsActive(Visual visual) => visual.GetValue(IsActiveProperty);
    public static void SetIsActive(Visual visual, bool value) => visual.SetValue(IsActiveProperty, value);

    public static double Opacity {
        get {
            var phase = TimeProvider.System.GetElapsedTime(Origin).TotalSeconds % PeriodSeconds / PeriodSeconds;
            return MinOpacity + (1 - MinOpacity) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * phase));
        }
    }

    static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Start((Visual)sender!);
    static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Stop((Visual)sender!);

    static void Start(Visual visual) {
        if (!Active.Add(visual)) return;
        visual.Opacity = Opacity;
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();
    }

    // A detached marker keeps no reference here, and the timer runs only while something is visible.
    static void Stop(Visual visual) {
        if (!Active.Remove(visual)) return;
        visual.ClearValue(Visual.OpacityProperty);
        if (Active.Count == 0) _timer?.Stop();
    }

    static void Tick() {
        var opacity = Opacity;
        foreach (var visual in Active) visual.Opacity = opacity;
    }
}
