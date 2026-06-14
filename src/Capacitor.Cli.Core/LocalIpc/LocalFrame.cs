namespace Capacitor.Cli.Core.LocalIpc;

public enum WorkLocation : byte { BorrowedCwd = 0, OwnedWorktree = 1 }

/// A decoded frame. Exactly one of the payload properties is meaningful per Type;
/// the codec owns which. Bytes are raw (Stdin/Stdout); strings are UTF-8.
public sealed record LocalFrame(FrameType Type) {
    public byte[]       Bytes    { get; init; } = [];
    public string       Text     { get; init; } = "";
    public ushort       Cols     { get; init; }
    public ushort       Rows     { get; init; }
    public WorkLocation Work     { get; init; }
    public int          ExitCode { get; init; }

    public static LocalFrame Stdin(byte[] b)            => new(FrameType.Stdin)  { Bytes = b };
    public static LocalFrame Stdout(byte[] b)           => new(FrameType.Stdout) { Bytes = b };
    public static LocalFrame Resize(ushort c, ushort r) => new(FrameType.Resize) { Cols = c, Rows = r };
    public static LocalFrame Detach()                   => new(FrameType.Detach);
    public static LocalFrame Exited(int code)           => new(FrameType.Exited) { ExitCode = code };
    public static LocalFrame Error(string m)            => new(FrameType.Error)  { Text = m };
}
