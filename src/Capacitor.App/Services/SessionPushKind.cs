namespace Capacitor.App.Services;

/// The server's session pushes, each carrying the session id.
public enum SessionPushKind { ActiveAdded, ActiveChanged, ActiveRemoved, TitleChanged, Deleted }
