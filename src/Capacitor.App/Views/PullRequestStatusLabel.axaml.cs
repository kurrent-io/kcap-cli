using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

public partial class PullRequestStatusLabel : UserControl {
    public static readonly StyledProperty<bool> ShowTextProperty =
        AvaloniaProperty.Register<PullRequestStatusLabel, bool>(nameof(ShowText), true);

    public bool ShowText {
        get => GetValue(ShowTextProperty);
        set => SetValue(ShowTextProperty, value);
    }

    public PullRequestStatusLabel() => InitializeComponent();
}
