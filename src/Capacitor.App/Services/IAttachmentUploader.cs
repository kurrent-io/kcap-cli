using Capacitor.App.ViewModels;

namespace Capacitor.App.Services;

public interface IAttachmentUploader {
    Task<UploadOutcome> UploadAsync(IReadOnlyList<StagedAttachment> files, CancellationToken ct);
}
