using Capacitor.App.ViewModels;

namespace Capacitor.App.Services;

/// Stands in for <see cref="ServerAttachmentUploader"/> when the app has no server profile.
public sealed class NoAttachmentUploader : IAttachmentUploader {
    public Task<UploadOutcome> UploadAsync(IReadOnlyList<StagedAttachment> files, CancellationToken ct) =>
        Task.FromResult(UploadOutcome.Unauthorized("not_signed_in"));
}
