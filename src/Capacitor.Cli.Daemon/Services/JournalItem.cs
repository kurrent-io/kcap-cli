using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// One queued journal append: the envelope, plus how many envelopes were dropped immediately before
/// it. The count rides the item so the note lands between the last kept line and the first line
/// after the loss, wherever the writer happens to be.
internal readonly record struct JournalItem(AcpEventEnvelope Envelope, int GapBefore);
