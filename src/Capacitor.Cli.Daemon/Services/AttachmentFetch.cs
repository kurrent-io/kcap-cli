namespace Capacitor.Cli.Daemon.Services;

/// The outcome of one batch fetch: a published batch, or the id that ended it and why.
internal sealed record AttachmentFetch(AttachmentBatch? Batch, string? FailedId, string? Error);
