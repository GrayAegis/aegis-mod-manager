using System.Web;

namespace DD2ModManager.Core.Nexus;

/// <summary>A "Mod Manager Download" link: nxm://dragonsdogma2/mods/1196/files/4321?key=...&amp;expires=...&amp;user_id=...</summary>
public sealed record NxmLink(string Game, int ModId, int FileId, string? Key, long? Expires)
{
    public static bool TryParse(string? text, out NxmLink link)
    {
        link = null!;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 4 || !segments[0].Equals("mods", StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("files", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(segments[1], out var modId) || !int.TryParse(segments[3], out var fileId))
            return false;

        var query = HttpUtility.ParseQueryString(uri.Query);
        link = new NxmLink(uri.Host, modId, fileId, query["key"],
            long.TryParse(query["expires"], out var expires) ? expires : null);
        return true;
    }
}
