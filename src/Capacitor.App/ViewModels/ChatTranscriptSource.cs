using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

/// Which reader a session's transcript_path gets, stated on the wire by transcript_format. Null is an
/// older daemon: its PTY agents take today's vendor path, anything else cannot be read.
internal static class ChatTranscriptSource {
    public const string OlderDaemonNote = "Update the daemon to view this session";
    public const string NewerDaemonNote = "Update the app to view this session";

    public static (IChatTranscriptProjection? Projection, string? UnavailableNote) Resolve(AgentStatusDto dto) => dto.TranscriptFormat switch {
        TranscriptFormats.Vendor    => (TranscriptChat.For(dto.Vendor), null),
        TranscriptFormats.Envelopes => (TranscriptChat.Journal, null),
        null => HostedHarnessCatalog.ShowsTerminal(dto.HasTerminal, dto.Vendor) ? (TranscriptChat.For(dto.Vendor), null) : (null, OlderDaemonNote),
        _    => (null, NewerDaemonNote),
    };
}
