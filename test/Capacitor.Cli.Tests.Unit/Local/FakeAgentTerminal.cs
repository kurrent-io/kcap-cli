using System.Text;
using Capacitor.Cli.Local;

namespace Capacitor.Cli.Tests.Unit.Local;

/// <summary>A terminal whose input never arrives until it is disposed, and whose output is recorded.</summary>
sealed class FakeAgentTerminal : IAgentTerminal, IDisposable {
    readonly ManualResetEventSlim _closed = new();
    readonly List<byte> _output = [];

    public string Output {
        get { lock (_output) return Encoding.UTF8.GetString([.. _output]); }
    }

    public (ushort Cols, ushort Rows) Size() => (80, 24);

    public IDisposable EnterRawMode() => new NoRawMode();

    public int Read(byte[] buffer) {
        _closed.Wait();

        return 0;
    }

    public void Write(byte[] data, int length) {
        lock (_output) _output.AddRange(data.AsSpan(0, length));
    }

    public void Dispose() => _closed.Set();

    sealed class NoRawMode : IDisposable {
        public void Dispose() { }
    }
}
