using System.IO.Hashing;
using DD2ModManager.Core.Install;
using DD2ModManager.Core.Library;
using DD2ModManager.Core.State;
using DD2ModManager.Core.Util;

namespace DD2ModManager.Core.Fluffy;

public sealed record FluffyEntry(string Section, string? ModName, IReadOnlyList<string> Files);

public sealed record FluffyInstall(string? GamePath, IReadOnlyList<FluffyEntry> Entries);

public sealed record FluffyMatch(FluffyEntry Entry, ModPackage? Package, ModUnit? Unit, double Score);

/// <summary>Reads Fluffy Mod Manager's DD2 state so an existing setup can be taken over without reinstalling.</summary>
public static class FluffyImport
{
    public const string GameFolderName = "DragonsDogma2";

    public static string GameDir(string fluffyRoot) => Path.Combine(fluffyRoot, "Games", GameFolderName);
    public static string ModsDir(string fluffyRoot) => Path.Combine(GameDir(fluffyRoot), "Mods");
    public static string InstalledIni(string fluffyRoot) => Path.Combine(GameDir(fluffyRoot), "installed.ini");

    public static bool LooksLikeFluffy(string dir) => Directory.Exists(GameDir(dir)) &&
        (File.Exists(Path.Combine(dir, "Modmanager.exe")) || Directory.Exists(ModsDir(dir)));

    /// <summary>Probes drive roots and the user's common folders two levels deep for a Fluffy install with DD2 mods.</summary>
    public static string? Locate()
    {
        var starts = DriveInfo.GetDrives().Where(d => d is { DriveType: DriveType.Fixed, IsReady: true }).Select(d => d.RootDirectory.FullName)
            .Concat(new[] { "Downloads", "Desktop", "Documents" }.Select(f =>
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), f)));
        foreach (var start in starts)
        {
            foreach (var dir in SafeDirs(start))
            {
                if (LooksLikeFluffy(dir)) return dir;
                foreach (var sub in SafeDirs(dir))
                    if (LooksLikeFluffy(sub)) return sub;
            }
        }
        return null;

        static IEnumerable<string> SafeDirs(string path)
        {
            try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
            catch (Exception) { return []; }
        }
    }

    public static FluffyInstall ReadInstalled(string iniPath)
    {
        string? gamePath = null;
        var entries = new List<FluffyEntry>();
        string? section = null, modName = null;
        var files = new List<string>();

        void Flush()
        {
            if (section != null) entries.Add(new FluffyEntry(section, modName, files.ToList()));
            files.Clear(); modName = null;
        }

        foreach (var raw in File.ReadLines(iniPath))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                section = line[1..^1];
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..];
            if (section == null)
            {
                if (key.Equals("GameInstallPath", StringComparison.OrdinalIgnoreCase)) gamePath = value;
            }
            else if (key.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                if (PathUtil.NormalizeRelative(value) is { } rel) files.Add(rel);
            }
            else if (key.Equals("ModName", StringComparison.OrdinalIgnoreCase))
            {
                modName = value;
            }
        }
        Flush();
        return new FluffyInstall(gamePath, entries);
    }

    /// <summary>
    /// Pairs each Fluffy entry with the library package it came from. When several versions share a unit name,
    /// the one whose files match what's actually on disk wins.
    /// </summary>
    public static IReadOnlyList<FluffyMatch> Match(FluffyInstall install, IReadOnlyList<ModPackage> library, string gameDir)
    {
        var results = new List<FluffyMatch>();
        foreach (var entry in install.Entries)
        {
            var candidates = library
                .SelectMany(p => p.Units.Select(u => (Package: p, Unit: u)))
                .Where(c => PathUtil.Comparer.Equals(c.Unit.Id, entry.Section) ||
                            (PathUtil.Comparer.Equals(c.Package.BaseName, entry.Section) && c.Package.Units.Count == 1))
                .Select(c => (c.Package, c.Unit, Score: Score(c.Unit, entry, gameDir)))
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Package.LastWriteUtc)
                .ToList();

            results.Add(candidates.Count == 0
                ? new FluffyMatch(entry, null, null, 0)
                : new FluffyMatch(entry, candidates[0].Package, candidates[0].Unit, candidates[0].Score));
        }
        return results;
    }

    public static List<InstalledMod> ToInstalledMods(IEnumerable<FluffyMatch> matches) =>
        matches.Select(m => new InstalledMod
        {
            UnitId = m.Unit?.Id ?? m.Entry.Section,
            PackageFileName = m.Package?.FileName,
            DisplayName = m.Unit != null && m.Package != null ? m.Unit.DisplayNameIn(m.Package) : m.Entry.ModName ?? m.Entry.Section,
            Version = m.Unit?.Info?.Version ?? m.Package?.Version,
            Origin = "fluffy",
            // Fluffy's own record is the truth about what's on disk; keep it rather than our re-derived list.
            Files = m.Entry.Files.Select(f => new InstalledFile { Path = f }).ToList(),
        }).ToList();

    /// <summary>Fraction of a unit's files whose installed copy matches the archive entry (size, then CRC).</summary>
    private static double Score(ModUnit unit, FluffyEntry entry, string gameDir)
    {
        if (unit.Files.Count == 0) return 0;
        var matched = 0;
        foreach (var file in unit.Files)
        {
            var installedPath = file.IsPak
                ? entry.Files.FirstOrDefault(f => f.StartsWith(ModLayout.PakFolder + "/", StringComparison.OrdinalIgnoreCase) &&
                                                  PathUtil.Comparer.Equals(StripPakPrefix(PathUtil.FileName(f)), PathUtil.FileName(file.Target)))
                : entry.Files.FirstOrDefault(f => PathUtil.Comparer.Equals(f, file.Target));
            if (installedPath == null) continue;
            var full = Path.Combine(gameDir, installedPath);
            if (!File.Exists(full)) continue;
            var info = new FileInfo(full);
            if (info.Length != file.Size) continue;
            if (!file.IsPak && file.Crc is { } crc && info.Length < 64 * 1024 * 1024 && Crc32Of(full) != crc) continue;
            matched++;
        }
        return (double)matched / unit.Files.Count;
    }

    public static string StripPakPrefix(string fileName) =>
        Installer.PakSlot(fileName) != null ? fileName[6..] : fileName;

    private static uint Crc32Of(string path)
    {
        using var stream = File.OpenRead(path);
        var crc = new Crc32();
        crc.Append(stream);
        return crc.GetCurrentHashAsUInt32();
    }
}
