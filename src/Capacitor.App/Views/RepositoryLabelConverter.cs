using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// RepositoryChip's label: always "Repo · …" so the chip's job stays readable next to
/// Effort/Permissions — leaf name, or "No repository" for HomeViewModel.ScratchRepoPath
/// ("" — RepoLabel.Leaf("") returns "", not the sentinel this chip needs).
public sealed class RepositoryLabelConverter : IValueConverter {
    public static readonly RepositoryLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path ? $"Repo · {RepoLabel.Leaf(path)}" : "Repo · No repository";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
