using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// A scripted IAttachmentUploader. Setting <see cref="Pending"/> suspends the next upload so a
/// test can act while the launch is still in flight; every call's files are recorded.
sealed class ScriptedUploader : IAttachmentUploader {
    public UploadOutcome Next = new(UploadKind.Uploaded, ["u1"], null);
    public TaskCompletionSource<UploadOutcome>? Pending;
    public List<IReadOnlyList<StagedAttachment>> Calls { get; } = [];

    public Task<UploadOutcome> UploadAsync(IReadOnlyList<StagedAttachment> files, CancellationToken ct) {
        Calls.Add(files);
        if (Pending is not { } pending) return Task.FromResult(Next);
        Pending = null;
        return pending.Task;
    }
}
