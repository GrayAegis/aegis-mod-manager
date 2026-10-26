using System.Globalization;
using System.Text.RegularExpressions;

namespace DD2ModManager.Core.Library;

public sealed record ParsedArchiveName(string DisplayName, int? NexusModId, string? Version, DateTimeOffset? UploadedAt);

/// <summary>
/// Pulls the Nexus mod ID and version out of downloaded archive names. Two schemes are in the wild:
/// "Bestiary 1196 2.8.11 2026-10-25T15-30Z lEHTkEUYS.zip" and the classic "Bestiary-1196-2-8-11-1729868400.zip".
/// </summary>
public static partial class ArchiveNameParser
{
    [GeneratedRegex(@"^(?<name>.+) (?<id>\d+) (?<ver>\S+) (?<ts>\d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z) (?<uid>[A-Za-z0-9]{4,})$")]
    private static partial Regex SpacedFormat();

    [GeneratedRegex(@"^(?<name>.+?)-(?<id>\d+)-(?<ver>.+)-(?<ts>\d{9,11})$")]
    private static partial Regex DashedFormat();

    public static ParsedArchiveName Parse(string fileName)
    {
        var stem = Directory.Exists(fileName) ? Path.GetFileName(fileName) : Path.GetFileNameWithoutExtension(fileName);

        var m = SpacedFormat().Match(stem);
        if (m.Success)
        {
            DateTimeOffset? ts = DateTimeOffset.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd'T'HH-mm'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            return new ParsedArchiveName(m.Groups["name"].Value.Trim(), int.Parse(m.Groups["id"].Value), m.Groups["ver"].Value, ts);
        }

        m = DashedFormat().Match(stem);
        if (m.Success)
        {
            var ts = DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups["ts"].Value));
            return new ParsedArchiveName(m.Groups["name"].Value.Trim(), int.Parse(m.Groups["id"].Value),
                m.Groups["ver"].Value.Replace('-', '.'), ts);
        }

        return new ParsedArchiveName(stem, null, null, null);
    }

    /// <summary>Lowercase alphanumerics only, with version-like tokens removed. Used for fuzzy file matching.</summary>
    public static string Normalize(string name)
    {
        var withoutVersions = Regex.Replace(name, @"v?\d+(?:[._-]\d+)+[a-z]?|\bv\d+\b", " ", RegexOptions.IgnoreCase);
        return new string(withoutVersions.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
