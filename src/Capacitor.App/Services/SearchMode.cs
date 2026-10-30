namespace Capacitor.App.Services;

public enum SearchMode { Sessions, Transcripts }

public static class SearchModeExtensions {
    public static string ToWire(this SearchMode mode) => mode switch {
        SearchMode.Sessions => "sessions",
        SearchMode.Transcripts => "transcripts",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };
}
