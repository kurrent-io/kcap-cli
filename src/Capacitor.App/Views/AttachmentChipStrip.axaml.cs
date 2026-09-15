using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The chips staged in one prompt. Chips are matched to tray files by id, so a file that stays
/// keeps the thumbnail it already decoded and one that leaves has its bitmap freed here.
public partial class AttachmentChipStrip : UserControl {
    public static readonly StyledProperty<AttachmentTray?> TrayProperty =
        AvaloniaProperty.Register<AttachmentChipStrip, AttachmentTray?>(nameof(Tray));

    readonly ObservableCollection<StagedAttachmentViewModel> _chips = [];
    INotifyCollectionChanged? _watched;
    bool _attached;

    public AttachmentChipStrip() {
        InitializeComponent();
        ChipItems.ItemsSource = _chips;
    }

    public AttachmentTray? Tray {
        get => GetValue(TrayProperty);
        set => SetValue(TrayProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == TrayProperty) Rebind();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Rebind();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _attached = false;
        Rebind();
        base.OnDetachedFromVisualTree(e);
    }

    /// Chips live only while the strip is in the tree: a strip taken out of it holds neither the
    /// tray subscription nor any decoded bitmap, and re-entering rebuilds both from the tray.
    void Rebind() {
        if (_watched is not null) _watched.CollectionChanged -= OnTrayChanged;
        _watched = _attached ? Tray?.Items : null;
        if (_watched is not null) _watched.CollectionChanged += OnTrayChanged;
        Sync();
    }

    void OnTrayChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    void Sync() {
        var staged = _attached ? Tray?.Items.ToList() ?? [] : [];
        var kept = staged.Select(f => f.Id).ToHashSet();
        var existing = _chips.ToDictionary(c => c.File.Id);
        foreach (var chip in _chips) {
            if (!kept.Contains(chip.File.Id)) chip.Dispose();
        }
        _chips.Clear();
        foreach (var file in staged)
            _chips.Add(existing.TryGetValue(file.Id, out var chip) ? chip : new StagedAttachmentViewModel(file, Remove));
    }

    void Remove(StagedAttachment file) => Tray?.Remove(file);
}
