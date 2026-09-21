namespace Capacitor.Cli.Core.Skills;

/// <summary>The root a path must be a direct kcap-owned child of, and the boundary it must resolve
/// inside. Both come from what kcap itself recorded while the anchor was live, never from what a
/// row asserts about its own parent.</summary>
public readonly record struct SkillAuthority(string Root, string Boundary) {
    /// <summary>What authorises acting on <paramref name="row"/>, or null when nothing does: a
    /// repository row whose recorded root is not the one its recorded anchor produces has been
    /// altered under us, and is refused rather than guessed at.</summary>
    public static SkillAuthority? For(OwnedSkillRow row, SkillsTarget target, string anchor) {
        if (row.Origin == SkillOrigin.Legacy) return new SkillAuthority(target.LegacyRoot, target.LegacyRoot);

        // A converted row records no anchor, so this run's own answers for it.
        if (row.Anchor is null) return new SkillAuthority(target.Root(anchor), anchor);

        var root = target.Root(row.Anchor);

        return PathComparison.Equal(CanonicalPath.Resolve(root), CanonicalPath.Resolve(row.Root))
            ? new SkillAuthority(root, row.Anchor)
            : null;
    }
}
