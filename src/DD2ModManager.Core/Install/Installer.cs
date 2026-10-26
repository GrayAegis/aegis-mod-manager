using System.Text.RegularExpressions;
using DD2ModManager.Core.Library;
using DD2ModManager.Core.State;
using DD2ModManager.Core.Util;

namespace DD2ModManager.Core.Install;

public sealed record InstallConflict(string Path, string OwnerUnitId, string OwnerName);

public sealed class InstallConflictException(IReadOnlyList<InstallConflict> conflicts)
    : Exception($"{conflicts.Count} file(s) already belong to other installed mods")
{
    public IReadOnlyList<InstallConflict> Conflicts { get; } = conflicts;
}

/// <summary>
/// Copies mod files into the game folder and tracks them, the way Fluffy does: original game files are moved to a
/// backup folder and restored on uninstall, and paks go to pak_mods with an "x0000-" prefix that sets load order.
/// </summary>
public sealed partial class Installer(string gameDir, string backupDir, AppState state, Action saveState)
{
    [GeneratedRegex(@"^x(\d{4})-", RegexOptions.IgnoreCase)]
    private static partial Regex PakPrefix();

    private readonly string _gameDir = Path.GetFullPath(gameDir);
    private readonly string _backupDir = Path.GetFullPath(backupDir);

    private HashSet<string> ProtectedDirs => new(PathUtil.Comparer)
    {
        _gameDir,
        Path.Combine(_gameDir, ModLayout.PakFolder),
        Path.Combine(_gameDir, "reframework"),
        Path.Combine(_gameDir, "reframework", "autorun"),
        Path.Combine(_gameDir, "reframework", "plugins"),
        Path.Combine(_gameDir, "reframework", "data"),
    };

    public static int? PakSlot(string relativePath)
    {
        var m = PakPrefix().Match(PathUtil.FileName(relativePath));
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    public static IReadOnlyList<int> PakSlotsOf(InstalledMod mod) =>
        mod.Files.Select(f => PakSlot(f.Path)).OfType<int>().Order().ToList();

    public int NextPakSlot()
    {
        var used = state.Installed.SelectMany(PakSlotsOf).ToList();
        var pakDir = Path.Combine(_gameDir, ModLayout.PakFolder);
        if (Directory.Exists(pakDir))
            used.AddRange(Directory.EnumerateFiles(pakDir, "*.pak").Select(f => PakSlot(Path.GetFileName(f))).OfType<int>());
        return used.Count == 0 ? 0 : used.Max() + 1;
    }

    /// <summary>Resolves final game-relative targets, assigning pak slots (reusing <paramref name="pakSlots"/> first).</summary>
    public IReadOnlyList<(FileMapping Mapping, string Target)> PlanTargets(ModUnit unit, IReadOnlyList<int>? pakSlots = null)
    {
        var next = NextPakSlot();
        if (pakSlots is { Count: > 0 }) next = Math.Max(next, pakSlots.Max() + 1);
        var cursor = 0;
        var result = new List<(FileMapping, string)>();
        foreach (var m in unit.Files)
        {
            if (!m.IsPak) { result.Add((m, m.Target)); continue; }
            var slot = pakSlots != null && cursor < pakSlots.Count ? pakSlots[cursor] : next++;
            cursor++;
            result.Add((m, $"{ModLayout.PakFolder}/x{slot:D4}-{PathUtil.FileName(m.Target)}"));
        }
        return result;
    }

    /// <summary>
    /// Files other installed mods already own with different content. Identical copies (shared fonts and script
    /// libraries several mods bundle) aren't conflicts. Uses archive sizes/CRCs, so it works before extraction.
    /// </summary>
    public IReadOnlyList<InstallConflict> FindConflicts(ModUnit unit, IReadOnlyList<int>? pakSlots = null) =>
        PlanTargets(unit, pakSlots)
            .Select(t => (t.Mapping, t.Target, Owner: state.OwnersOf(t.Target).FirstOrDefault()))
            .Where(t => t.Owner != null && !MatchesDisk(t.Mapping, t.Target))
            .Select(t => new InstallConflict(t.Target, t.Owner!.UnitId, t.Owner.DisplayName))
            .ToList();

    private bool MatchesDisk(FileMapping mapping, string target)
    {
        var path = PathUtil.Combine(_gameDir, target);
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length != mapping.Size) return false;
        if (mapping.Crc is not { } crc) return true;
        using var stream = File.OpenRead(path);
        var hash = new System.IO.Hashing.Crc32();
        hash.Append(stream);
        return hash.GetCurrentHashAsUInt32() == crc;
    }

    private static bool SameContent(string a, string b)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (!infoA.Exists || !infoB.Exists || infoA.Length != infoB.Length) return false;
        using var streamA = infoA.OpenRead();
        using var streamB = infoB.OpenRead();
        var bufferA = new byte[65536];
        var bufferB = new byte[65536];
        int read;
        while ((read = streamA.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false)) > 0)
        {
            streamB.ReadExactly(bufferB, 0, read);
            if (!bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read))) return false;
        }
        return true;
    }

    /// <summary>
    /// Installs one unit from an already-extracted package. All-or-nothing: on failure every change is undone.
    /// Files other mods also ship are shared: identical copies are just recorded, and with
    /// <paramref name="overwriteConflicts"/> differing ones are replaced. Either way every mod keeps listing the
    /// file, so it stays on disk until the last of them is uninstalled.
    /// </summary>
    public InstalledMod Install(ModPackage package, ModUnit unit, string extractedDir, bool overwriteConflicts = false,
        IReadOnlyList<int>? pakSlots = null)
    {
        if (state.Installed.Any(m => PathUtil.Comparer.Equals(m.UnitId, unit.Id) &&
                                     PathUtil.Comparer.Equals(m.PackageFileName, package.FileName)))
            throw new InvalidOperationException($"{unit.DisplayName} is already installed");

        var plan = PlanTargets(unit, pakSlots);
        var conflicts = plan
            .Select(p => (p.Target, Owner: state.OwnersOf(p.Target).FirstOrDefault(),
                Same: SameContent(PathUtil.Combine(extractedDir, p.Mapping.Source), PathUtil.Combine(_gameDir, p.Target))))
            .Where(c => c.Owner != null && !c.Same)
            .Select(c => new InstallConflict(c.Target, c.Owner!.UnitId, c.Owner.DisplayName))
            .ToList();
        if (conflicts.Count > 0 && !overwriteConflicts) throw new InstallConflictException(conflicts);

        var mod = new InstalledMod
        {
            UnitId = unit.Id,
            PackageFileName = package.FileName,
            DisplayName = unit.DisplayNameIn(package),
            Version = unit.Info?.Version ?? package.Version,
        };

        var rollbackDir = Path.Combine(Path.GetTempPath(), "DD2ModManager", "rollback", Guid.NewGuid().ToString("N"));
        var undo = new Stack<Action>();
        try
        {
            foreach (var (mapping, target) in plan)
            {
                var source = PathUtil.Combine(extractedDir, mapping.Source);
                var destination = PathUtil.Combine(_gameDir, target);
                var hasBackup = false;

                var owners = state.OwnersOf(target).ToList();
                if (owners.Count > 0)
                {
                    // Shared with other mods: never take the file away from them, and the original-file backup stays theirs.
                    if (SameContent(source, destination))
                    {
                        mod.Files.Add(new InstalledFile { Path = target });
                        continue;
                    }
                    Log.Warn($"{mod.DisplayName} replaces {target}, which {string.Join(", ", owners.Select(o => o.DisplayName))} also use");
                }

                if (File.Exists(destination))
                {
                    var backup = PathUtil.Combine(_backupDir, target);
                    if (owners.Count == 0 && !File.Exists(backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Move(destination, backup);
                        undo.Push(() => File.Move(backup, destination, overwrite: true));
                        hasBackup = true;
                    }
                    else
                    {
                        // Keep what we overwrite so a failed install can put it back.
                        var saved = PathUtil.Combine(rollbackDir, target);
                        Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                        File.Move(destination, saved);
                        undo.Push(() => File.Move(saved, destination, overwrite: true));
                        hasBackup |= owners.Count == 0;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
                undo.Push(() => File.Delete(destination));
                mod.Files.Add(new InstalledFile { Path = target, HasBackup = hasBackup });
            }

            state.Installed.Add(mod);
            saveState();
            Log.Info($"Installed {mod.DisplayName} ({mod.Files.Count} files)");
            return mod;
        }
        catch
        {
            while (undo.Count > 0)
            {
                try { undo.Pop()(); }
                catch (Exception ex) { Log.Error($"Rollback step failed: {ex.Message}"); }
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(rollbackDir)) Directory.Delete(rollbackDir, recursive: true);
        }
    }

    /// <summary>
    /// Removes a mod's files and restores any originals. Files other installed mods also list stay on disk.
    /// State is saved even if a file can't be removed.
    /// </summary>
    public void Uninstall(InstalledMod mod)
    {
        var keep = ProtectedDirs;
        try
        {
            foreach (var file in mod.Files.ToList())
            {
                var stillUsed = state.OwnersOf(file.Path).Where(o => o != mod).ToList();
                if (stillUsed.Count > 0)
                {
                    if (file.HasBackup)
                        stillUsed[0].Files.First(f => PathUtil.Comparer.Equals(f.Path, file.Path)).HasBackup = true;
                    mod.Files.Remove(file);
                    continue;
                }

                var destination = PathUtil.Combine(_gameDir, file.Path);
                if (File.Exists(destination)) File.Delete(destination);
                if (file.HasBackup)
                {
                    var backup = PathUtil.Combine(_backupDir, file.Path);
                    if (File.Exists(backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Move(backup, destination);
                        PathUtil.PruneEmptyDirectories(Path.GetDirectoryName(backup)!, _backupDir);
                    }
                }
                mod.Files.Remove(file);
                PathUtil.PruneEmptyDirectories(Path.GetDirectoryName(destination)!, _gameDir, keep);
            }
            state.Installed.Remove(mod);
            Log.Info($"Uninstalled {mod.DisplayName}");
        }
        finally
        {
            saveState();
        }
    }
}

public static class ModUnitNaming
{
    /// <summary>Unit name for display; multi-unit packages get the package name as context.</summary>
    public static string DisplayNameIn(this ModUnit unit, ModPackage package) =>
        package.Units.Count > 1 ? $"{unit.DisplayName} ({package.ParsedName.DisplayName})" : package.DisplayName;
}
