namespace Capacitor.Cli.Local;

/// <summary>The process's own terminal, read and written through the raw file descriptors.</summary>
internal sealed class ConsoleAgentTerminal : IAgentTerminal {
    public static readonly ConsoleAgentTerminal Instance = new();

    ConsoleAgentTerminal() { }

    public (ushort Cols, ushort Rows) Size() {
        try { return ((ushort)Math.Max(1, Console.WindowWidth), (ushort)Math.Max(1, Console.WindowHeight)); }
        catch { return (80, 24); }
    }

    public IDisposable EnterRawMode() => TerminalRawMode.Enable();

    public int Read(byte[] buffer) => TerminalRawMode.ReadStdin(buffer);

    public void Write(byte[] data, int length) => TerminalRawMode.WriteStdout(data, length);
}
