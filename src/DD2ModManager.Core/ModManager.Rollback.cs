using DD2ModManager.Core.Library;
using DD2ModManager.Core.State;
using DD2ModManager.Core.Util;

namespace DD2ModManager.Core;

// Keeping replaced versions and undoing updates.
public sealed partial class ModManager
{
    /// <summary>Kept versions of this mod, newest first.</summary>
    public IReadOnlyList<PreviousVersion> PreviousVersionsOf(PackageView view)
    {
        if (view.Package == null) return [];
        var key = FamilyKey(view.Package);
        return State.PreviousVersions
            .Where(p => p.FamilyKey == key && (File.Exists(StoredPathOf(p)) || Directory.Exists(StoredPathOf(p))))
            .OrderByDescending(p => p.UploadedAt)
            .ToList();
    }

    private static string StoredPathOf(PreviousVersion previous) => PathUtil.Combine(AppPaths.Previous, previous.StoredPath);

    /// <summary>
    /// Moves an archive an update replaced out of the library into the kept-versions folder, then trims that mod's
    /// kept versions to <see cref="AppSettings.KeepPreviousVersions"/> (extras go to the Recycle Bin).
    /// </summary>
    private void RetireArchive(ModPackage old, ModPackage replacedBy)
    {
        if (!File.Exists(old.FullPath) && !Directory.Exists(old.FullPath)) return;
        var key = FamilyKey(old);
        if (Settings.KeepPreviousVersions <= 0)
        {
            if (RecycleFile?.Invoke(old.FullPath) == true)
            {
                State.Nexus.Remove(old.FileName);
                Log.Info($"Moved replaced archive {old.FileName} to the Recycle Bin");
            }
            return;
        }

        var stored = $"{Guid.NewGuid():N}/{old.FileName}";
        var destination = PathUtil.Combine(AppPaths.Previous, stored);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (old.IsFolder) Directory.Move(old.FullPath, destination);
        else File.Move(old.FullPath, destination);

        State.PreviousVersions.Add(new PreviousVersion
        {
            FileName = old.FileName,
            StoredPath = stored,
            FamilyKey = key,
            Version = old.Version,
            UploadedAt = UploadedAt(old),
            ReplacedBy = replacedBy.FileName,
            Link = State.Nexus.GetValueOrDefault(old.FileName),
        });
        State.Nexus.Remove(old.FileName);
        Log.Info($"Kept {old.FileName} for rollback");
        TrimPreviousVersions(key);
    }

    private void TrimPreviousVersions(string familyKey)
    {
        var extras = State.PreviousVersions
            .Where(p => p.FamilyKey == familyKey)
            .OrderByDescending(p => p.UploadedAt)
            .Skip(Math.Max(0, Settings.KeepPreviousVersions))
            .ToList();
        foreach (var extra in extras)
        {
            var path = StoredPathOf(extra);
            var exists = File.Exists(path) || Directory.Exists(path);
            if (exists && RecycleFile?.Invoke(path) != true) continue; // can't recycle: keep it rather than delete for good
            State.PreviousVersions.Remove(extra);
            PathUtil.PruneEmptyDirectories(Path.GetDirectoryName(path)!, AppPaths.Previous);
            Log.Info($"Moved {extra.FileName} to the Recycle Bin (more than {Settings.KeepPreviousVersions} kept versions)");
        }
    }

    /// <summary>
    /// Undoes an update: puts the kept archive back in the library, swaps every installed version of the mod to it
    /// (keeping pak load-order slots), and pauses updates for it so the next check doesn't reinstall the newer one.
    /// The newer archive stays in the library, so unticking "Ignore updates" offers it again.
    /// </summary>
    public ModPackage Rollback(PackageView view, PreviousVersion previous, IProgress<OperationProgress>? progress = null)
    {
        var stored = StoredPathOf(previous);
        if (!File.Exists(stored) && !Directory.Exists(stored))
            throw new InvalidOperationException($"The kept copy of {previous.FileName} is missing.");

        var family = view.Family.Count > 0 ? view.Family : view.Package != null ? [view.Package] : [];
        var sources = family.Where(p => State.InstalledFrom(p.FileName).Any()).ToList();
        if (Game?.IsRunning == true &&
            NeedsGameClosed(sources.SelectMany(p => State.InstalledFrom(p.FileName)).SelectMany(m => m.Files.Select(f => f.Path))))
            throw new InvalidOperationException("Dragon's Dogma 2 is running. Close it and try again.");

        progress?.Report(new OperationProgress($"Restoring {previous.FileName}…"));
        Directory.CreateDirectory(LibraryPath);
        var destination = UniquePath(Path.Combine(LibraryPath, previous.FileName));
        if (Directory.Exists(stored)) Directory.Move(stored, destination);
        else File.Move(stored, destination);
        PathUtil.PruneEmptyDirectories(Path.GetDirectoryName(stored)!, AppPaths.Previous);
        State.PreviousVersions.Remove(previous);

        var name = Path.GetFileName(destination);
        var link = previous.Link ?? new NexusLink();
        if (File.Exists(destination))
        {
            var info = new FileInfo(destination);
            (link.Size, link.LastWriteUtc) = (info.Length, info.LastWriteTimeUtc);
        }
        else
        {
            (link.Size, link.LastWriteUtc) = (0, Directory.GetLastWriteTimeUtc(destination));
        }
        link.IgnoreUpdates = true;
        State.Nexus[name] = link;
        SaveState();
        Refresh();

        var restored = Library.First(p => PathUtil.Comparer.Equals(p.FileName, name));
        foreach (var source in sources) SwapInstalled(source, restored, progress);
        SaveState();
        Refresh();
        Log.Info($"Rolled back to {name}; updates for it are paused");
        return restored;
    }
}
