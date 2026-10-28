namespace Capacitor.Cli.Service.Installation;

/// <summary>
/// Windows' queue of file operations run at the next restart, <c>PendingFileRenameOperations</c>: pairs of
/// source and target, where an empty target deletes the source. Sources carry the NT prefix <c>\??\</c>,
/// sometimes behind a flag such as <c>*1</c>
/// </summary>
public static class PendingRemovals {
    public const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    public const string ValueName = "PendingFileRenameOperations";

    /// <summary>The queue without its deletions of <paramref name="directory"/> or anything inside it; every
    /// other operation, a rename into the directory included, is kept as it was</summary>
    public static string[] Without(IReadOnlyList<string> operations, string directory) {
        var root = directory.TrimEnd('\\');
        var kept = new List<string>(operations.Count);
        for (var i = 0; i < operations.Count; i += 2) {
            var target = i + 1 < operations.Count ? operations[i + 1] : null;
            if (target is "" && IsWithin(LocalPath(operations[i]), root)) continue;
            kept.Add(operations[i]);
            if (target is not null) kept.Add(target);
        }
        return [.. kept];
    }

    static string LocalPath(string source) {
        var at = source.IndexOf(@"\??\", StringComparison.Ordinal);
        return (at < 0 ? source : source[(at + 4)..]).TrimEnd('\\');
    }

    static bool IsWithin(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase);
}
