using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

sealed class ChatPromptUploader : IAttachmentUploader {
    public Task<UploadOutcome> UploadAsync(IReadOnlyList<StagedAttachment> files, CancellationToken ct) =>
        Task.FromResult(new UploadOutcome(UploadKind.Uploaded, ["file-1"], null));
}
