using System.Text.Json.Serialization;

namespace DD2ModManager.Core.Nexus;

// Shapes of the Nexus Mods v1 REST API (snake_case on the wire).

public sealed class NexusUser
{
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public bool IsPremium { get; set; }
    public bool IsSupporter { get; set; }
    public string? ProfileUrl { get; set; }
}

public sealed class NexusMod
{
    public int ModId { get; set; }
    public string? DomainName { get; set; }
    public string? Name { get; set; }
    public string? Summary { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? PictureUrl { get; set; }
    public long UpdatedTimestamp { get; set; }
    public bool Available { get; set; }
    public string? Status { get; set; }
}

public sealed class NexusFile
{
    public int FileId { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public int CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? FileName { get; set; }
    public long UploadedTimestamp { get; set; }
    public long? SizeInBytes { get; set; }
    public long SizeKb { get; set; }
    public string? Description { get; set; }
    public string? ChangelogHtml { get; set; }
    public string? Md5 { get; set; }
    public bool IsPrimary { get; set; }

    [JsonIgnore]
    public DateTimeOffset Uploaded => DateTimeOffset.FromUnixTimeSeconds(UploadedTimestamp);

    [JsonIgnore]
    public bool IsActive => (CategoryName?.ToUpperInvariant() ?? CategoryId switch
    {
        4 => "OLD_VERSION",
        6 => "DELETED",
        7 => "ARCHIVED",
        _ => "MAIN",
    }) is not ("OLD_VERSION" or "ARCHIVED" or "REMOVED" or "DELETED");
}

public sealed class NexusFileUpdate
{
    public int OldFileId { get; set; }
    public int NewFileId { get; set; }
    public string? OldFileName { get; set; }
    public string? NewFileName { get; set; }
    public long UploadedTimestamp { get; set; }
}

public sealed class NexusModFiles
{
    public List<NexusFile> Files { get; set; } = [];
    public List<NexusFileUpdate> FileUpdates { get; set; } = [];
}

public sealed class NexusMd5Result
{
    public NexusMod Mod { get; set; } = new();
    public NexusFile FileDetails { get; set; } = new();
}

public sealed class NexusUpdateEntry
{
    public int ModId { get; set; }
    public long LatestFileUpdate { get; set; }
    public long LatestModActivity { get; set; }
}

public sealed class NexusDownloadLink
{
    [JsonPropertyName("URI")]
    public string Uri { get; set; } = "";
    public string? Name { get; set; }
    public string? ShortName { get; set; }
}

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes);
