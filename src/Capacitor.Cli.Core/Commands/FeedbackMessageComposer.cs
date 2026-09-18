namespace Capacitor.Cli.Core.Commands;

/// <summary>The one wire message a desktop report sends: the reporter's text and a trailer naming
/// the client. The server trims and caps the whole message, so the cap is checked on the composed
/// value, never on the reporter's text alone.</summary>
public static class FeedbackMessageComposer {
    public const int MaxLength = 8000;

    public static string Compose(string userText, string trailer) => $"{userText.Trim()}\n\n{trailer}";

    public static int Remaining(string userText, string trailer) => MaxLength - Compose(userText, trailer).Length;
}
