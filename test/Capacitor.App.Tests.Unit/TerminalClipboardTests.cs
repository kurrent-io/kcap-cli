using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Views;
using SvcSystems.UI.Terminal;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// Pins terminal paste. Copy is left to the control: Ctrl+C stays an interrupt, and the platform
/// paste chord is claimed while the terminal holds focus.
[NotInParallel("AvaloniaSession")]
public class TerminalClipboardTests {
    static (Window Window, TerminalControl Terminal, XtermTerminalSurface Surface, List<byte[]> Sent) Show() {
        var surface = new XtermTerminalSurface(80, 24);
        var sent = new List<byte[]>();
        surface.InputProduced += sent.Add;
        var terminal = new TerminalControl {
            Model = surface.Model, Width = 640, Height = 400,
            FontFamily = "Menlo,Monaco,Consolas,DejaVu Sans Mono,monospace",
        };
        var host = new Panel { Children = { terminal } };
        TerminalClipboard.Attach(host, terminal);
        var window = new Window { Content = host, Width = 700, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        terminal.Focus();
        return (window, terminal, surface, sent);
    }

    static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers) {
        window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    static RawInputModifiers Platform(IReadOnlyList<KeyGesture> gestures) => (RawInputModifiers)gestures[0].KeyModifiers;

    [Test]
    public async Task Ctrl_C_leaves_the_clipboard_alone_and_interrupts() {
        await RunOnUiAsync(async () => {
            var (window, terminal, surface, sent) = Show();
            try {
                var clipboard = window.Clipboard!;
                await clipboard.SetTextAsync("kept");
                surface.Feed("hello");
                terminal.SelectAll();
                Press(window, PhysicalKey.C, RawInputModifiers.Control);
                await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("kept");
                await Assert.That(sent).Count().IsEqualTo(1);
                await Assert.That(sent[0]).IsEquivalentTo(new byte[] { 0x03 });
            } finally { window.Close(); }
        });
    }

    [Test]
    public async Task The_platform_paste_gesture_pastes_a_bracketed_block_and_does_not_submit_it() {
        await RunOnUiAsync(async () => {
            var (window, _, _, sent) = Show();
            try {
                var paste = Application.Current!.PlatformSettings!.HotkeyConfiguration.Paste;
                await window.Clipboard!.SetTextAsync("a\r\nb\n");
                Press(window, PhysicalKey.V, Platform(paste));
                await Assert.That(sent).Count().IsEqualTo(1);
                await Assert.That(sent[0]).IsEquivalentTo(TerminalInputEncoder.Paste("a\r\nb\n"));
            } finally { window.Close(); }
        });
    }

    [Test]
    public async Task The_platform_copy_gesture_leaves_the_clipboard_alone() {
        await RunOnUiAsync(async () => {
            var (window, terminal, surface, _) = Show();
            try {
                var clipboard = window.Clipboard!;
                await clipboard.SetTextAsync("kept");
                surface.Feed("hello");
                terminal.SelectAll();
                var copy = Application.Current!.PlatformSettings!.HotkeyConfiguration.Copy;
                Press(window, PhysicalKey.C, Platform(copy));
                await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("kept");
            } finally { window.Close(); }
        });
    }

    [Test]
    public async Task A_focused_composer_does_not_paste_into_the_terminal() {
        await RunOnUiAsync(async () => {
            var surface = new XtermTerminalSurface(80, 24);
            var sent = new List<byte[]>();
            surface.InputProduced += sent.Add;
            var view = new WorkspaceView();
            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try {
                var terminal = window.GetVisualDescendants().OfType<TerminalControl>().Single(c => c.Name == "TerminalHost");
                terminal.Model = surface.Model;
                var composer = window.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "ComposerInput");
                composer.Focus();
                await window.Clipboard!.SetTextAsync("composer only");
                var paste = Application.Current!.PlatformSettings!.HotkeyConfiguration.Paste;
                Press(window, PhysicalKey.V, Platform(paste));
                await Assert.That(sent).IsEmpty();
            } finally { window.Close(); }
        });
    }

    [Test]
    public async Task Both_terminal_views_paste() {
        await RunOnUiAsync(async () => {
            var paste = Application.Current!.PlatformSettings!.HotkeyConfiguration.Paste;
            foreach (var view in new Control[] { new WorkspaceView(), new RemoteSessionView() }) {
                var surface = new XtermTerminalSurface(80, 24);
                var sent = new List<byte[]>();
                surface.InputProduced += sent.Add;
                var window = new Window { Content = view, Width = 900, Height = 600 };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                try {
                    var terminal = window.GetVisualDescendants().OfType<TerminalControl>().Single(c => c.Name == "TerminalHost");
                    terminal.Model = surface.Model;
                    terminal.Focus();
                    await window.Clipboard!.SetTextAsync("pasted");
                    Press(window, PhysicalKey.V, Platform(paste));
                    await Assert.That(sent).Count().IsEqualTo(1);
                    await Assert.That(sent[0]).IsEquivalentTo(TerminalInputEncoder.Paste("pasted"));
                } finally { window.Close(); }
            }
        });
    }
}
