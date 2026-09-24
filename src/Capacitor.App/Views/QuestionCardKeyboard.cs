using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Moves focus into a question card when it appears, and between its options with the arrow keys.
/// Tab stays on Continue: the card is inline in the chat, and cycling would leave the composer
/// and the rest of the window unreachable.
public sealed class QuestionCardKeyboard {
    QuestionCardKeyboard() { }
    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<QuestionCardKeyboard, Control, bool>("IsActive");

    static readonly AttachedProperty<Wiring?> WiringProperty =
        AvaloniaProperty.RegisterAttached<QuestionCardKeyboard, Control, Wiring?>("Wiring");

    static readonly ConditionalWeakTable<object, Claim> Claims = new();

    static QuestionCardKeyboard() => IsActiveProperty.Changed.AddClassHandler<Control>(OnActiveChanged);

    public static bool GetIsActive(Control control) => control.GetValue(IsActiveProperty);
    public static void SetIsActive(Control control, bool value) => control.SetValue(IsActiveProperty, value);

    static void OnActiveChanged(Control card, AvaloniaPropertyChangedEventArgs e) {
        if (e.NewValue is not true || card.GetValue(WiringProperty) is not null) return;
        var wiring = new Wiring(card);
        card.SetValue(WiringProperty, wiring);
        card.AttachedToVisualTree += wiring.OnAttached;
        card.DetachedFromVisualTree += wiring.OnDetached;
        card.DataContextChanged += wiring.OnDataContext;
        card.AddHandler(InputElement.KeyDownEvent, wiring.OnKeyDown, RoutingStrategies.Tunnel);
        wiring.WatchSource();
        if (card.IsAttachedToVisualTree()) wiring.OnAttached(card, null!);
    }

    static void Schedule(Control card) =>
        Dispatcher.UIThread.Post(() => MoveFocus(card), DispatcherPriority.Background);

    static void MoveFocus(Control card) {
        if (!card.IsAttachedToVisualTree() || !card.IsEffectivelyVisible) return;
        var wiring = card.GetValue(WiringProperty);
        if (wiring is null) return;
        var step = StepOf(card);
        var target = FirstTarget(card);
        if (target is null || !TargetMatches(card, target)) {
            wiring.ArmRetry();
            return;
        }

        var claim = Claims.GetValue(card.DataContext ?? card, static _ => new Claim());
        var focused = TopLevel.GetTopLevel(card)?.FocusManager?.GetFocusedElement() as Visual;
        if (claim.Step == step || ReferenceEquals(focused, target)) {
            claim.Step = step;
            return;
        }

        if (focused is not null && card.IsVisualAncestorOf(focused)) {
            if (BelongsToStep(card, focused, step)) claim.Step = step;
            else wiring.ArmRetry();
            return;
        }

        var sameChat = focused is null || InSameChat(card, focused);
        claim.Step = step;
        if (sameChat) target.Focus(NavigationMethod.Tab);
    }

    static int StepOf(Control card) => card.DataContext is QuestionCardViewModel q
        ? q.IsOnReview ? q.Questions.Count : q.CurrentIndex
        : 0;

    static bool TargetMatches(Control card, Control target) {
        if (card.DataContext is not QuestionCardViewModel q) return true;
        if (q.IsOnReview) return target.Classes.Contains("reviewRow");
        return GroupOf(target)?.Index == q.CurrentIndex;
    }

    static bool BelongsToStep(Control card, Visual focused, int step) {
        if (focused is not InputElement { IsEffectivelyVisible: true } element) return false;
        if (element is StyledElement { DataContext: QuestionStepViewModel chip }) return chip.Index == step;
        if (card.DataContext is QuestionCardViewModel { IsOnReview: true })
            return GroupOf(element) is null || element.Classes.Contains("reviewRow");
        var group = GroupOf(element);
        return group is null || group.Index == step;
    }

    static QuestionGroupViewModel? GroupOf(StyledElement? node) {
        for (; node is not null; node = node.Parent)
            if (node.DataContext is QuestionGroupViewModel group) return group;
        return null;
    }

    static bool InSameChat(Control card, Visual focused) {
        var chat = card.FindAncestorOfType<ChatTabView>();
        return chat is not null && (ReferenceEquals(chat, focused) || chat.IsVisualAncestorOf(focused));
    }

    static Control? FirstTarget(Control card) {
        foreach (var input in card.GetVisualDescendants().OfType<InputElement>()) {
            if (!input.IsEffectivelyVisible || !input.IsEffectivelyEnabled || !input.Focusable) continue;
            if (input is not StyledElement styled || styled.Classes.Contains("step")) continue;
            if (input is not Control control) continue;
            if (styled.Classes.Contains("option") || styled.Classes.Contains("acpOption") || input is TextBox
                || styled.Classes.Contains("reviewRow"))
                return control;
        }
        return null;
    }

    static List<Control> Options(Control card) {
        var options = new List<Control>();
        foreach (var styled in card.GetVisualDescendants().OfType<StyledElement>()) {
            if (styled is not Control { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true } control) continue;
            if (control.Classes.Contains("option") || control.Classes.Contains("acpOption")) options.Add(control);
        }
        return options;
    }

    sealed class Claim {
        public int Step = int.MinValue;
    }

    sealed class Wiring {
        readonly Control _card;
        INotifyPropertyChanged? _source;
        PropertyChangedEventHandler? _sourceHandler;
        ChatTabView? _chat;
        EventHandler<AvaloniaPropertyChangedEventArgs>? _chatHandler;
        bool _retryArmed;
        int _retries;

        public Wiring(Control card) => _card = card;

        public void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) {
            var chat = _card.FindAncestorOfType<ChatTabView>();
            if (chat is not null && !ReferenceEquals(chat, _chat)) {
                DetachChat();
                _chat = chat;
                _chatHandler = (_, args) => {
                    if (args.Property == Visual.IsVisibleProperty) Schedule(_card);
                };
                chat.PropertyChanged += _chatHandler;
            }
            _retries = 0;
            WatchSource();
            Schedule(_card);
        }

        public void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) {
            DetachChat();
            DisarmRetry();
            if (_source is not null && _sourceHandler is not null) _source.PropertyChanged -= _sourceHandler;
            _source = null;
            _sourceHandler = null;
        }

        public void OnDataContext(object? sender, EventArgs e) => WatchSource();

        public void WatchSource() {
            if (_source is not null && _sourceHandler is not null) _source.PropertyChanged -= _sourceHandler;
            _source = _card.DataContext as INotifyPropertyChanged;
            _sourceHandler = null;
            if (_source is null) return;
            _sourceHandler = (_, args) => {
                if (args.PropertyName == nameof(QuestionCardViewModel.CurrentIndex)) Schedule(_card);
            };
            _source.PropertyChanged += _sourceHandler;
        }

        public void OnKeyDown(object? sender, KeyEventArgs e) {
            if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox) return;
            var digit = OptionNumber(e.Key);
            var directional = e.Key is Key.Up or Key.Down or Key.Left or Key.Right;
            if (!directional && digit is null) return;
            if (TopLevel.GetTopLevel(_card)?.FocusManager?.GetFocusedElement() is not Control focused) return;
            if (!_card.IsVisualAncestorOf(focused) || focused is TextBox) return;

            var options = Options(_card);
            var index = options.IndexOf(focused);
            if (index < 0) return;

            var next = digit is int n && n >= 1 && n <= options.Count ? n - 1
                : e.Key is Key.Up or Key.Left ? (index - 1 + options.Count) % options.Count
                : e.Key is Key.Down or Key.Right ? (index + 1) % options.Count
                : -1;
            if (!directional && next < 0) return;
            e.Handled = true;
            if (next >= 0 && next != index) options[next].Focus(NavigationMethod.Directional);
        }

        public void ArmRetry() {
            if (_retryArmed || _retries >= 8 || !_card.IsAttachedToVisualTree()) return;
            _retryArmed = true;
            _retries++;
            _card.LayoutUpdated += OnLayout;
        }

        void OnLayout(object? sender, EventArgs e) {
            DisarmRetry();
            MoveFocus(_card);
        }

        void DisarmRetry() {
            if (!_retryArmed) return;
            _card.LayoutUpdated -= OnLayout;
            _retryArmed = false;
        }

        void DetachChat() {
            if (_chat is not null && _chatHandler is not null) _chat.PropertyChanged -= _chatHandler;
            _chat = null;
            _chatHandler = null;
        }

        static int? OptionNumber(Key key) => key switch {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => null,
        };
    }
}
