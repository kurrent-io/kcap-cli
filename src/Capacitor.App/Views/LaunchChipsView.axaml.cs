using Lucide.Avalonia;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.App.Views;

/// The agent, effort, permission and Codex posture chips over an ILaunchChoices. DataContext is
/// supplied externally; the view never builds its own ViewModel.
public partial class LaunchChipsView : UserControl {
    Flyout? _openMenu;

    public static readonly StyledProperty<Control?> LeadingProperty =
        AvaloniaProperty.Register<LaunchChipsView, Control?>(nameof(Leading));

    /// Placed first in the chip row, so it wraps with the chips instead of ahead of them as a block.
    public Control? Leading {
        get => GetValue(LeadingProperty);
        set => SetValue(LeadingProperty, value);
    }

    static LaunchChipsView() {
        LeadingProperty.Changed.AddClassHandler<LaunchChipsView>((view, e) => view.ReplaceLeading(e.OldValue as Control, e.NewValue as Control));
    }

    public LaunchChipsView() {
        InitializeComponent();
    }

    void ReplaceLeading(Control? old, Control? next) {
        if (old is not null) ChipsPanel.Children.Remove(old);
        if (next is not null) ChipsPanel.Children.Insert(0, next);
    }

    internal Flyout? OpenMenuForTesting => _openMenu;

    /// A picker search term matches a model by its label or its launch id, so a Pi row is found by
    /// model name, provider, or `provider/` prefix alike.
    internal static bool RowMatches(ModelChoice model, string term) =>
        model.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
     || model.Slug.Contains(term, StringComparison.OrdinalIgnoreCase);

    // Effort picker over HostedHarnessCatalog.EffortLadder; Default hands the choice back to the
    // harness. A wrong value for a given vendor surfaces as that session's own launch error, same
    // as the CLI's --effort. Separator after Default matches Permissions (base vs escalations).
    void OnEffortChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not ILaunchChoices vm || sender is not Control anchor) return;

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var flyout = ChoiceMenus.PanelFlyout(rows, minWidth: 180);

        rows.Children.Add(this.ChoiceRow(HostedHarnessCatalog.EffortDefaultLabel, vm.SelectedEffort is null, () => {
            vm.SelectedEffort = null;
            flyout.Hide();
        }));
        rows.Children.Add(this.ChoiceSeparator());
        foreach (var effort in HostedHarnessCatalog.EffortLadder) {
            var value = effort;
            rows.Children.Add(this.ChoiceRow(HostedHarnessCatalog.EffortLabelFor(value), vm.SelectedEffort == effort, () => {
                vm.SelectedEffort = value;
                flyout.Hide();
            }));
        }
        ShowMenu(flyout, anchor);
    }

    void OnPermissionChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not ILaunchChoices vm || sender is not Control anchor) return;

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var flyout = ChoiceMenus.PanelFlyout(rows, minWidth: 200);

        // Manual first (omit-from-wire default), then separator, then escalations — same shape as Effort.
        foreach (var mode in HostedHarnessCatalog.PermissionModes) {
            if (rows.Children.Count == 1)
                rows.Children.Add(this.ChoiceSeparator());

            var token = mode.Token;
            var selected = string.Equals(vm.SelectedPermissionMode, token, StringComparison.Ordinal);
            rows.Children.Add(this.ChoiceRow(mode.Label, selected, () => {
                vm.SelectedPermissionMode = token;
                flyout.Hide();
            }));
        }
        ShowMenu(flyout, anchor);
    }

    void OnCodexSandboxChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is ILaunchChoices vm && sender is Control anchor)
            ShowChoices(anchor, HostedHarnessCatalog.CodexSandboxes, vm.SelectedCodexSandbox, t => vm.SelectedCodexSandbox = t);
    }

    void OnCodexApprovalChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is ILaunchChoices vm && sender is Control anchor)
            ShowChoices(anchor, HostedHarnessCatalog.CodexApprovals, vm.SelectedCodexApproval, t => vm.SelectedCodexApproval = t);
    }

    void ShowChoices(Control anchor, IReadOnlyList<PermissionModeChoice> choices, string current, Action<string> pick) {
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var flyout = ChoiceMenus.PanelFlyout(rows, minWidth: 200);
        foreach (var choice in choices) {
            var token = choice.Token;
            rows.Children.Add(this.ChoiceRow(choice.Label, string.Equals(current, token, StringComparison.Ordinal), () => {
                pick(token);
                flyout.Hide();
            }));
        }
        ShowMenu(flyout, anchor);
    }


    void ShowMenu(Flyout flyout, Control anchor) {
        _openMenu = flyout;
        flyout.ShowAt(anchor);
    }

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
        if (DataContext is not ILaunchChoices vm || sender is not Control anchor) return;

        var text = this.Brush("KcapTextBrush");
        var muted = this.Brush("KcapMutedBrush");
        var faint = this.Brush("KcapFaintBrush");
        var raised = this.Brush("KcapSurfaceRaisedBrush");
        var border = this.Brush("KcapBorderBrush");
        var warning = this.Brush("KcapWarningBrush");
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
            var check = this.SelectionCheck(selected);
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
            AutomationProperties.SetName(row, ChoiceMenus.ChoiceName($"{label}, {vendorLabel}", selected));
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
                        Background = this.Brush(UsageToneBrushConverter.FillKey(bar.Tone)), CornerRadius = new CornerRadius(2),
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
                        Foreground = this.Brush(UsageToneBrushConverter.TextKey(bar.Tone)),
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
                    Background = this.Brush("KcapDangerDimBrush"), CornerRadius = new CornerRadius(999),
                    Padding = new Thickness(8, 3), Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Child = new TextBlock { Text = blockedText, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = this.Brush("KcapDangerBrush") },
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
            var check = this.SelectionCheck(choice.Selected);
            Grid.SetColumn(check, 1);
            body.Children.Add(check);

            var id = account.Id;
            var row = ChoiceMenus.ChoiceButton(body, () => PickAccount(id));
            row.Padding = new Thickness(10, 7);
            AutomationProperties.SetName(row, ChoiceMenus.ChoiceName(account.Label, choice.Selected));
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
            rows.Children.Add(this.ChoiceSeparator());
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
            if (e.PropertyName is not (nameof(ILaunchChoices.AccountSection) or nameof(ILaunchChoices.AccountRows)
                    or nameof(ILaunchChoices.AccountPendingText) or nameof(ILaunchChoices.AccountRetryVisible)
                    or nameof(ILaunchChoices.AccountUsageById)))
                return;
            var next = AccountsToDraw();
            var same = drawnAccounts is { } drawn && next is { } now
                ? drawn.Pending == now.Pending && drawn.Retry == now.Retry && drawn.Rows.SequenceEqual(now.Rows)
                    && AccountUsageView.DrawnUsageEquals(drawnUsage, vm.AccountUsageById)
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
