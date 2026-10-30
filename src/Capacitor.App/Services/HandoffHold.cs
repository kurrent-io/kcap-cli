namespace Capacitor.App.Services;

/// What holding a hand-off draft found. AlreadyFailed: a failure for the agent arrived before the
/// hold, and the draft waits in TakeFailed. AlreadyStarted: the agent's row is already listed.
public enum HandoffHold { Pending, AlreadyFailed, AlreadyStarted }
