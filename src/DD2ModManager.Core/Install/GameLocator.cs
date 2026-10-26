using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DD2ModManager.Core.Install;

/// <summary>Finds the Dragon's Dogma 2 folder through Steam's library list.</summary>
public static partial class GameLocator
{
    private const string SteamFolderName = "Dragons Dogma 2";

    [GeneratedRegex("\"path\"\\s+\"(?<p>[^\"]+)\"")]
    private static partial Regex LibraryPath();

    public static string? Find()
    {
        foreach (var library in SteamLibraries())
        {
            var candidate = Path.Combine(library, "steamapps", "common", SteamFolderName);
            if (new GameFolder(candidate).IsValid) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string user) roots.Add(user);
            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string machine) roots.Add(machine);
        }
        roots.Add(@"C:\Program Files (x86)\Steam");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var full = Path.GetFullPath(root.Replace('/', '\\'));
            if (seen.Add(full)) yield return full;
            var vdf = Path.Combine(full, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in LibraryPath().Matches(File.ReadAllText(vdf)))
            {
                var lib = Path.GetFullPath(m.Groups["p"].Value.Replace(@"\\", @"\"));
                if (seen.Add(lib)) yield return lib;
            }
        }
    }
}
