namespace Capacitor.App.Services;

public sealed record SearchRequest(string Query, SearchMode Mode, int Limit = 20);
