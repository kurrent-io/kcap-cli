using System.Globalization;

namespace Capacitor.App.Services;

/// How much the main window magnifies its content: text and the layout around it grow together,
/// because control font sizes are fixed per control. Kept to whole steps so a value read back from
/// app state lands on one the shortcuts can step from.
public static class UiScale {
    public const double Default = 1.0;
    public const double Min = 0.8;
    public const double Max = 1.5;
    const double Step = 0.1;

    public static double Normalize(double? scale) {
        if (scale is not { } value || !double.IsFinite(value)) return Default;

        return Math.Round(Math.Round(Math.Clamp(value, Min, Max) / Step, MidpointRounding.AwayFromZero) * Step, 2);
    }

    public static double Larger(double scale) => Normalize(Normalize(scale) + Step);

    public static double Smaller(double scale) => Normalize(Normalize(scale) - Step);

    public static string Label(double scale) =>
        ((int)Math.Round(Normalize(scale) * 100)).ToString(CultureInfo.InvariantCulture) + "%";
}
