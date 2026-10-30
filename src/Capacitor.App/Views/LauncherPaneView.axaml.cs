using Lucide.Avalonia;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Views;

/// The launcher pane: DataContext is supplied externally (a plainly-constructed HomeViewModel);
/// this view never builds its own ViewModel.
public partial class LauncherPaneView : UserControl {
    AttachmentDropPaste? _attachments;
    Flyout? _openMenu;

    /// A picker search term matches a model by its label or its launch id, so a Pi row is found by
    /// model name, provider, or `provider/` prefix alike.
    internal static bool RowMatches(ModelChoice model, string term) =>
        model.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
     || model.Slug.Contains(term, StringComparison.OrdinalIgnoreCase);

    public LauncherPaneView() {
        InitializeComponent();
        // Tunnel, not bubble: the TextBox marks Enter handled on the bubble route, so bare-Enter
        // submit must see the key on the way down. Shift+Enter falls through untouched, so the
        // TextBox inserts a newline (AcceptsReturn is true).
        GoalInput.AddHandler(KeyDownEvent, OnGoalKeyDown, RoutingStrategies.Tunnel);
    }

    public void FocusComposer() => GoalInput.Focus();

    internal Task? PendingIntakeForTesting => _attachments?.PendingIntakeForTesting;

    internal Flyout? OpenMenuForTesting => _openMenu;

    /// Paired with the visual tree rather than the constructor: a pane swap detaches and
    /// re-attaches the same view, and a behaviour disposed on the way out has to come back with it.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _attachments ??= AttachmentDropPaste.Attach(
            GoalCard, GoalInput, AttachButton,
            () => (DataContext as HomeViewModel)?.Attachments, TimeProvider.System);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _attachments?.Dispose();
        _attachments = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// Bare Enter starts a session when Start can run; otherwise the key is consumed so it does
    /// not leave a stray newline. Shift+Enter is left for the TextBox to insert a newline. Mirrors
    /// the Start button: connection readiness from CanExecute, repository from SelectedRepoPath.
    void OnGoalKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (DataContext is not HomeViewModel vm) return;
        if (string.IsNullOrEmpty(vm.SelectedRepoPath)) return;
        if (!((ICommand)vm.StartCommand).CanExecute(null)) return;
        vm.StartCommand.Execute().Subscribe();
    }

    // Repository picker: one flyout item per ListRepositoriesAsync entry — leaf name over full
    // path, remembered-harness pill on the right. The scratch entry and
    // the add affordance sit last, each behind a separator; for a remote machine the add is a typed
    // path, since the folder picker only sees this machine. Built as a kcapPanel Flyout
    // (same shape as the agent picker) rather than MenuFlyout — Fluent's radio MenuItem chrome
    // fights the dark palette.
    async void OnRepositoryChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var muted = Brush("KcapMutedBrush");
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };

        var flyout = PanelFlyout(rows, minWidth: 300);
        foreach (var option in await vm.ListRepositoriesAsync()) {
            // Scratch is only listed when there are no real repos — still separate it visually
            // if it ever shares the menu with another row.
            if (option.RepoPath.Length == 0 && rows.Children.Count > 0)
                rows.Children.Add(ChoiceSeparator());
            rows.Children.Add(RepositoryRow(vm, option, muted, flyout));
        }
        rows.Children.Add(ChoiceSeparator());
        if (vm.RemoteMachineSelected)
            rows.Children.Add(ChoiceRow($"Enter a path on {vm.SelectedMachine}…", selected: false, () => {
                flyout.Hide();
                ShowRemotePathEntry(vm, anchor);
            }));
        else
            rows.Children.Add(ChoiceRow("Add repository…", selected: false, () => {
                flyout.Hide();
                _ = AddRepositoryAsync(vm);
            }));

        ShowMenu(flyout, anchor);
    }

    Button RepositoryRow(HomeViewModel vm, RepositoryOption option, IBrush muted, Flyout flyout) {
        var isScratch = option.RepoPath.Length == 0;
        var title = new TextBlock {
            Text = isScratch ? "No repository" : RepoLabel.Leaf(option.RepoPath),
            FontSize = 13.5,
            Foreground = Brush("KcapTextBrush"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        // Pill sits on the title row only (not vertically centered on title+path), so every
        // repo's badge lines up with "No repository"'s badge. No harness available, no badge.
        var pill = new Border {
            Background = Brush("KcapMutedBrush"), CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 3), Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsVisible = HomeViewModel.IsAvailable(vm.Harnesses, option.Vendor),
            Child = new TextBlock {
                Text = HostedHarnessCatalog.LabelFor(vm.Harnesses, option.Vendor),
                FontSize = 11, FontWeight = FontWeight.SemiBold,
                Foreground = Brush("KcapOnPrimaryBrush"),
            },
        };

        var grid = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            MinWidth = 260,
            // Stretch so the * column eats leftover width and the pill stays right-aligned
            // across one-line and two-line rows alike.
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        grid.Children.Add(title);
        var check = SelectionCheck(option.Selected);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);
        Grid.SetColumn(pill, 2);
        grid.Children.Add(pill);

        if (!isScratch) {
            var path = new TextBlock {
                Text = option.RepoPath, FontSize = 10.5, Foreground = muted,
                Margin = new Thickness(0, 2, 0, 0),
            };
            Grid.SetRow(path, 1);
            Grid.SetColumnSpan(path, 3);
            grid.Children.Add(path);
        }

        var repoPath = option.RepoPath;
        var row = ChoiceButton(grid, () => {
            flyout.Hide();
            _ = SelectRepositoryObservedAsync(vm, repoPath);
        });
        AutomationProperties.SetName(row, ChoiceName(title.Text, option.Selected));
        return row;
    }

    // ChoiceButton's click is sync; keep flyout-close immediate and observe the async load so a
    // failed state read is not an unobserved task.
    static async Task SelectRepositoryObservedAsync(HomeViewModel vm, string repoPath) {
        try {
            await vm.SelectRepositoryAsync(repoPath);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: select repository failed: {ex}");
        }
    }

    // Machine picker: one flyout item per ListMachinesAsync entry — DaemonName with a checkmark
    // on the selected one, disabled (never pickable) when not Connected. Same kcapPanel Flyout
    // shape as the repository picker, one row layout instead of two-line paths.
    async void OnMachineChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var muted = Brush("KcapMutedBrush");
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var flyout = PanelFlyout(rows, minWidth: 220);
        foreach (var option in await vm.ListMachinesAsync())
            rows.Children.Add(MachineRow(vm, option, muted, flyout));

        ShowMenu(flyout, anchor);
    }

    Button MachineRow(HomeViewModel vm, MachineOption option, IBrush muted, Flyout flyout) {
        var title = new TextBlock {
            Text = option.DaemonName,
            FontSize = 13.5,
            Foreground = option.Connected ? Brush("KcapTextBrush") : muted,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var check = SelectionCheck(option.Selected);

        var subtitle = new TextBlock {
            Text = option.IsLocal ? "This machine" : option.Connected ? "Connected" : "Offline",
            FontSize = 10.5, Foreground = muted, Margin = new Thickness(0, 2, 0, 0),
        };

        var grid = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            MinWidth = 200,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        grid.Children.Add(title);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);
        Grid.SetRow(subtitle, 1);
        grid.Children.Add(subtitle);

        var daemonName = option.DaemonName;
        var isLocal = option.IsLocal;
        var row = ChoiceButton(grid, () => {
            flyout.Hide();
            _ = SelectMachineObservedAsync(vm, daemonName, isLocal);
        });
        row.IsEnabled = option.Connected;
        AutomationProperties.SetName(row, ChoiceName(option.DaemonName, option.Selected));
        return row;
    }

    // ChoiceButton's click is sync; keep flyout-close immediate and observe the async load so a
    // failed state read is not an unobserved task (SelectRepositoryObservedAsync's identical shape).
    // isLocal comes from the clicked option itself — an owned remote daemon can share the local
    // daemon's name (different servers), so routing by name alone would misselect it.
    static async Task SelectMachineObservedAsync(HomeViewModel vm, string daemonName, bool isLocal) {
        try {
            await vm.SelectMachineAsync(daemonName, isLocal);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: select machine failed: {ex}");
        }
    }

    // "Add one" via a native folder picker — SelectRepositoryAsync then treats the choice like
    // any other repository, so it shows up in the menu from the next open on.
    async Task AddRepositoryAsync(HomeViewModel vm) {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = "Choose a repository",
            AllowMultiple = false,
        });
        if (folders is not [{ } folder]) return;
        if (folder.TryGetLocalPath() is not { } path) return;

        await vm.SelectRepositoryAsync(path);
    }

    void ShowRemotePathEntry(HomeViewModel vm, Control anchor) {
        var input = new TextBox { PlaceholderText = "/path/to/repository", MinWidth = 320 };
        input.Classes.Add("kcapField");
        AutomationProperties.SetName(input, $"Repository path on {vm.SelectedMachine}");
        var error = new TextBlock {
            FontSize = 11.5, Foreground = Brush("KcapDangerBrush"), IsVisible = false,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 320,
        };
        var hint = new TextBlock {
            Text = "Checked on that machine when the session starts.",
            FontSize = 11.5, Foreground = Brush("KcapMutedBrush"),
        };

        var body = new StackPanel { Spacing = 6, Margin = new Thickness(10) };
        body.Children.Add(input);
        body.Children.Add(error);
        body.Children.Add(hint);
        var flyout = PanelFlyout(body, minWidth: 340);

        input.KeyDown += async (_, e) => {
            if (e.Key == Key.Escape) {
                e.Handled = true;
                flyout.Hide();
                return;
            }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            try {
                var refusal = await vm.EnterRemoteRepositoryAsync(input.Text ?? "");
                error.Text = refusal;
                error.IsVisible = refusal is not null;
                if (refusal is null) flyout.Hide();
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: enter remote repository failed: {ex}");
            }
        };
        flyout.Opened += (_, _) => input.Focus();

        ShowMenu(flyout, anchor);
    }

    // Effort picker over HostedHarnessCatalog.EffortLadder; Default hands the choice back to the
    // harness. A wrong value for a given vendor surfaces as that session's own launch error, same
    // as the CLI's --effort. Separator after Default matches Permissions (base vs escalations).
    void OnEffortChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var flyout = PanelFlyout(rows, minWidth: 180);

        rows.Children.Add(ChoiceRow(HostedHarnessCatalog.EffortDefaultLabel, vm.SelectedEffort is null, () => {
            vm.SelectedEffort = null;
            flyout.Hide();
        }));
        rows.Children.Add(ChoiceSeparator());
        foreach (var effort in HostedHarnessCatalog.EffortLadder) {
            var value = effort;
            rows.Children.Add(ChoiceRow(HostedHarnessCatalog.EffortLabelFor(value), vm.SelectedEffort == effort, () => {
                vm.SelectedEffort = value;
                flyout.Hide();
            }));
        }
        ShowMenu(flyout, anchor);
    }

    void OnPermissionChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var flyout = PanelFlyout(rows, minWidth: 200);

        // Manual first (omit-from-wire default), then separator, then escalations — same shape as Effort.
        foreach (var mode in HostedHarnessCatalog.PermissionModes) {
            if (rows.Children.Count == 1)
                rows.Children.Add(ChoiceSeparator());

            var token = mode.Token;
            var selected = string.Equals(vm.SelectedPermissionMode, token, StringComparison.Ordinal);
            rows.Children.Add(ChoiceRow(mode.Label, selected, () => {
                vm.SelectedPermissionMode = token;
                flyout.Hide();
            }));
        }
        ShowMenu(flyout, anchor);
    }

    void OnCodexSandboxChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is HomeViewModel vm && sender is Control anchor)
            ShowChoices(anchor, HostedHarnessCatalog.CodexSandboxes, vm.SelectedCodexSandbox, t => vm.SelectedCodexSandbox = t);
    }

    void OnCodexApprovalChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is HomeViewModel vm && sender is Control anchor)
            ShowChoices(anchor, HostedHarnessCatalog.CodexApprovals, vm.SelectedCodexApproval, t => vm.SelectedCodexApproval = t);
    }

    void ShowChoices(Control anchor, IReadOnlyList<PermissionModeChoice> choices, string current, Action<string> pick) {
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var flyout = PanelFlyout(rows, minWidth: 200);
        foreach (var choice in choices) {
            var token = choice.Token;
            rows.Children.Add(ChoiceRow(choice.Label, string.Equals(current, token, StringComparison.Ordinal), () => {
                pick(token);
                flyout.Hide();
            }));
        }
        ShowMenu(flyout, anchor);
    }

    // Shared chip-picker chrome: kcapPanel Flyout + ghost rows, the choice marked by
    // SelectionCheck in every menu. Left-aligned: a menu wider than its chip, centred, overhangs
    // the composer's left edge.
    static Flyout PanelFlyout(Control content, double minWidth) {
        var host = new Border { Child = content, MinWidth = minWidth };
        var flyout = new Flyout {
            Placement = PlacementMode.BottomEdgeAlignedLeft, Content = host,
            // A few pixels of air between the chip and the panel — flush looks glued on.
            VerticalOffset = 6,
        };
        flyout.FlyoutPresenterClasses.Add("kcapPanel");
        return flyout;
    }

    void ShowMenu(Flyout flyout, Control anchor) {
        _openMenu = flyout;
        flyout.ShowAt(anchor);
    }

    Button ChoiceRow(string label, bool selected, Action pick) {
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        body.Children.Add(new TextBlock {
            Text = label, FontSize = 13.5, Foreground = Brush("KcapTextBrush"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        var check = SelectionCheck(selected);
        Grid.SetColumn(check, 1);
        body.Children.Add(check);
        var row = ChoiceButton(body, pick);
        AutomationProperties.SetName(row, ChoiceName(label, selected));
        return row;
    }

    /// The check mark is drawn only, so the selection has to be spoken in the row's name.
    internal static string ChoiceName(string? label, bool selected) => selected ? $"{label}, selected" : label ?? "";

    AppIcon SelectionCheck(bool selected) => new() {
        Kind = LucideIconKind.Check, Size = 14, IsVisible = selected,
        Foreground = Brush("KcapInfoBrush"), Margin = new Thickness(12, 0, 0, 0),
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    static Button ChoiceButton(object content, Action pick) {
        var row = new Button {
            Content = content,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            // Stretch: left-aligned content stays content-sized and never pushes a trailing pill
            // to the row's right edge (repo list badges).
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8),
        };
        row.Classes.Add("kcapGhost");
        row.Click += (_, _) => pick();
        return row;
    }

    Border ChoiceSeparator() => new() {
        Height = 1, Margin = new Thickness(8, 4),
        Background = Brush("KcapBorderBrush"),
    };

    IBrush Brush(string key) => (IBrush)this.FindResource(key)!;

    /// The vendor's mark at a given size: the brand path when VendorIcons carries one, the tinted
    /// monogram otherwise (both from HostedHarnessCatalog.TileFor). UI-thread only (constructs
    /// thread-affine Geometry/brushes).
    internal static Control BuildGlyph(string vendor, double size) {
        var (glyph, color) = HostedHarnessCatalog.TileFor(vendor);
        if (VendorIcons.For(vendor) is { } geometry)
            return new Avalonia.Controls.Shapes.Path {
                Data = geometry, Fill = new SolidColorBrush(Color.Parse(color)),
                Width = size, Height = size, Stretch = Stretch.Uniform,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
        return new TextBlock {
            Text = glyph, FontSize = size, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    // The combined harness+model picker: a vendor icon rail on the left, an underlined
    // search over the model rows on the right. No search term = the active vendor tab's models;
    // typing searches ACROSS vendors and always offers the term verbatim as a custom model id, so
    // the curated catalog can drift without ever blocking a launch. Unavailable vendors stay
    // listed but disabled (never withdrawn silently — HostedHarnessCatalog's rule).
    void OnAgentChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var text = Brush("KcapTextBrush");
        var muted = Brush("KcapMutedBrush");
        var faint = Brush("KcapFaintBrush");
        var raised = Brush("KcapSurfaceRaisedBrush");
        var border = Brush("KcapBorderBrush");
        var warning = Brush("KcapWarningBrush");
        var recording = RecordingStates.Token(RecordingState.Recording);

        var currentTab = vm.SelectedVendor;

        var searchBox = new TextBox {
            Name = "ModelSearchBox",
            PlaceholderText = "Search models…", FontSize = 13.5,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        AutomationProperties.SetName(searchBox, "Search models");
        var underline = new Border { Height = 1, Background = border };
        var magnifier = new AppIcon {
            Kind = LucideIconKind.Search, Size = 16, Foreground = muted,
            Margin = new Thickness(14, 0, 6, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(10, 8, 10, 10) };
        var tabs = new StackPanel { Spacing = 6, Margin = new Thickness(9, 12, 9, 12) };

        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = 46 };
        searchRow.Children.Add(magnifier);
        Grid.SetColumn(searchBox, 1);
        searchRow.Children.Add(searchBox);

        var header = new StackPanel();
        header.Children.Add(searchRow);
        header.Children.Add(underline);

        var right = new DockPanel();
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        right.Children.Add(header);
        right.Children.Add(new ScrollViewer { Height = 380, Content = rows });

        var rail = new Border { BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = border, Child = tabs };

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*"), Width = 440 };
        root.Children.Add(rail);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        var flyout = new Flyout {
            Placement = PlacementMode.BottomEdgeAlignedLeft, Content = root, VerticalOffset = 6,
        };
        flyout.FlyoutPresenterClasses.Add("kcapPanel");

        async void Pick(string vendor, string slug) {
            await vm.ChooseHarnessAsync(vendor);
            vm.SelectedModel = slug;
            flyout.Hide();
        }

        Button Row(string vendor, string vendorLabel, string label, string? unavailableTip, bool selected, Action pick) {
            var enabled = unavailableTip is null;
            var sub = new StackPanel {
                Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6,
                Margin = new Thickness(0, 2, 0, 0),
            };
            sub.Children.Add(BuildGlyph(vendor, 10));
            sub.Children.Add(new TextBlock { Text = vendorLabel, FontSize = 11.5, Foreground = muted });

            var titles = new StackPanel();
            titles.Children.Add(new TextBlock {
                Text = label, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
                Foreground = enabled ? text : faint,
            });
            titles.Children.Add(sub);

            var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            body.Children.Add(titles);
            var check = SelectionCheck(selected);
            Grid.SetColumn(check, 1);
            body.Children.Add(check);

            var row = new Button {
                Content = body, IsEnabled = enabled,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7),
            };
            row.Classes.Add("kcapGhost");
            AutomationProperties.SetName(row, ChoiceName($"{label}, {vendorLabel}", selected));
            if (unavailableTip is not null) {
                ToolTip.SetTip(row, unavailableTip);
                ToolTip.SetShowOnDisabled(row, true);
                AutomationProperties.SetHelpText(row, unavailableTip);
            }
            row.Click += (_, _) => pick();
            return row;
        }

        async void PickAccount(string? accountId) {
            await vm.PickAccountAsync(accountId);
            RebuildRows();
        }

        IReadOnlyDictionary<string, AccountUsageView> drawnUsage = vm.AccountUsageById;

        Control? UsageLine(AccountUsageView usage) {
            var line = new WrapPanel {
                Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0),
            };
            void Add(Control c) {
                c.Margin = new Thickness(0, 0, 10, 0);
                c.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                line.Children.Add(c);
            }
            TextBlock Faint(string t) => new() { Text = t, FontSize = 10.5, Foreground = faint };

            if (usage.Bars.Count == 0) {
                if (usage.LineText is not null) Add(Faint(usage.LineText));
            } else {
                foreach (var bar in usage.Bars) {
                    var fill = new Border {
                        Background = Brush(UsageToneBrushConverter.FillKey(bar.Tone)), CornerRadius = new CornerRadius(2),
                        Width = UsageBarFillConverter.Width(bar, 46),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    };
                    var track = new Border {
                        Width = 46, Height = 4, Background = border, CornerRadius = new CornerRadius(2), Child = fill,
                    };
                    var mini = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
                    mini.Children.Add(Faint(bar.Label));
                    mini.Children.Add(track);
                    mini.Children.Add(new TextBlock {
                        Text = bar.PercentText, FontSize = 10.5,
                        Foreground = Brush(UsageToneBrushConverter.TextKey(bar.Tone)),
                    });
                    track.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                    Add(mini);
                }
                if (usage.NextResetText is { } reset) Add(Faint(reset));
            }
            if (usage.PerModelText is { } perModel) Add(Faint(perModel));
            return line.Children.Count == 0 ? null : line;
        }

        Button AccountRow(AccountChoice choice) {
            var account = choice.Row;
            var title = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            title.Children.Add(new TextBlock {
                Text = account.Label, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = text,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            if (choice.Removed)
                title.Children.Add(new TextBlock {
                    Text = " — removed", FontSize = 13.5, Foreground = muted,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
            if (account.IsDefault)
                title.Children.Add(new Border {
                    Background = raised, BorderBrush = border, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 3), Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Child = new TextBlock { Text = "Default", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = muted },
                });
            var usage = choice.Removed || account.Id is not { } usageId ? null
                : drawnUsage.TryGetValue(usageId, out var view) ? view : null;
            if (usage?.BlockedText is { } blockedText)
                title.Children.Add(new Border {
                    Background = Brush("KcapDangerDimBrush"), CornerRadius = new CornerRadius(999),
                    Padding = new Thickness(8, 3), Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Child = new TextBlock { Text = blockedText, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush("KcapDangerBrush") },
                });

            var titles = new StackPanel();
            titles.Children.Add(title);
            // A removed row is the choice the snapshot no longer lists, so it has no directory or state to show.
            if (!choice.Removed) {
                var sub = new StackPanel {
                    Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0),
                };
                sub.Children.Add(new TextBlock { Text = account.Directory, FontSize = 10.5, Foreground = faint });
                if (!string.Equals(account.RecordingState, recording, StringComparison.Ordinal))
                    sub.Children.Add(new TextBlock {
                        Text = RecordingStateLabels.Label(account.Vendor, account.RecordingState), FontSize = 10.5,
                        Foreground = RecordingStateLabels.IsAttention(account.Vendor, account.RecordingState) ? warning : muted,
                    });
                titles.Children.Add(sub);
                if (usage is not null && UsageLine(usage) is { } usageLine) titles.Children.Add(usageLine);
            }

            var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            body.Children.Add(titles);
            var check = SelectionCheck(choice.Selected);
            Grid.SetColumn(check, 1);
            body.Children.Add(check);

            var id = account.Id;
            var row = ChoiceButton(body, () => PickAccount(id));
            row.Padding = new Thickness(10, 7);
            AutomationProperties.SetName(row, ChoiceName(account.Label, choice.Selected));
            return row;
        }

        // What the Account section draws, or null when it is absent: the rows belong to the selected
        // vendor and are not searched. The pending line stands in for the rows even while the
        // view-model still publishes rows, so they are left out of it.
        (string? Pending, bool Retry, IReadOnlyList<AccountChoice> Rows)? AccountsToDraw() =>
            vm.AccountSection && (searchBox.Text?.Trim() ?? "").Length == 0
         && string.Equals(currentTab, vm.SelectedVendor, StringComparison.OrdinalIgnoreCase)
                ? vm.AccountPendingText is { } pending ? (pending, vm.AccountRetryVisible, []) : (null, false, vm.AccountRows)
                : null;

        (string? Pending, bool Retry, IReadOnlyList<AccountChoice> Rows)? drawnAccounts = null;

        void AddAccountSection(string? pending, bool retryVisible, IReadOnlyList<AccountChoice> choices) {
            rows.Children.Add(new TextBlock { Text = "Account", Classes = { "sectionMeta" }, Margin = new Thickness(10, 4, 10, 2) });
            if (pending is not null) {
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 4) };
                line.Children.Add(new TextBlock {
                    Text = pending, FontSize = 12.5, Foreground = muted,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
                if (retryVisible) {
                    var retry = new Button {
                        Content = "Retry", Command = vm.RetryAccountsCommand, Padding = new Thickness(10, 3), FontSize = 12,
                    };
                    retry.Classes.Add("kcapChip");
                    Grid.SetColumn(retry, 1);
                    line.Children.Add(retry);
                }
                rows.Children.Add(line);
            } else {
                foreach (var choice in choices) rows.Children.Add(AccountRow(choice));
            }
            rows.Children.Add(ChoiceSeparator());
        }

        void AddVendorRows(HarnessOption option, Func<ModelChoice, bool> matches, bool includeDefault) {
            var vendor = option.Vendor;
            var isCurrentVendor = string.Equals(vm.SelectedVendor, vendor, StringComparison.OrdinalIgnoreCase);
            var unavailableTip = vm.UnavailableHarnessTip(option);
            if (includeDefault)
                rows.Children.Add(Row(
                    vendor, option.Label, $"Default — {option.Label} chooses", unavailableTip,
                    isCurrentVendor && vm.SelectedModel.Length == 0, () => Pick(vendor, "")));
            foreach (var model in vm.ModelChoicesFor(vendor).Where(matches))
                rows.Children.Add(Row(
                    vendor, option.Label, model.Label, unavailableTip,
                    isCurrentVendor && string.Equals(vm.SelectedModel, model.Slug, StringComparison.OrdinalIgnoreCase),
                    () => Pick(vendor, model.Slug)));
        }

        void RebuildRows() {
            rows.Children.Clear();
            var term = searchBox.Text?.Trim() ?? "";
            drawnAccounts = AccountsToDraw();
            drawnUsage = vm.AccountUsageById;

            if (term.Length == 0) {
                if (drawnAccounts is { } accounts) AddAccountSection(accounts.Pending, accounts.Retry, accounts.Rows);
                var option = vm.Harnesses.FirstOrDefault(
                    o => string.Equals(o.Vendor, currentTab, StringComparison.OrdinalIgnoreCase));
                if (option is not null) AddVendorRows(option, _ => true, includeDefault: true);
                return;
            }

            bool Matches(string s) => s.Contains(term, StringComparison.OrdinalIgnoreCase);
            foreach (var option in vm.Harnesses) {
                var vendorMatches = Matches(option.Label) || Matches(option.Vendor);
                AddVendorRows(
                    option,
                    vendorMatches ? _ => true : m => RowMatches(m, term),
                    includeDefault: vendorMatches || Matches("default"));
            }

            // The escape hatch: whatever was typed, offered verbatim for the active vendor tab.
            if (!vm.Harnesses.SelectMany(o => vm.ModelChoicesFor(o.Vendor))
                    .Any(m => string.Equals(m.Slug, term, StringComparison.OrdinalIgnoreCase))) {
                var tabOption = vm.Harnesses.FirstOrDefault(
                    o => string.Equals(o.Vendor, currentTab, StringComparison.OrdinalIgnoreCase));
                var tabLabel = tabOption?.Label ?? currentTab;
                rows.Children.Add(Row(
                    currentTab, tabLabel, $"Use “{term}”", tabOption is null ? null : vm.UnavailableHarnessTip(tabOption),
                    selected: false, () => Pick(currentTab, term)));
            }
        }

        void RebuildTabs() {
            tabs.Children.Clear();
            foreach (var option in vm.Harnesses) {
                var vendor = option.Vendor;
                var active = string.Equals(vendor, currentTab, StringComparison.OrdinalIgnoreCase);
                var tile = new Button {
                    Content = BuildGlyph(vendor, 16), IsEnabled = option.Available,
                    Width = 34, Height = 34, Padding = new Thickness(0),
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Background = active ? raised : Brushes.Transparent,
                    BorderBrush = active ? border : Brushes.Transparent, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Opacity = option.Available ? 1 : 0.4,
                };
                tile.Classes.Add(active ? "kcapChip" : "kcapGhost");
                var unavailable = vm.UnavailableHarnessTip(option);
                ToolTip.SetTip(tile, unavailable ?? $"{option.Label} — {HostedHarnessCatalog.DescriptionFor(option)}");
                if (unavailable is not null) AutomationProperties.SetHelpText(tile, unavailable);
                ToolTip.SetShowOnDisabled(tile, true);
                AutomationProperties.SetName(tile, option.Label);
                tile.Click += (_, _) => {
                    currentTab = vendor;
                    RebuildTabs();
                    RebuildRows();
                };
                tabs.Children.Add(tile);
            }
        }

        // The account choice can resolve while the panel is open; a closed panel keeps no handler.
        // AccountRows is a fresh list on every listing or snapshot emission, and a rebuild drops focus
        // and hover, so only a change to what the section draws rebuilds.
        void OnAccountChanged(object? _, PropertyChangedEventArgs e) {
            if (e.PropertyName is not (nameof(HomeViewModel.AccountSection) or nameof(HomeViewModel.AccountRows)
                    or nameof(HomeViewModel.AccountPendingText) or nameof(HomeViewModel.AccountRetryVisible)
                    or nameof(HomeViewModel.AccountUsageById)))
                return;
            var next = AccountsToDraw();
            var same = drawnAccounts is { } drawn && next is { } now
                ? drawn.Pending == now.Pending && drawn.Retry == now.Retry && drawn.Rows.SequenceEqual(now.Rows)
                    && HomeViewModel.DrawnUsageEquals(drawnUsage, vm.AccountUsageById)
                : drawnAccounts is null && next is null;
            if (!same) RebuildRows();
        }
        vm.PropertyChanged += OnAccountChanged;
        flyout.Closed += (_, _) => vm.PropertyChanged -= OnAccountChanged;

        searchBox.TextChanged += (_, _) => RebuildRows();
        RebuildTabs();
        RebuildRows();
        ShowMenu(flyout, anchor);
        searchBox.Focus();
    }
}

/// The vendor's mark as bindable content (AgentChip's leading glyph), from a vendor token or a
/// HarnessId. One-way; parameter is the size in pixels. Bindings evaluate on the UI thread, which
/// BuildGlyph requires.
public sealed class VendorGlyphConverter : IValueConverter {
    public static readonly VendorGlyphConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is HarnessId id ? id.VendorId : value) is string vendor
            ? LauncherPaneView.BuildGlyph(vendor, double.TryParse(parameter as string, out var size) ? size : 13)
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// AgentChip's label: "Claude · Fable 5" — vendor label plus the model's label, resolved first
/// against the effective model catalog (4th binding), then the curated fallback (raw slug when neither
/// carries it, "Default" for the "" sentinel). Same "left · right" shape as Effort/Permissions. The
/// chosen account (5th binding) follows as "· work", and is null while the default is chosen.
public sealed class AgentChipTextConverter : IMultiValueConverter {
    public static readonly AgentChipTextConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [IReadOnlyList<HarnessOption> options, string vendor, string model, ..]
            ? $"{HostedHarnessCatalog.LabelFor(options, vendor)} · {ModelLabel(vendor, model, values.Count > 3 ? values[3] : null)}"
              + (values.Count > 4 && values[4] is string account ? $" · {account}" : "")
            : "";

    static string ModelLabel(string vendor, string model, object? catalog) {
        if (!string.IsNullOrWhiteSpace(model)
         && catalog is IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> map
         && map.TryGetValue(vendor, out var models)
         && models.FirstOrDefault(m => string.Equals(m.Slug, model, StringComparison.OrdinalIgnoreCase)) is { } hit)
            return hit.Label;
        return HostedHarnessCatalog.ModelLabelFor(vendor, model);
    }
}

/// EffortChip's label: always "Effort · …" — Default when null, otherwise the chosen rung's
/// sentence-case label. Keeps the category visible after a pick.
public sealed class EffortChipTextConverter : IValueConverter {
    public static readonly EffortChipTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        $"Effort · {HostedHarnessCatalog.EffortLabelFor(value as string)}";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// PermissionChip's label: always "Permissions · …" so a bare "Manual" is not mistaken for an
/// effort or harness setting.
public sealed class PermissionChipTextConverter : IValueConverter {
    public static readonly PermissionChipTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string token
            ? $"Permissions · {HostedHarnessCatalog.PermissionModeLabelFor(token)}"
            : "Permissions · Manual";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PermissionChipVisibleConverter : IValueConverter {
    public static readonly PermissionChipVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string vendor && HostedHarnessCatalog.SupportsPermissionMode(vendor);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Leaf under the fixed question; empty when nothing is selected (the subtitle is then hidden).
public sealed class LauncherRepoSubtitleConverter : IValueConverter {
    public static readonly LauncherRepoSubtitleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path ? RepoLabel.Leaf(path) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Repo chip tip: full path when selected, otherwise the control's job label.
public sealed class RepositoryChipTipConverter : IValueConverter {
    public static readonly RepositoryChipTipConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path ? path : "Repository for the new session";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// MachineChip's label: always "Machine · …" so the chip's job stays readable next to Repo.
public sealed class MachineLabelConverter : IValueConverter {
    public static readonly MachineLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } name ? $"Machine · {name}" : "Machine";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// StartButton's tip: an upload in flight owns the button, so it says so rather than leaving the
/// repository-gate wording standing over a control the user cannot use.
public sealed class StartButtonTipConverter : IMultiValueConverter {
    public static readonly StartButtonTipConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [_, true] ? "Uploading…" : values is [string tip, ..] ? tip : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
