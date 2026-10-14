namespace Capacitor.Cli.Core.NextWork;

public sealed record NextWorkRead(NextWorkReadKind Kind, IReadOnlyList<NextWorkFeedRowDto> Items) {
    public static NextWorkRead Of(NextWorkReadKind kind) => new(kind, []);
}
