using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

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
