namespace Capacitor.Cli.Local;

/// <summary>The local end of an attached agent session: raw input, raw output and the window size.</summary>
internal interface IAgentTerminal {
    (ushort Cols, ushort Rows) Size();

    /// <summary>Raw mode until the returned token is disposed.</summary>
    IDisposable EnterRawMode();

    /// <summary>Blocks for input; 0 means end of input.</summary>
    int Read(byte[] buffer);

    void Write(byte[] data, int length);
}
