namespace Capacitor.Cli.Core.Http;

/// <summary>What a write came back as. A refusal is a value rather than an exception because every
/// one of these is something the caller can act on.</summary>
public abstract record ArtefactWriteResult {
    public sealed record Written(ArtefactDetailDto Detail) : ArtefactWriteResult;
    public sealed record Gone : ArtefactWriteResult;

    /// <summary>Refused for a reason the server named.</summary>
    public sealed record Refused(ArtefactErrorDto Error) : ArtefactWriteResult;

    /// <summary>No artefact under that id, or none this profile may see — the server does not
    /// distinguish the two, and neither does this.</summary>
    public sealed record NotFound : ArtefactWriteResult;

    /// <summary>Visible, but not this profile's to change.</summary>
    public sealed record NotYours : ArtefactWriteResult;
}
