namespace Capacitor.App.Services;

/// Account advances only when the lane connects as another subject; Generation on every lane
/// connect and disconnect.
public sealed record SessionIdentity(int Account, int Generation, bool Connected);
