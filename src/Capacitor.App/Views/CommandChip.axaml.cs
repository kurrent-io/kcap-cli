using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Capacitor.App.Views;

/// A terminal command shown as code, with a copy button that confirms the copy in place.
public partial class CommandChip : UserControl {
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<CommandChip, string?>(nameof(Text));

    static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.5);
    int _copies;

    public CommandChip() => InitializeComponent();

    public string? Text {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    async void OnCopyClick(object? sender, RoutedEventArgs e) {
        if (Text is not { Length: > 0 } text || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var mine = ++_copies;
        try {
            await clipboard.SetTextAsync(text);
            Show(copied: true, "Copied");
        } catch (Exception) {
            Show(copied: false, "Copy failed");
        }
        DispatcherTimer.RunOnce(() => { if (_copies == mine) Show(copied: false, "Copy"); }, CopiedFor);
    }

    void Show(bool copied, string tip) {
        CopyIcon.IsVisible = !copied;
        CopiedIcon.IsVisible = copied;
        ToolTip.SetTip(CopyButton, tip);
        if (tip != "Copy") ToolTip.SetIsOpen(CopyButton, true);
    }
}
