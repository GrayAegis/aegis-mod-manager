using System.Text.Json.Serialization;

namespace DD2ModManager.Core.Nexus;

public sealed class CachedNexusMod
{
    public long FetchedUnix { get; set; }
    public NexusModFiles Files { get; set; } = new();
    public NexusMod? Info { get; set; }
}

/// <summary>Mod pages and file lists from Nexus, kept between runs so update checks stay well under the rate limit.</summary>
public sealed class NexusCache
{
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<int, CachedNexusMod> Mods { get; } = [];

    public NexusFile? FindFile(int? modId, int? fileId) =>
        modId is { } m && fileId is { } f && Mods.TryGetValue(m, out var mod) ? mod.Files.Files.FirstOrDefault(x => x.FileId == f) : null;
}
