namespace Capacitor.App.Services;

/// One account of a vendor on one host. A null Id is the vendor's default directory with no
/// registry entry behind it.
public sealed record AccountRow(string? Id, string Vendor, string Directory, string Label, string RecordingState, bool IsDefault);
