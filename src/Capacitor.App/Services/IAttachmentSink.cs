namespace Capacitor.App.Services;

/// What every intake source — paste, drop, file picker — hands its result to. One surface owns the
/// tray and the notice, so a source never decides what staging or a refusal looks like.
public interface IAttachmentSink {
    /// False carries its reason in <see cref="AttachHint"/>.
    bool CanAttach { get; }
    string? AttachHint { get; }
    /// How many more files the prompt can take; intake opens nothing past it.
    int FreeSlots { get; }
    void Accept(IntakeResult result);
}
