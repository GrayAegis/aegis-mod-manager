namespace DD2ModManager.Core.Util;

public static class PathUtil
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Normalizes an archive or game-relative path to forward slashes with no leading separator.
    /// Returns null for paths that try to escape their root ("..", drive letters, UNC).
    /// </summary>
    public static string? NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Replace('\\', '/').Trim();
        if (p.Contains(':')) return null;
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..") return null;
            kept.Add(part);
        }
        return kept.Count == 0 ? null : string.Join('/', kept);
    }

    public static string Combine(string root, string relative)
    {
        var normalized = NormalizeRelative(relative) ?? throw new ArgumentException($"Unsafe path: {relative}");
        var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path escapes root: {relative}");
        return full;
    }

    public static string FileName(string relative)
    {
        var i = relative.LastIndexOf('/');
        return i < 0 ? relative : relative[(i + 1)..];
    }

    public static string Directory(string relative)
    {
        var i = relative.LastIndexOf('/');
        return i < 0 ? "" : relative[..i];
    }

    public static int Depth(string relative) => relative.Count(c => c == '/');

    /// <summary>Deletes empty directories from <paramref name="startDir"/> upward, stopping at <paramref name="stopAt"/>.</summary>
    public static void PruneEmptyDirectories(string startDir, string stopAt, ISet<string>? keep = null)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        var dir = Path.GetFullPath(startDir).TrimEnd(Path.DirectorySeparatorChar);
        while (dir.Length > stop.Length && dir.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
        {
            if (keep != null && keep.Contains(dir)) return;
            if (!System.IO.Directory.Exists(dir) || System.IO.Directory.EnumerateFileSystemEntries(dir).Any()) return;
            System.IO.Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir) ?? stop;
        }
    }
}
