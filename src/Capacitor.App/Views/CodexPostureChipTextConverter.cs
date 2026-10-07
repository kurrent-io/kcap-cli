using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// "Sandbox · …" / "Approvals · …": the category stays visible after a pick.
public sealed class CodexPostureChipTextConverter : IValueConverter {
    public static readonly CodexPostureChipTextConverter Sandbox =
        new("Sandbox", HostedHarnessCatalog.CodexSandboxLabelFor, HostedHarnessCatalog.DefaultCodexSandbox);

    public static readonly CodexPostureChipTextConverter Approval =
        new("Approvals", HostedHarnessCatalog.CodexApprovalLabelFor, HostedHarnessCatalog.DefaultCodexApproval);

    readonly string _category;
    readonly Func<string, string> _label;
    readonly string _fallback;

    CodexPostureChipTextConverter(string category, Func<string, string> label, string fallback) {
        _category = category;
        _label    = label;
        _fallback = fallback;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        $"{_category} · {_label(value as string ?? _fallback)}";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
