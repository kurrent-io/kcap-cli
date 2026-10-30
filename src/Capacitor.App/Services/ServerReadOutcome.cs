namespace Capacitor.App.Services;

/// Unavailable is a 403 or 404: the server answered, and the resource is not the caller's to read.
public enum ServerReadOutcome { Ok, Unauthorized, Unavailable, Failed }
