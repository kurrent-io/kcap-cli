namespace Capacitor.App.Services;

/// Unavailable is lane trouble, never a verdict; Denied is the server's refusal and stays until
/// the server says otherwise through an access-changed ping or a reconnect re-check.
public enum SessionAccessState { Establishing, Established, Denied, Unavailable }
