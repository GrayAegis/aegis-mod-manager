using System.Net;
using System.Text.RegularExpressions;

namespace DD2ModManager.Core.Nexus;

public sealed record ChangelogEntry(string? Version, DateTimeOffset Uploaded, string Text);

public static partial class Changelog
{
    private const int MaxEntries = 8;

    [GeneratedRegex(@"<br\s*/?>|</p>|</div>|</li>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    /// <summary>Nexus changelogs are light HTML; the details panel shows plain text.</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = html.Replace("\r\n", "\n");
        text = LineBreaks().Replace(text, "\n");
        text = ListItems().Replace(text, "• ");
        text = WebUtility.HtmlDecode(Tags().Replace(text, ""));
        text = string.Join('\n', text.Split('\n').Select(l => l.Trim()));
        return BlankRuns().Replace(text, "\n\n").Trim();
    }

    public static ChangelogEntry? Of(NexusFile file)
    {
        var text = ToPlainText(file.ChangelogHtml);
        return text.Length == 0 ? null : new ChangelogEntry(file.Version, file.Uploaded, text);
    }

    /// <summary>
    /// Changelogs for every version after <paramref name="currentFileId"/> up to and including the target, newest first,
    /// so skipping several versions still shows everything that changed.
    /// </summary>
    public static IReadOnlyList<ChangelogEntry> Between(NexusModFiles mod, int? currentFileId, int targetFileId)
    {
        var byId = mod.Files.GroupBy(f => f.FileId).ToDictionary(g => g.Key, g => g.First());
        if (!byId.TryGetValue(targetFileId, out var target)) return [];
        var current = currentFileId is { } c ? byId.GetValueOrDefault(c) : null;

        IEnumerable<NexusFile> span;
        var chain = currentFileId is { } id ? UpdateResolver.Successors(id, mod.FileUpdates) : [];
        var targetIndex = chain.ToList().IndexOf(targetFileId);
        if (targetIndex >= 0)
        {
            span = chain.Take(targetIndex + 1).Select(byId.GetValueOrDefault).OfType<NexusFile>();
        }
        else if (current != null)
        {
            var key = UpdateResolver.NameKey(target);
            span = mod.Files.Where(f => UpdateResolver.NameKey(f) == key &&
                                        f.UploadedTimestamp > current.UploadedTimestamp &&
                                        f.UploadedTimestamp <= target.UploadedTimestamp);
        }
        else
        {
            span = [target];
        }

        return span.Append(target)
            .DistinctBy(f => f.FileId)
            .OrderByDescending(f => f.UploadedTimestamp)
            .Select(Of)
            .OfType<ChangelogEntry>()
            .DistinctBy(e => e.Text)
            .Take(MaxEntries)
            .ToList();
    }
}
