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

    public IReadOnlyList<InstallConflict> FindConflicts(ModUnit unit, IReadOnlyList<int>? pakSlots = null) =>
        PlanTargets(unit, pakSlots)
            .Select(t => (t.Target, Owner: state.FindOwner(t.Target)))
            .Where(t => t.Owner != null)
            .Select(t => new InstallConflict(t.Target, t.Owner!.UnitId, t.Owner.DisplayName))
            .ToList();

    /// <summary>Installs one unit from an already-extracted package. All-or-nothing: on failure every change is undone.</summary>
    public InstalledMod Install(ModPackage package, ModUnit unit, string extractedDir, bool overwriteConflicts = false,
        IReadOnlyList<int>? pakSlots = null)
    {
        if (state.Installed.Any(m => PathUtil.Comparer.Equals(m.UnitId, unit.Id) &&
                                     PathUtil.Comparer.Equals(m.PackageFileName, package.FileName)))
            throw new InvalidOperationException($"{unit.DisplayName} is already installed");

        var plan = PlanTargets(unit, pakSlots);
        var conflicts = FindConflicts(unit, pakSlots);
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

                var owner = state.FindOwner(target);
                if (owner != null)
                {
                    var taken = owner.Files.First(f => PathUtil.Comparer.Equals(f.Path, target));
                    owner.Files.Remove(taken);
                    hasBackup = taken.HasBackup;
                    undo.Push(() => owner.Files.Add(taken));
                    Log.Warn($"{mod.DisplayName} overwrites {target} from {owner.DisplayName}");
                }

                if (File.Exists(destination))
                {
                    var backup = PathUtil.Combine(_backupDir, target);
                    if (owner == null && !File.Exists(backup))
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
                        hasBackup |= owner == null;
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

    /// <summary>Removes a mod's files and restores any originals. State is saved even if a file can't be removed.</summary>
    public void Uninstall(InstalledMod mod)
    {
        var keep = ProtectedDirs;
        try
        {
            foreach (var file in mod.Files.ToList())
            {
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
