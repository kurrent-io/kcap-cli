using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

/// One projected transcript line and where it sits in its source: a byte offset in a file, an
/// event number in a stream. Offsets only ever grow within one source, which is what lets a send
/// be acknowledged by a line that landed after it.
public readonly record struct ProjectedLine(ChatProjectionResult Projection, long Offset);
