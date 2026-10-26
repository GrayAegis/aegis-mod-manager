using DD2ModManager.Core.Archives;
using DD2ModManager.Core.Util;

namespace DD2ModManager.Core.Library;

/// <summary>One file a mod unit puts into the game folder.</summary>
/// <param name="Source">Path inside the archive.</param>
/// <param name="Target">Game-relative destination. Paks are "pak_mods/name.pak"; the installer adds the order prefix.</param>
public sealed record FileMapping(string Source, string Target, long Size, uint? Crc, bool IsPak);

/// <summary>
/// An installable mod inside a package. Most archives hold one; some (e.g. Content Editor) hold several
/// sibling folders that each have a modinfo.ini, and Fluffy treats each as its own mod.
/// </summary>
public sealed class ModUnit
{
    /// <summary>Same identifier Fluffy uses as the installed.ini section: the mod folder name, or the archive name.</summary>
    public required string Id { get; init; }
    public required string Root { get; init; }
    public ModInfo? Info { get; init; }
    public string? ScreenshotPath { get; init; }
    public required IReadOnlyList<FileMapping> Files { get; init; }

    public string DisplayName => Info?.Name ?? Id;
    public bool HasPaks => Files.Any(f => f.IsPak);
}

/// <summary>Works out mod units and their install targets from an archive's file list, following Fluffy's rules.</summary>
public static class ModLayout
{
    public const string PakFolder = "pak_mods";

    private static readonly HashSet<string> GameRootFolders = new(PathUtil.Comparer) { "natives", "reframework", PakFolder };
    private static readonly HashSet<string> ImageExtensions = new(PathUtil.Comparer) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
    private static readonly HashSet<string> IgnoredNames = new(PathUtil.Comparer) { "thumbs.db", ".ds_store", "desktop.ini" };
    private static readonly string[] IgnoredFolders = [".git", "__MACOSX"];

    public static IReadOnlyList<ModUnit> Analyze(IReadOnlyList<SourceEntry> files, string packageName, Func<string, byte[]?> readFile)
    {
        var usable = files.Where(f => !IsIgnored(f.Path)).ToList();
        if (usable.Count == 0) return [];

        var modInfos = usable
            .Where(f => PathUtil.Comparer.Equals(PathUtil.FileName(f.Path), "modinfo.ini"))
            .ToList();

        var roots = new List<(string Root, string Id, string? ModInfoPath)>();
        if (modInfos.Count > 0)
        {
            // Like Fluffy, lock onto the modinfo.ini files closest to the archive root.
            var minDepth = modInfos.Min(f => PathUtil.Depth(f.Path));
            foreach (var mi in modInfos.Where(f => PathUtil.Depth(f.Path) == minDepth))
            {
                var root = PathUtil.Directory(mi.Path);
                roots.Add((root, root.Length == 0 ? packageName : PathUtil.FileName(root), mi.Path));
            }
        }
        else
        {
            var root = DescendWrappers(usable);
            var variantFolders = VariantFolders(usable, root);
            if (variantFolders.Count > 1)
                roots.AddRange(variantFolders.Select(v => (v, PathUtil.FileName(v), (string?)null)));
            else
                roots.Add((root, packageName, null));
        }

        var units = new List<ModUnit>();
        foreach (var (root, id, modInfoPath) in roots)
        {
            var info = modInfoPath != null && readFile(modInfoPath) is { } bytes ? ModInfo.Parse(bytes) : null;
            var prefix = root.Length == 0 ? "" : root + "/";
            var inRoot = usable.Where(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            var screenshot = FindScreenshot(inRoot, prefix, info);

            var mappings = new List<FileMapping>();
            foreach (var f in inRoot)
            {
                var rel = f.Path[prefix.Length..];
                if (IsMetadataFile(rel, prefix + rel, screenshot)) continue;
                var isPak = rel.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);
                var target = isPak ? $"{PakFolder}/{PathUtil.FileName(rel)}" : rel;
                mappings.Add(new FileMapping(f.Path, target, f.Size, f.Crc, isPak));
            }
            if (mappings.Count == 0 && info?.DummyMod != true) continue;
            units.Add(new ModUnit { Id = id, Root = root, Info = info, ScreenshotPath = screenshot, Files = mappings });
        }
        return units;
    }

    private static bool IsIgnored(string path)
    {
        if (IgnoredNames.Contains(PathUtil.FileName(path))) return true;
        var segments = path.Split('/');
        return segments.Any(s => IgnoredFolders.Contains(s, PathUtil.Comparer));
    }

    /// <summary>Files at the unit root that describe the mod rather than belong in the game folder.</summary>
    private static bool IsMetadataFile(string rel, string fullPath, string? screenshot)
    {
        if (rel.Contains('/')) return false;
        if (PathUtil.Comparer.Equals(rel, "modinfo.ini")) return true;
        if (screenshot != null && PathUtil.Comparer.Equals(fullPath, screenshot)) return true;
        var ext = Path.GetExtension(rel);
        return ImageExtensions.Contains(ext) || PathUtil.Comparer.Equals(ext, ".txt");
    }

    private static string? FindScreenshot(List<SourceEntry> inRoot, string prefix, ModInfo? info)
    {
        var rootFiles = inRoot.Where(f => !f.Path[prefix.Length..].Contains('/')).ToList();
        if (info?.Screenshot is { } named)
        {
            var match = rootFiles.FirstOrDefault(f => PathUtil.Comparer.Equals(PathUtil.FileName(f.Path), named));
            if (match != null) return match.Path;
        }
        return rootFiles.FirstOrDefault(f => PathUtil.FileName(f.Path).StartsWith("screenshot", StringComparison.OrdinalIgnoreCase)
                                             && ImageExtensions.Contains(Path.GetExtension(f.Path)))?.Path
               ?? rootFiles.FirstOrDefault(f => ImageExtensions.Contains(Path.GetExtension(f.Path)))?.Path;
    }

    /// <summary>Skips single wrapper folders ("ModName/natives/...") until real content appears.</summary>
    private static string DescendWrappers(List<SourceEntry> files)
    {
        var root = "";
        while (true)
        {
            var prefix = root.Length == 0 ? "" : root + "/";
            var children = files.Select(f => f.Path[prefix.Length..]).ToList();
            if (children.Any(c => !c.Contains('/'))) return root;
            var dirs = children.Select(c => c[..c.IndexOf('/')]).Distinct(PathUtil.Comparer).ToList();
            if (dirs.Count != 1 || GameRootFolders.Contains(dirs[0])) return root;
            root = prefix + dirs[0];
        }
    }

    /// <summary>
    /// Archives without modinfo.ini sometimes ship alternatives side by side ("Option A/natives", "Option B/natives").
    /// Returns those folders when the root itself has no game content.
    /// </summary>
    private static List<string> VariantFolders(List<SourceEntry> files, string root)
    {
        var prefix = root.Length == 0 ? "" : root + "/";
        var children = files.Select(f => f.Path[prefix.Length..]).ToList();
        if (children.Any(c => !c.Contains('/'))) return [];
        var dirs = children.Select(c => c[..c.IndexOf('/')]).Distinct(PathUtil.Comparer).ToList();
        if (dirs.Any(GameRootFolders.Contains)) return [];
        return dirs.Where(d => children.Any(c =>
                c.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase) &&
                (GameRootFolders.Contains(c[(d.Length + 1)..].Split('/')[0]) || c.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))))
            .Select(d => prefix + d)
            .ToList();
    }
}
