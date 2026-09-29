using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

public partial class ChatKindHeader : UserControl {
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ChatKindHeader, string>(nameof(Label), "");

    public static readonly StyledProperty<string> IconDataProperty =
        AvaloniaProperty.Register<ChatKindHeader, string>(nameof(IconData), "");

    public static readonly StyledProperty<object?> StatusProperty =
        AvaloniaProperty.Register<ChatKindHeader, object?>(nameof(Status));

    public ChatKindHeader() => InitializeComponent();

    public string Label {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string IconData {
        get => GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public object? Status {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
}
