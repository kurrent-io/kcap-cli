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
    const int StepsPerUnit = 10;

    public static double Normalize(double? scale) {
        if (scale is not { } value || !double.IsFinite(value)) return Default;

        // Multiplied rather than divided by the step: 0.95 / 0.1 lands just below 9.5 and would round down.
        return Math.Round(Math.Round(Math.Clamp(value, Min, Max) * StepsPerUnit, MidpointRounding.AwayFromZero) / StepsPerUnit, 2);
    }

    public static double Larger(double scale) => Normalize(Normalize(scale) + Step);

    public static double Smaller(double scale) => Normalize(Normalize(scale) - Step);

    /// <summary>A window minimum that leaves the magnified content the room it is laid out for, without
    /// growing past what the screen offers. Never below the unscaled minimum.</summary>
    public static double Minimum(double unscaled, double scale, double? available) =>
        Math.Max(unscaled, Math.Min(unscaled * Normalize(scale), available ?? double.MaxValue));

    public static string Label(double scale) =>
        ((int)Math.Round(Normalize(scale) * 100)).ToString(CultureInfo.InvariantCulture) + "%";
}
