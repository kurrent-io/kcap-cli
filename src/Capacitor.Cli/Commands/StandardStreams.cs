namespace Capacitor.Cli.Commands;

/// <summary>
/// The console a command talks to, supplied rather than read from <see cref="Console"/> so a test can
/// hand each run its own writers and run beside other tests.
/// </summary>
public sealed record StandardStreams(TextWriter Out, TextWriter Error, TextReader In) {
    public static StandardStreams FromProcess() => new(Console.Out, Console.Error, Console.In);
}
