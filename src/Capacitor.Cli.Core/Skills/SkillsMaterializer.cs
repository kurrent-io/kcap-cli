using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Skills;

/// <summary>What is at a destination, as far as this run could establish. Absence means we looked
/// and nothing is there; the other three are each a different outcome and none of them is
/// absence.</summary>
public enum SkillFileProbe {
    Absent,
    Present,
    Unreadable,
    Unresolvable,
}

/// <summary>
/// The file half of skills materialization, harness-neutral: how a slug maps to a directory under
/// a given skills root, and the write and probe operations. Which roots exist and which vendor
/// each fetches as is the target catalog's business.
/// </summary>
public static class SkillsMaterializer {
    /// <summary>What marks a directory as kcap's inside a skills root the repository also uses. The
    /// write, the deletion guard and the Git exclusion glob all spell it through this constant: a
    /// divergence would stop the exclusion matching what the writer creates, with nothing
    /// failing.</summary>
    public const string OwnedPrefix = "kcap-";

    // The server's slug is already doc-id-anchored and unique; the prefix namespaces the
    // materialized set inside a shared skills root.
    public static string SkillDirFor(string root, string slug) => Path.Combine(root, OwnedPrefix + slug);

    public static string SkillFileFor(string dir) => Path.Combine(dir, "SKILL.md");

    /// <summary>The bytes a rendered skill is published as, and the only thing a receipt is ever
    /// taken over.</summary>
    public static byte[] Encode(string rendered) => Encoding.UTF8.GetBytes(rendered);

    /// <summary>A receipt is taken over the bytes on disk, never over decoded text: a file
    /// re-encoded, or given a byte-order mark, decodes to the same characters and would otherwise
    /// hash equal to a receipt it does not match.</summary>
    public static string FileHash(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static string FileHash(string rendered) => FileHash(Encode(rendered));

    /// <summary>The hash of a file that is there and readable, or null when it is neither.</summary>
    public static string? HashOf(string file) {
        try {
            return FileHash(File.ReadAllBytes(file));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>What a materialized directory holds, and its hash when that is answerable. A
    /// <c>SKILL.md</c> that is itself a link is not readable evidence of anything we wrote, and a
    /// directory we cannot search answers "not found" for everything inside it — which is a refusal
    /// to answer, not absence.</summary>
    public static (SkillFileProbe Probe, string? Hash) Inspect(string dir) {
        if (!CanonicalPath.TryResolve(dir, out _)) return (SkillFileProbe.Unresolvable, null);

        var file = new FileInfo(SkillFileFor(dir));

        if (file.Exists) {
            if (file.LinkTarget is not null) return (SkillFileProbe.Unreadable, null);

            return HashOf(file.FullName) is { } hash
                ? (SkillFileProbe.Present, hash)
                : (SkillFileProbe.Unreadable, null);
        }

        return PathExistence.OfFile(file.FullName) == PathPresence.Missing
            ? (SkillFileProbe.Absent, null)
            : (SkillFileProbe.Unreadable, null);
    }

    /// <summary>Writes one skill, refusing a destination that leaves the anchor through a link: a
    /// vendor directory inside the repository may be a symlink to the user-global tree, which would
    /// publish repository content globally again.</summary>
    public static bool Write(string dir, string anchor, byte[] content) {
        if (!CanonicalPath.IsWithin(dir, anchor)) return false;

        Directory.CreateDirectory(dir);

        var file = SkillFileFor(dir);

        if (File.Exists(file) && new FileInfo(file).LinkTarget is not null) return false;

        AtomicFile.Replace(file, content);

        return true;
    }
}
