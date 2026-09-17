using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// An attachment sink that records every intake result handed to it, with the gate and its hint
/// under the test's control.
sealed class RecordingSink : IAttachmentSink {
    public bool CanAttachValue { get; set; } = true;
    public string? AttachHintValue { get; set; } = "attachments need the daemon updated";
    public List<IntakeResult> Accepted { get; } = [];

    public bool CanAttach => CanAttachValue;
    public string? AttachHint => AttachHintValue;
    public int FreeSlotsValue { get; set; } = InputWire.MaxAttachmentsPerPrompt;
    public int FreeSlots => FreeSlotsValue;
    public void Accept(IntakeResult result) => Accepted.Add(result);
}
