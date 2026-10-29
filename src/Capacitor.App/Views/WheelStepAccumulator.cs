namespace Capacitor.App.Views;

/// Accumulates sub-line gestures without retaining an oversized event's discarded whole steps.
internal struct WheelStepAccumulator {
    double _remainder;

    public int Take(double delta, int maximum) {
        if (!double.IsFinite(delta) || delta == 0) return 0;
        if (Math.Sign(delta) != Math.Sign(_remainder)) _remainder = 0;
        var total = _remainder + delta;
        var whole = Math.CopySign(Math.Floor(Math.Abs(total) + 1e-9), total);
        _remainder = total - whole;
        if (Math.Abs(_remainder) < 1e-9) _remainder = 0;
        return (int)Math.Clamp(whole, -maximum, maximum);
    }
}
