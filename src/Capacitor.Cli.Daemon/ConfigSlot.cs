namespace Capacitor.Cli.Daemon;

/// <summary>
/// One place in <see cref="DaemonConfig"/>, named once and handed to both directions: the boot
/// binder writes an operator's override through it, the launch reads back whatever ended up there.
/// Naming the property twice is what lets an override land in one place while the launcher reads
/// another, with both spellings valid and nothing failing.
/// </summary>
internal sealed record ConfigSlot<T>(Func<T> Read, Action<T> Write);
