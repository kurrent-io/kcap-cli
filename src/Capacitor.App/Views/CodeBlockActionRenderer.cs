using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;
using Markdig.Syntax;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.SyntaxHighlighting;

namespace Capacitor.App.Views;

/// A code block under a Copy button, and under a Run it button when its text is a command. The
/// highlighting and its theme switch stay with MarkView's own renderer: this one captures the
/// block that renderer wrote and overlays it, so a MarkView upgrade keeps owning both.
public sealed class CodeBlockActionRenderer(MarkdownView view, CodeBlockActions actions) : AvaloniaObjectRenderer<CodeBlock> {
    static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.5);

    readonly TextMateCodeBlockRenderer _inner = new();

    protected override void Write(AvaloniaRenderer renderer, CodeBlock obj) {
        var scratch = new StackPanel();
        renderer.Push(scratch);
        _inner.Write(renderer, obj);
        renderer.Pop();
        if (scratch.Children.Count == 0) return;
        var block = scratch.Children[0];
        // A control carries one parent: the scratch panel has to let go before the host adopts it.
        scratch.Children.Clear();
        // The line group puts its newlines between lines and none after the last, so the text is
        // already what the block displays: trimming the end would drop a blank line it shows.
        renderer.WriteBlock(Host(block, obj.Lines.ToString()));
    }

    /// The bang is what makes the block a command the composer can run rather than prose, and it
    /// has to be the text's own first character: the text is sent verbatim, so a block that only
    /// reaches a bang past some whitespace would send something that is not a command after all.
    internal static bool IsRunnable(string text) => text.StartsWith('!');

    Control Host(Control block, string text) {
        var strip = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        strip.Classes.Add("markdown-code-actions");
        strip.Children.Add(CopyButton(text));
        // A view with no RunCode — the pull request reader — has nowhere to run one.
        if (view.RunCode is { } command && IsRunnable(text)) strip.Children.Add(RunButton(command, text));
        actions.Track(strip, text);

        var host = new Panel { Children = { block, strip } };
        host.Classes.Add("markdown-code-host");
        return host;
    }

    static Button CopyButton(string text) {
        var button = ActionButton("Copy", "markdown-code-copy");
        var copies = 0;
        button.Click += async (_, _) => {
            if (TopLevel.GetTopLevel(button)?.Clipboard is not { } clipboard) return;
            var mine = ++copies;
            try {
                await clipboard.SetTextAsync(text);
                button.Content = "Copied";
            } catch (Exception) {
                button.Content = "Copy failed";
            }
            // The label is the only receipt a copy leaves, so a later copy's own receipt outranks
            // this one's expiry rather than being cut short by it.
            DispatcherTimer.RunOnce(() => { if (copies == mine) button.Content = "Copy"; }, CopiedFor);
        };
        return button;
    }

    /// The command itself, not a click handler that consults it: Avalonia then greys the button
    /// whenever the composer cannot take the command, instead of leaving a live-looking control
    /// whose click does nothing. The button unsubscribes when it leaves the tree, so a render per
    /// streamed delta leaves nothing behind on a command the view model owns for the session.
    internal static Button RunButton(ICommand command, string text) {
        var button = ActionButton("Run it", "markdown-code-run");
        button.Command = command;
        button.CommandParameter = text;
        return button;
    }

    static Button ActionButton(string label, string kind) {
        var button = new Button { Content = label, Cursor = new Cursor(StandardCursorType.Hand) };
        button.Classes.Add("markdown-code-action");
        button.Classes.Add(kind);
        return button;
    }
}
