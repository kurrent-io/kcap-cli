using Microsoft.AspNetCore.SignalR;

namespace Capacitor.App.Services;

/// SignalR prefixes a server's HubException with "An unexpected error occurred invoking 'X' on the
/// server. HubException: ", which is transport noise to a person reading the refusal.
public static class HubErrorText {
    const string Marker = "HubException: ";

    public static string Of(Exception ex) {
        if (ex is not HubException) return ex.Message;
        var at = ex.Message.IndexOf(Marker, StringComparison.Ordinal);
        return at < 0 ? ex.Message : ex.Message[(at + Marker.Length)..];
    }
}
