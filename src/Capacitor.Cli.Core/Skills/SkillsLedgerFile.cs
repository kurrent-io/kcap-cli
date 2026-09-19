using System.Text.Json;

namespace Capacitor.Cli.Core.Skills;

public enum SkillsLedgerRead {
    Missing,
    Loaded,

    /// <summary>There but not readable. Not evidence of anything, and never "no owners".</summary>
    Unreadable,

    /// <summary>There, readable, and in neither shape. It names paths nothing else can, so it is
    /// preserved aside rather than discarded.</summary>
    Corrupt,
}

/// <summary>
/// Reading and writing one ownership ledger. Both shapes are recognised throughout: the one every
/// installed release wrote, which converts on load, and the one this reads back.
/// </summary>
public static class SkillsLedgerFile {
    public static SkillsLedgerRead Read(string path, SkillOrigin origin, out SkillsLedger? ledger) {
        ledger = null;
        if (!File.Exists(path)) return SkillsLedgerRead.Missing;

        string text;
        try {
            // A plain read denies Write and Delete to every other handle, and on Windows that
            // sharing is mandatory — this file has a concurrent writer by design, and its atomic
            // replace renames over the destination.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return SkillsLedgerRead.Unreadable;
        }

        ledger = Parse(text, origin);

        return ledger is null ? SkillsLedgerRead.Corrupt : SkillsLedgerRead.Loaded;
    }

    /// <summary>Reads a ledger, treating an unreadable or unparseable file as absent. A caller that
    /// must not act on a superseded copy holds the lock that owns the file first.</summary>
    public static SkillsLedger? ReadQuietly(string path, SkillOrigin origin) =>
        Read(path, origin, out var ledger) == SkillsLedgerRead.Loaded ? ledger : null;

    public static void Save(string path, SkillsLedger ledger) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Replace(path, JsonSerializer.Serialize(ledger, CapacitorJsonContext.Default.SkillsLedger));
    }

    /// <summary>Converts the shipped shape. Each entry becomes a published row with its receipt
    /// taken from the recorded file hash; an entry without one becomes a claim we cannot vouch for,
    /// which is reported and never deleted, and whose bytes on disk are never adopted as proof.
    /// A converted row records no anchor, so its authority comes from its origin.</summary>
    public static SkillsLedger Convert(SkillsManifest manifest, SkillOrigin origin) => new() {
        Etag = manifest.Etag, SyncedAt = manifest.SyncedAt,
        Owned = [.. (manifest.Skills ?? []).Select(entry => Convert(entry, origin))],
    };

    static SkillsLedger? Parse(string text, SkillOrigin origin) {
        try {
            if (JsonSerializer.Deserialize(text, CapacitorJsonContext.Default.SkillsLedger) is { Owned: not null } ledger)
                return ledger;

            return JsonSerializer.Deserialize(text, CapacitorJsonContext.Default.SkillsManifest) is { Skills: not null } shipped
                ? Convert(shipped, origin)
                : null;
        } catch (JsonException) {
            return null;
        }
    }

    static OwnedSkillRow Convert(SkillsManifestEntry entry, SkillOrigin origin) {
        var row = new OwnedSkillRow {
            Path   = entry.Path,
            Root   = Path.GetDirectoryName(entry.Path) is { Length: > 0 } parent ? parent : entry.Path,
            Origin = origin,
            State  = OwnedSkillState.Unverified,
        };

        return entry.FileHash is not { Length: > 0 } fileHash
            ? row
            : row with {
                State     = OwnedSkillState.Published,
                Confirmed = new SkillReceipt {
                    FileHash = fileHash,
                    Document = new SkillDocument {
                        DocId = entry.DocId, Slug = entry.Slug, Version = entry.Version,
                        ContentHash = entry.ContentHash,
                    },
                },
            };
    }
}
