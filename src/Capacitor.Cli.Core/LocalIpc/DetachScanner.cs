namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>
/// Scans the local terminal's raw stdin for the detach sequence — the prefix byte
/// <c>Ctrl-Q</c> (<c>0x11</c>) followed by <c>d</c> — and strips it before the rest is
/// forwarded to the agent. The prefix is held back until the next byte arrives (possibly
/// in a later read), so the sequence is detected even when split across reads. A prefix
/// not followed by <c>d</c> is forwarded as-is, so Ctrl-Q stays usable otherwise.
/// </summary>
public sealed class DetachScanner {
    const byte Prefix = 0x11;        // Ctrl-Q
    const byte Detach = (byte)'d';

    bool _armed; // saw the prefix; the next byte decides detach-vs-forward

    /// <summary>
    /// Feeds a chunk of raw stdin. Returns the bytes to forward to the agent and whether a
    /// detach was requested. When a detach is returned, any bytes after it in the same chunk
    /// are intentionally dropped (the session is ending).
    /// </summary>
    public (byte[] Forward, bool Detach) Process(ReadOnlySpan<byte> input) {
        var outBuf = new List<byte>(input.Length + 1);

        foreach (var b in input) {
            if (_armed) {
                _armed = false;

                if (b == Detach) return (outBuf.ToArray(), true); // strip prefix + 'd'

                outBuf.Add(Prefix); // prefix was not a detach — forward it now

                if (b == Prefix) { _armed = true; continue; } // a fresh prefix — re-arm

                outBuf.Add(b);
            } else if (b == Prefix) {
                _armed = true; // hold; decide on the next byte
            } else {
                outBuf.Add(b);
            }
        }

        return (outBuf.ToArray(), false);
    }
}
