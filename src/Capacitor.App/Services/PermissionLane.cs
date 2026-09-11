namespace Capacitor.App.Services;

/// The lane that delivered a pending request is the only lane that may answer it.
public enum PermissionLane { Local, Server }
