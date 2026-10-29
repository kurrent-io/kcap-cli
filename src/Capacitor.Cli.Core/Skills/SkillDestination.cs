namespace Capacitor.Cli.Core.Skills;

/// <summary>A place a row can sit: the directory, the skills root it is a direct child of, and the
/// anchor that root belongs to.</summary>
public readonly record struct SkillDestination(string Path, string Root, string Anchor) {
    public static SkillDestination For(SkillsTarget target, string anchor, string slug) {
        var root = target.Root(anchor);

        return new SkillDestination(SkillsMaterializer.SkillDirFor(root, slug), root, anchor);
    }

    public OwnedSkillRow Place(OwnedSkillRow row) =>
        row with { Path = Path, Root = Root, Anchor = Anchor };
}
