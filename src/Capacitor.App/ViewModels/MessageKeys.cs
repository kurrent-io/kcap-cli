namespace Capacitor.App.ViewModels;

/// What the server can find a user message by: the stored event's id, its number in the session's root
/// stream, or the dispatch that delivered it. A message holds whichever its feed knows.
public readonly record struct MessageKeys(Guid? EventId = null, long? EventNumber = null, Guid? DispatchId = null) {
    public int Count => (EventId is null ? 0 : 1) + (EventNumber is null ? 0 : 1) + (DispatchId is null ? 0 : 1);
}
