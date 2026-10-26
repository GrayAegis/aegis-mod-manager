using System.Text.Json;
using System.Text.Json.Serialization;
using DD2ModManager.Core.Util;

namespace DD2ModManager.Core.State;

public sealed class InstalledFile
{
    /// <summary>Game-relative path with forward slashes.</summary>
    public required string Path { get; set; }
    /// <summary>True when an original game file was moved to the backup folder before this one was written.</summary>
    public bool HasBackup { get; set; }
}

public sealed class InstalledMod
{
    public required string UnitId { get; set; }
    /// <summary>Library file the mod came from; null when imported from Fluffy without a matching archive.</summary>
    public string? PackageFileName { get; set; }
    public required string DisplayName { get; set; }
    public string? Version { get; set; }
    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
    public string Origin { get; set; } = "manager";
    public List<InstalledFile> Files { get; set; } = [];
}

/// <summary>What we know about a library package on Nexus.</summary>
public sealed class NexusLink
{
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string? Md5 { get; set; }
    public int? ModId { get; set; }
    public int? FileId { get; set; }
    /// <summary>"md5", "name" or "nxm": how the file was identified.</summary>
    public string? MatchedBy { get; set; }
    public bool Md5Searched { get; set; }
    public bool IgnoreUpdates { get; set; }
}

/// <summary>An archive an update replaced, kept in <see cref="AppPaths.Previous"/> so the update can be undone.</summary>
public sealed class PreviousVersion
{
    public required string FileName { get; set; }
    /// <summary>Path under <see cref="AppPaths.Previous"/>.</summary>
    public required string StoredPath { get; set; }
    public required string FamilyKey { get; set; }
    public string? Version { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public string? ReplacedBy { get; set; }
    public DateTime ReplacedAtUtc { get; set; } = DateTime.UtcNow;
    public NexusLink? Link { get; set; }
}

/// <summary>The REFramework build this app installed, recognised later by the DLL's hash.</summary>
public sealed class REFrameworkRecord
{
    public required string Commit { get; set; }
    public int? Number { get; set; }
    public required string DllSha256 { get; set; }
    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AppState
{
    public List<InstalledMod> Installed { get; set; } = [];
    /// <summary>Keyed by package file name.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, NexusLink> Nexus { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FluffyImported { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    public List<PreviousVersion> PreviousVersions { get; set; } = [];
    public REFrameworkRecord? REFramework { get; set; }

    /// <summary>Installed mods that list this game file. Several mods can share one (bundled fonts, script libraries).</summary>
    public IEnumerable<InstalledMod> OwnersOf(string relativePath) =>
        Installed.Where(m => m.Files.Any(f => PathUtil.Comparer.Equals(f.Path, relativePath)));

    public IEnumerable<InstalledMod> InstalledFrom(string packageFileName) =>
        Installed.Where(m => PathUtil.Comparer.Equals(m.PackageFileName, packageFileName));
}

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (JsonException ex)
        {
            var broken = path + ".broken";
            File.Copy(path, broken, overwrite: true);
            Log.Error($"{Path.GetFileName(path)} was unreadable ({ex.Message}); saved a copy as {Path.GetFileName(broken)}");
            return new T();
        }
    }

    /// <summary>Writes via a temp file so a crash mid-save never leaves a truncated state file.</summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}
