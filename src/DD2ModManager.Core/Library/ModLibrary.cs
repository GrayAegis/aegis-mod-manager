using DD2ModManager.Core.Archives;

namespace DD2ModManager.Core.Library;

/// <summary>A downloaded mod archive (or extracted folder) in the library.</summary>
public sealed class ModPackage
{
    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required long Size { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public required ParsedArchiveName ParsedName { get; init; }
    public required IReadOnlyList<ModUnit> Units { get; init; }
    public string? Error { get; init; }

    public bool IsFolder => Directory.Exists(FullPath);
    public string BaseName => IsFolder ? FileName : Path.GetFileNameWithoutExtension(FileName);
    public IModSource OpenSource() => ModSources.Open(FullPath);

    /// <summary>Best human name: modinfo name for single-unit packages, otherwise the parsed archive name.</summary>
    public string DisplayName => Units.Count == 1 && Units[0].Info?.Name is { } n ? n : ParsedName.DisplayName;

    public string? Version => ParsedName.Version ?? (Units.Count == 1 ? Units[0].Info?.Version : null);
}

public static class ModLibrary
{
    public static IReadOnlyList<ModPackage> Scan(string libraryPath)
    {
        if (!Directory.Exists(libraryPath)) return [];
        var paths = Directory.EnumerateFiles(libraryPath).Where(ModSources.IsArchive)
            .Concat(Directory.EnumerateDirectories(libraryPath).Where(d => !Path.GetFileName(d).StartsWith('.')))
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

        var result = new List<ModPackage>();
        foreach (var path in paths) result.Add(Load(path));
        return result;
    }

    public static ModPackage Load(string path)
    {
        var name = Path.GetFileName(path);
        var isFolder = Directory.Exists(path);
        var parsed = ArchiveNameParser.Parse(name);
        var baseName = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        IReadOnlyList<ModUnit> units = [];
        string? error = null;
        try
        {
            var source = ModSources.Open(path);
            units = ModLayout.Analyze(source.ListFiles(), baseName, p => source.ReadFile(p, 1024 * 1024));
            if (units.Count == 0) error = "No installable files found";
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"Could not read {name}: {ex.Message}");
        }
        return new ModPackage
        {
            FullPath = path,
            FileName = name,
            Size = isFolder ? 0 : new FileInfo(path).Length,
            LastWriteUtc = isFolder ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path),
            ParsedName = parsed,
            Units = units,
            Error = error,
        };
    }
}
