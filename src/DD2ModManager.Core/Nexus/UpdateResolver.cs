using DD2ModManager.Core.Library;

namespace DD2ModManager.Core.Nexus;

public enum UpdateStatus
{
    Unknown,
    UpToDate,
    /// <summary>The author linked a newer file, or demoted ours and uploaded one with the same name.</summary>
    UpdateAvailable,
    /// <summary>A newer file probably replaces ours, but the author didn't say so. Never auto-installed.</summary>
    ProbableUpdate,
    /// <summary>Our file is gone from the mod page and nothing obviously replaces it.</summary>
    NoLongerListed,
}

public sealed record UpdateResult(UpdateStatus Status, NexusFile? Current, NexusFile? Latest, string Reason)
{
    public bool HasUpdate => Status is UpdateStatus.UpdateAvailable or UpdateStatus.ProbableUpdate;
}

public static class UpdateResolver
{
    public static UpdateResult Resolve(int fileId, NexusModFiles mod)
    {
        var byId = mod.Files.GroupBy(f => f.FileId).ToDictionary(g => g.Key, g => g.First());
        byId.TryGetValue(fileId, out var current);

        // 1. Follow the author's own "this file replaces that file" chain.
        var chainEnd = FollowChain(fileId, mod.FileUpdates);
        if (chainEnd != fileId && byId.TryGetValue(chainEnd, out var linked) && linked.IsActive)
            return new UpdateResult(UpdateStatus.UpdateAvailable, current, linked, "Marked by the author as the replacement");

        // 2. A newer active file with the same name (versions stripped) in the same category.
        var key = current != null ? NameKey(current) : null;
        var sameName = key == null ? null : mod.Files
            .Where(f => f.FileId != fileId && f.IsActive && f.UploadedTimestamp > current!.UploadedTimestamp && NameKey(f) == key)
            .OrderByDescending(f => f.UploadedTimestamp)
            .FirstOrDefault();

        if (current is { IsActive: true })
        {
            if (sameName != null && Category(sameName) == Category(current))
                return new UpdateResult(UpdateStatus.UpdateAvailable, current, sameName, "Newer upload of the same file");
            return new UpdateResult(UpdateStatus.UpToDate, current, current, "Latest file");
        }

        if (sameName != null)
            return new UpdateResult(UpdateStatus.UpdateAvailable, current, sameName, "Old version replaced by a newer file of the same name");

        // 3. Our file was retired with no obvious successor: suggest the newest main file, but only as a guess.
        var main = mod.Files
            .Where(f => f.IsActive && Category(f) == "MAIN" && (current == null || f.UploadedTimestamp > current.UploadedTimestamp))
            .OrderByDescending(f => f.UploadedTimestamp)
            .ToList();
        if (main.Count == 1)
            return new UpdateResult(UpdateStatus.ProbableUpdate, current, main[0], "Our file was retired; this is the newest main file");

        return new UpdateResult(UpdateStatus.NoLongerListed, current, null, "File is no longer listed on the mod page");
    }

    /// <summary>File IDs reachable from <paramref name="fileId"/> through the update chain, oldest to newest.</summary>
    public static IReadOnlyList<int> Successors(int fileId, IEnumerable<NexusFileUpdate> updates)
    {
        var list = updates.ToList();
        var result = new List<int>();
        var seen = new HashSet<int> { fileId };
        var currentId = fileId;
        while (true)
        {
            var next = list.Where(u => u.OldFileId == currentId).OrderByDescending(u => u.UploadedTimestamp).FirstOrDefault();
            if (next == null || !seen.Add(next.NewFileId)) return result;
            result.Add(next.NewFileId);
            currentId = next.NewFileId;
        }
    }

    private static int FollowChain(int fileId, IEnumerable<NexusFileUpdate> updates) =>
        Successors(fileId, updates) is { Count: > 0 } s ? s[^1] : fileId;

    /// <summary>
    /// Finds the Nexus file for a local package when MD5 lookup fails: by upload time encoded in the name,
    /// then by exact file name, then by a unique version match.
    /// </summary>
    public static NexusFile? IdentifyByName(ModPackage package, NexusModFiles mod)
    {
        var parsed = package.ParsedName;
        if (parsed.UploadedAt is { } at)
        {
            var byTime = mod.Files.Where(f => Math.Abs(f.UploadedTimestamp - at.ToUnixTimeSeconds()) <= 120).ToList();
            if (byTime.Count == 1) return byTime[0];
        }
        var byName = mod.Files.FirstOrDefault(f => string.Equals(f.FileName, package.FileName, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;
        if (parsed.Version != null)
        {
            var nameKey = ArchiveNameParser.Normalize(parsed.DisplayName);
            var byVersion = mod.Files.Where(f => VersionEquals(f.Version, parsed.Version)).ToList();
            if (byVersion.Count > 1) byVersion = byVersion.Where(f => NameKey(f) == nameKey).ToList();
            if (byVersion.Count == 1) return byVersion[0];
        }
        return null;
    }

    private static bool VersionEquals(string? a, string? b) =>
        a != null && b != null && string.Equals(a.Trim().TrimStart('v', 'V'), b.Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

    private static string NameKey(NexusFile f) => ArchiveNameParser.Normalize(f.Name ?? f.FileName ?? "");

    private static string Category(NexusFile f) => (f.CategoryName ?? (f.CategoryId == 1 ? "MAIN" : f.CategoryId.ToString())).ToUpperInvariant();
}
