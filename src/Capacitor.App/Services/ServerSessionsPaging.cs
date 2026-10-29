namespace Capacitor.App.Services;

/// Where the ended prefix stands: Loaded rows of Total in the scope, over Pages pages; AtCap once
/// the listing's offset limit stops further pages.
public sealed record ServerSessionsPaging(int Loaded, int Total, int Pages, bool CanShowMore, bool AtCap);
