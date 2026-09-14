using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using LiquidGlassAvaloniaUI;

namespace Capacitor.App.Prototypes;

// Question: does a glass composer fit Capacitor? Same launcher, three materials; switch in
// place without losing the goal or picker state. Only attached by --glass-prototype startup.
static class GlassLauncherPrototype {
    static readonly string[] ChipNames = ["RepositoryChip", "MachineChip", "AgentChip", "EffortChip", "PermissionChip"];

    public static void Attach(MainWindow window) {
        var launcher = window.GetVisualDescendants().OfType<LauncherPaneView>().Single();
        window.Styles.Add(new StyleInclude(new Uri("avares://Kurrent Capacitor/")) {
            Source = new Uri("avares://Kurrent Capacitor/Prototypes/GlassChipStyles.axaml"),
        });
        var chips = ChipNames.Select(name => launcher.FindControl<Button>(name)!).ToArray();
        var sidebar = new GlassSidebarPrototype(window.FindControl<SessionRailView>("SessionRail")!);
        var card = launcher.FindControl<Border>("ComposerCard")!;
        var cardParent = (StackPanel)card.Parent!;
        var cardIndex = cardParent.Children.IndexOf(card);
        var content = card.Child!;
        var glassContent = new Border { Padding = new Thickness(14) };
        var glass = new LiquidGlassSurface {
            CornerRadius = new CornerRadius(18),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            IsVisible = false,
        };
        var host = new Panel();
        cardParent.Children.Remove(card);
        host.Children.Add(card);
        host.Children.Add(glass);
        cardParent.Children.Insert(cardIndex, host);

        // One backdrop behind both panes lets the floating sidebar sample the same scene.
        // Launcher content margins only inset controls, never the glow.
        var sessions = window.FindControl<Grid>("SessionsSurface")!;
        var launcherPane = window.FindControl<Grid>("LauncherPane")!;
        var rightPane = (Panel)launcherPane.Parent!;
        var backdrop = new GlassPrototypeBackdrop { IsHitTestVisible = false };
        rightPane.ClipToBounds = true;
        rightPane.Background = Brushes.Transparent;
        sessions.ClipToBounds = true;
        Grid.SetColumnSpan(backdrop, 2);
        sessions.Children.Insert(0, backdrop);
        sessions.PointerMoved += (_, e) => {
            backdrop.LightPosition = e.GetPosition(backdrop);
            backdrop.InvalidateVisual();
        };

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var label = new TextBlock {
            Text = "MATERIAL STUDY", FontSize = 10, LetterSpacing = 1.3,
            Foreground = Brush("#949BAA"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 12, 0),
        };
        controls.Children.Add(label);
        var buttons = new List<Button>();
        var description = new TextBlock {
            Foreground = Brush("#949BAA"), FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 9, 0, 0),
        };
        var current = 1;
        var glow = new CheckBox {
            Content = "Backdrop glow", IsChecked = true, FontSize = 12,
            Foreground = Brush("#B0B7C6"), Margin = new Thickness(14, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var switcher = new StackPanel();

        void Show(int mode) {
            current = mode;
            var sidebarWidth = mode == 0 ? 310 : 334;
            sessions.ColumnDefinitions[0].Width = new GridLength(sidebarWidth);
            sidebar.Apply(mode);
            backdrop.ContentLeft = sidebarWidth;
            backdrop.ShowSidebarGlow = mode != 0;
            backdrop.InvalidateVisual();
            switcher.Margin = new Thickness(sidebarWidth, 0, 0, 25);
            card.Child = null;
            glassContent.Child = null;
            glass.Content = null;
            card.IsVisible = mode == 0;
            glass.IsVisible = mode != 0;
            if (mode == 0) card.Child = content;
            else {
                glassContent.Child = content;
                glass.Content = glassContent;
                glass.BlurRadius = mode == 1 ? 14 : 5;
                glass.RefractionHeight = mode == 1 ? 12 : 22;
                glass.RefractionAmount = mode == 1 ? 5 : 32;
                glass.ChromaticAberration = mode == 2;
                glass.Vibrancy = mode == 1 ? 1.1 : 1.3;
                glass.TintColor = Color.Parse(mode == 1 ? "#65151D29" : "#35151D29");
                glass.SurfaceColor = Color.Parse(mode == 1 ? "#3812151D" : "#1812151D");
                glass.HighlightOpacity = mode == 1 ? 0.35 : 0.7;
                glass.HighlightWidth = mode == 1 ? 0.7 : 1.1;
                glass.ShadowColor = Color.Parse("#60000000");
                glass.ShadowRadius = 28;
                glass.ShadowOffset = new Vector(0, 12);
            }
            foreach (var chip in chips) {
                chip.Classes.Set("kcapChip", mode == 0);
                chip.Classes.Set("glassPrototypeChip", mode != 0);
                chip.Classes.Set("liquidPrototypeChip", mode == 2);
                chip.Padding = mode == 0 ? new Thickness(11, 5) : new Thickness(12, 7);
            }
            for (var i = 0; i < buttons.Count; i++) {
                buttons[i].Background = Brush(i == mode ? "#F1F3F7" : "#191D27");
                buttons[i].Foreground = Brush(i == mode ? "#0B0D12" : "#B0B7C6");
                buttons[i].Classes.Set("kcapPrimary", i == mode);
                buttons[i].Classes.Set("kcapChip", i != mode);
            }
            description.Text = mode switch {
                0 => "Current · full-height sidebar and opaque controls",
                1 => "Soft glass · floating sidebar, frosted controls · move the pointer to shift the light",
                _ => "Liquid glass · floating sidebar, stronger lenses · move the pointer to shift the light",
            };
            Console.WriteLine($"Glass prototype: material={mode}, glow={glow.IsChecked == true}, blur={glass.BlurRadius}, refraction={glass.RefractionAmount}");
        }

        foreach (var (name, index) in new[] { ("Current", 0), ("Soft glass", 1), ("Liquid glass", 2) }) {
            var button = new Button {
                Content = name, Padding = new Thickness(14, 7), CornerRadius = new CornerRadius(8),
                FontSize = 12, BorderThickness = new Thickness(0),
            };
            button.Click += (_, _) => Show(index);
            buttons.Add(button);
            controls.Children.Add(button);
        }
        glow.IsCheckedChanged += (_, _) => {
            backdrop.IsVisible = glow.IsChecked == true;
            Show(current);
        };
        controls.Children.Add(glow);
        switcher.Children.Add(new Border {
            Background = Brush("#12151D"), BorderBrush = Brush("#2A3040"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(8), Child = controls,
        });
        switcher.Children.Add(description);
        switcher.HorizontalAlignment = HorizontalAlignment.Center;
        switcher.VerticalAlignment = VerticalAlignment.Bottom;
        ((Panel)window.Content!).Children.Add(switcher);
        window.KeyDown += (_, e) => {
            if (e.Source is TextBox || window.FocusManager?.GetFocusedElement() is TextBox) return;
            if (e.Key is not (Key.Left or Key.Right)) return;
            Show((current + (e.Key == Key.Right ? 1 : 2)) % 3);
            e.Handled = true;
        };
        Show(current);
    }

    static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}

sealed class GlassPrototypeBackdrop : Control {
    public Point? LightPosition { get; set; }
    public double ContentLeft { get; set; } = 310;
    public bool ShowSidebarGlow { get; set; }

    public override void Render(DrawingContext context) {
        var left = ContentLeft;
        var w = Bounds.Width - left;
        var h = Bounds.Height;
        var pointer = LightPosition ?? new Point(left + w * 0.35, h * 0.52);
        // Pointer response is deliberately small: lets the live refraction be judged without
        // animating the reading surface or running a permanent render timer.
        var dx = (Math.Clamp(pointer.X, left, Bounds.Width) - left - w / 2) * 0.12;
        var dy = (pointer.Y - h / 2) * 0.08;
        if (ShowSidebarGlow) {
            Glow(context, "#553D7581", new Point(left * 0.35, h * 0.42), left * 1.25, h * 0.7);
            Glow(context, "#344D4878", new Point(left * 0.5, h * 0.85), left, h * 0.45);
        }
        Glow(context, "#8023806C", new Point(left + w * 0.3 + dx, h * 0.55 + dy), w * 0.43, h * 0.46);
        Glow(context, "#6851528F", new Point(left + w * 0.72 - dx, h * 0.59 - dy), w * 0.38, h * 0.4);
        Glow(context, "#45226B85", new Point(left + w * 0.55, h * 0.35), w * 0.36, h * 0.33);
    }

    static void Glow(DrawingContext context, string color, Point center, double rx, double ry) {
        var tint = Color.Parse(color);
        var brush = new RadialGradientBrush {
            GradientStops = [new GradientStop(tint, 0), new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1)],
        };
        context.DrawEllipse(brush, null, center, rx, ry);
    }
}
