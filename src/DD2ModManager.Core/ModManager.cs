using System.Security.Cryptography;
using DD2ModManager.Core.Fluffy;
using DD2ModManager.Core.Install;
using DD2ModManager.Core.Library;
using DD2ModManager.Core.Nexus;
using DD2ModManager.Core.State;
using DD2ModManager.Core.Util;

namespace DD2ModManager.Core;

public enum InstallState { NotInstalled, Partial, Installed }

public enum PackageStatus { Unchecked, UpToDate, UpdateAvailable, UpdateDownloaded, ProbableUpdate, NotOnNexus, NoLongerListed, Ignored, ArchiveMissing, Unreadable }

/// <summary>Everything the UI needs to show one row: a package (or an installed mod whose archive is gone) and its status.</summary>
public sealed class PackageView
{
    public ModPackage? Package { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<InstalledMod> Installed { get; init; } = [];
    public InstallState InstallState { get; init; }
    /// <summary>Every library archive of this mod, newest first.</summary>
    public IReadOnlyList<ModPackage> Family { get; init; } = [];
    public IReadOnlyList<ModPackage> OtherVersions { get; init; } = [];
    public ModPackage? NewerLocal { get; init; }
    public NexusLink? Link { get; init; }
    public NexusMod? NexusInfo { get; init; }
    public UpdateResult? Update { get; init; }
    public PackageStatus Status { get; init; }
    public string StatusText { get; init; } = "";
    public string? LatestVersion { get; init; }
    public int? ModId => Link?.ModId ?? Package?.ParsedName.NexusModId;
    public bool CanUpdate => Status is PackageStatus.UpdateAvailable or PackageStatus.UpdateDownloaded or PackageStatus.ProbableUpdate;
    /// <summary>Safe to apply without asking (author-linked update, or a newer copy already in the library).</summary>
    public bool UpdateIsConfident => Status is PackageStatus.UpdateAvailable or PackageStatus.UpdateDownloaded;
}

public sealed record OperationProgress(string Message, double? Fraction = null);

/// <summary>Coordinates the library, the installer, Fluffy import and Nexus updates. One operation at a time.</summary>
public sealed partial class ModManager
{
    private const long FullRefetchAgeSeconds = 28L * 24 * 3600;

    public AppSettings Settings { get; private set; } = new();
    public AppState State { get; private set; } = new();
    public NexusCache Cache { get; private set; } = new();
    public IReadOnlyList<ModPackage> Library { get; private set; } = [];

    /// <summary>Sends a file to the Recycle Bin. Supplied by the UI; when null, replaced archives are kept.</summary>
    public Func<string, bool>? RecycleFile { get; set; }

    /// <summary>Nexus API calls left today, as of the last request.</summary>
    public int? ApiDailyRemaining { get; private set; }

    public GameFolder? Game => string.IsNullOrEmpty(Settings.GamePath) ? null : new GameFolder(Settings.GamePath);
    public string LibraryPath => Settings.LibraryPath ?? AppPaths.DefaultLibrary;
    public bool HasApiKey => !string.IsNullOrEmpty(Settings.NexusApiKeyProtected);

    public void Load()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Log.FilePath = AppPaths.Log;
        Settings = JsonStore.Load<AppSettings>(AppPaths.Settings);
        State = JsonStore.Load<AppState>(AppPaths.State);
        Cache = JsonStore.Load<NexusCache>(AppPaths.NexusCache);

        if (Settings.FluffyPath == null && !State.FluffyImported)
            Settings.FluffyPath = FluffyImport.Locate();
        if (Settings.GamePath == null && Settings.FluffyPath != null && File.Exists(FluffyImport.InstalledIni(Settings.FluffyPath)))
            Settings.GamePath = FluffyImport.ReadInstalled(FluffyImport.InstalledIni(Settings.FluffyPath)).GamePath;
        Settings.GamePath ??= GameLocator.Find();
        if (Settings.LibraryPath == null && Settings.FluffyPath != null && Directory.Exists(FluffyImport.ModsDir(Settings.FluffyPath)))
            Settings.LibraryPath = FluffyImport.ModsDir(Settings.FluffyPath);
        SaveSettings();
    }

    public void SaveSettings() => JsonStore.Save(AppPaths.Settings, Settings);
    public void SaveState() => JsonStore.Save(AppPaths.State, State);
    private void SaveCache() => JsonStore.Save(AppPaths.NexusCache, Cache);

    public void Refresh()
    {
        Directory.CreateDirectory(LibraryPath);
        Library = ModLibrary.Scan(LibraryPath);
        foreach (var stale in State.Nexus.Keys.Where(k => Library.All(p => !PathUtil.Comparer.Equals(p.FileName, k))).ToList())
            State.Nexus.Remove(stale);
        if (ReconcileSharedFiles()) SaveState();
        Log.Info($"Library: {Library.Count} packages in {LibraryPath}");
    }

    /// <summary>
    /// Makes every installed mod list the shared files it ships. Fluffy records a file bundled by several mods
    /// under only one of them, which would let uninstalling that one delete a file the others still need.
    /// </summary>
    private bool ReconcileSharedFiles()
    {
        var owned = State.Installed
            .SelectMany(m => m.Files.Select(f => (f.Path, Mod: m)))
            .ToLookup(x => x.Path, x => x.Mod, PathUtil.Comparer);
        var packages = Library.ToDictionary(p => p.FileName, PathUtil.Comparer);
        var changed = false;
        foreach (var mod in State.Installed)
        {
            if (mod.PackageFileName == null || !packages.TryGetValue(mod.PackageFileName, out var package)) continue;
            var unit = package.Units.FirstOrDefault(u => PathUtil.Comparer.Equals(u.Id, mod.UnitId));
            if (unit == null) continue;
            var listed = mod.Files.Select(f => f.Path).ToHashSet(PathUtil.Comparer);
            foreach (var target in unit.Files.Where(f => !f.IsPak).Select(f => f.Target))
            {
                if (listed.Contains(target) || !owned[target].Any(o => o != mod)) continue;
                mod.Files.Add(new InstalledFile { Path = target });
                listed.Add(target);
                changed = true;
                Log.Info($"{mod.DisplayName} also uses {target} (shared with {string.Join(", ", owned[target].Select(o => o.DisplayName))})");
            }
        }
        return changed;
    }

    private Installer CreateInstaller()
    {
        var game = Game ?? throw new InvalidOperationException("Set the Dragon's Dogma 2 folder in Settings first.");
        if (!game.IsValid) throw new InvalidOperationException($"DD2.exe not found in {game.Root}");
        return new Installer(game.Root, AppPaths.Backup, State, SaveState);
    }

    // ---------------------------------------------------------------- Fluffy

    public bool CanImportFromFluffy =>
        !State.FluffyImported && Settings.FluffyPath != null && File.Exists(FluffyImport.InstalledIni(Settings.FluffyPath));

    /// <summary>Adopts Fluffy's installed mods so they can be uninstalled and updated from here. Nothing on disk changes.</summary>
    public IReadOnlyList<FluffyMatch> ImportFromFluffy()
    {
        var root = Settings.FluffyPath ?? throw new InvalidOperationException("Fluffy Mod Manager folder not set");
        var install = FluffyImport.ReadInstalled(FluffyImport.InstalledIni(root));
        var gameDir = Settings.GamePath ?? install.GamePath ?? throw new InvalidOperationException("Game folder not set");
        var matches = FluffyImport.Match(install, Library, gameDir);
        foreach (var mod in FluffyImport.ToInstalledMods(matches))
        {
            var alreadyTracked = State.Installed.Any(m => PathUtil.Comparer.Equals(m.UnitId, mod.UnitId) &&
                                                          PathUtil.Comparer.Equals(m.PackageFileName, mod.PackageFileName));
            if (!alreadyTracked && mod.Files.Count > 0) State.Installed.Add(mod);
        }
        State.FluffyImported = true;
        ReconcileSharedFiles();
        SaveState();
        Log.Info($"Imported {matches.Count} installed mods from Fluffy ({matches.Count(m => m.Package == null)} without a matching archive)");
        return matches;
    }

    // ---------------------------------------------------------------- views

    public IReadOnlyList<PackageView> BuildViews(bool collapseVersions)
    {
        var views = new List<PackageView>();
        var families = Library.GroupBy(FamilyKey).ToList();
        foreach (var family in families)
        {
            var ordered = family.OrderByDescending(UploadedAt).ToList();
            if (collapseVersions)
            {
                // One row per mod. Parts can be installed from different versions (e.g. two Content Editor parts still on 1.5.6).
                var installedFrom = ordered.Where(p => State.InstalledFrom(p.FileName).Any()).ToList();
                views.Add(BuildView(installedFrom.FirstOrDefault() ?? ordered[0], ordered, installedFrom));
            }
            else
            {
                views.AddRange(ordered.Select(p => BuildView(p, ordered, State.InstalledFrom(p.FileName).Any() ? [p] : [])));
            }
        }

        // Installed mods whose archive was deleted (or never matched during Fluffy import) still need an uninstall row.
        foreach (var orphan in State.Installed.Where(m => m.PackageFileName == null || Library.All(p => !PathUtil.Comparer.Equals(p.FileName, m.PackageFileName))))
        {
            views.Add(new PackageView
            {
                Name = orphan.DisplayName,
                Version = orphan.Version,
                Installed = [orphan],
                InstallState = InstallState.Installed,
                Status = PackageStatus.ArchiveMissing,
                StatusText = "Archive missing",
            });
        }
        return views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private PackageView BuildView(ModPackage package, IReadOnlyList<ModPackage> family, IReadOnlyList<ModPackage> installedFrom)
    {
        var installed = installedFrom.SelectMany(p => State.InstalledFrom(p.FileName)).ToList();
        var installedUnits = installed.Select(m => m.UnitId).Distinct(PathUtil.Comparer).Count();
        var installState = installed.Count == 0 ? InstallState.NotInstalled
            : installedUnits >= package.Units.Count ? InstallState.Installed : InstallState.Partial;
        var newest = family[0];
        var outdatedParts = installedFrom.Where(p => p != newest && UploadedAt(p) < UploadedAt(newest)).ToList();
        var newerLocal = (newest != package && UploadedAt(newest) > UploadedAt(package)) || outdatedParts.Count > 0 ? newest : null;
        var link = State.Nexus.GetValueOrDefault(package.FileName);
        var modId = link?.ModId ?? package.ParsedName.NexusModId;
        var cached = modId is { } id ? Cache.Mods.GetValueOrDefault(id) : null;

        // Compare Nexus against the newest copy we have, so a downloaded-but-not-installed update isn't offered twice.
        var newestLink = State.Nexus.GetValueOrDefault(newest.FileName);
        var update = cached != null && newestLink?.FileId is { } fileId ? UpdateResolver.Resolve(fileId, cached.Files) : null;

        PackageStatus status;
        string text;
        string? latest = null;
        if (package.Error != null) { status = PackageStatus.Unreadable; text = package.Error; }
        else if (link?.IgnoreUpdates == true) { status = PackageStatus.Ignored; text = "Updates ignored"; }
        else if (update is { HasUpdate: true })
        {
            latest = update.Latest?.Version;
            status = update.Status == UpdateStatus.ProbableUpdate ? PackageStatus.ProbableUpdate : PackageStatus.UpdateAvailable;
            text = $"{(status == PackageStatus.ProbableUpdate ? "Possible update" : "Update")} → {latest ?? update.Latest?.Name}";
        }
        else if (newerLocal != null)
        {
            latest = newerLocal.Version;
            status = PackageStatus.UpdateDownloaded;
            text = newerLocal == package
                ? $"Some parts outdated → {latest ?? newerLocal.FileName}"
                : $"Newer copy downloaded → {latest ?? newerLocal.FileName}";
        }
        else if (update?.Status == UpdateStatus.UpToDate) { status = PackageStatus.UpToDate; text = "Up to date"; latest = update.Latest?.Version; }
        else if (update?.Status == UpdateStatus.NoLongerListed) { status = PackageStatus.NoLongerListed; text = "Removed from Nexus"; }
        else if (link is { Md5Searched: true, ModId: null } && modId == null) { status = PackageStatus.NotOnNexus; text = "Not found on Nexus"; }
        else { status = PackageStatus.Unchecked; text = modId != null ? "Not checked yet" : "Unknown source"; }

        return new PackageView
        {
            Package = package,
            Name = package.DisplayName,
            Version = package.Version ?? cached?.Files.Files.FirstOrDefault(f => f.FileId == link?.FileId)?.Version,
            Installed = installed,
            InstallState = installState,
            Family = family,
            OtherVersions = family.Where(p => p != package).ToList(),
            NewerLocal = newerLocal,
            Link = link,
            NexusInfo = cached?.Info,
            Update = update,
            Status = status,
            StatusText = text,
            LatestVersion = latest,
        };
    }

    /// <summary>Versions of the same mod file group together: same Nexus mod and same name once version numbers are stripped.</summary>
    private string FamilyKey(ModPackage p)
    {
        var modId = State.Nexus.GetValueOrDefault(p.FileName)?.ModId ?? p.ParsedName.NexusModId;
        return $"{modId}|{ArchiveNameParser.Normalize(p.ParsedName.DisplayName)}";
    }

    private DateTimeOffset UploadedAt(ModPackage p)
    {
        var link = State.Nexus.GetValueOrDefault(p.FileName);
        if (Cache.FindFile(link?.ModId, link?.FileId) is { } file) return file.Uploaded;
        return p.ParsedName.UploadedAt ?? new DateTimeOffset(p.LastWriteUtc, TimeSpan.Zero);
    }

    // ---------------------------------------------------------------- install / uninstall

    public static bool NeedsGameClosed(IEnumerable<string> gamePaths) => gamePaths.Any(p =>
        p.StartsWith("natives/", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith(ModLayout.PakFolder + "/", StringComparison.OrdinalIgnoreCase) ||
        (!p.Contains('/') && p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ||
        p.StartsWith("reframework/plugins/", StringComparison.OrdinalIgnoreCase));

    private void EnsureGameClosed(IEnumerable<string> gamePaths)
    {
        if (Game?.IsRunning == true && NeedsGameClosed(gamePaths))
            throw new InvalidOperationException("Close Dragon's Dogma 2 first: this changes files the game keeps open.");
    }

    public IReadOnlyList<InstallConflict> FindConflicts(ModPackage package, IEnumerable<ModUnit> units)
    {
        var installer = CreateInstaller();
        return units.SelectMany(u => installer.FindConflicts(u)).ToList();
    }

    public void Install(ModPackage package, IEnumerable<ModUnit> units, bool overwriteConflicts, IProgress<OperationProgress>? progress = null)
    {
        var toInstall = units.Where(u => !State.InstalledFrom(package.FileName).Any(m => PathUtil.Comparer.Equals(m.UnitId, u.Id))).ToList();
        if (toInstall.Count == 0) return;
        var installer = CreateInstaller();
        EnsureGameClosed(toInstall.SelectMany(u => u.Files.Select(f => f.Target)));
        progress?.Report(new OperationProgress($"Extracting {package.DisplayName}…"));
        using var extracted = Extract(package);
        foreach (var unit in toInstall)
        {
            progress?.Report(new OperationProgress($"Installing {unit.DisplayName}…"));
            installer.Install(package, unit, extracted.Path, overwriteConflicts);
        }
    }

    public void Uninstall(IEnumerable<InstalledMod> mods, IProgress<OperationProgress>? progress = null)
    {
        var list = mods.ToList();
        if (list.Count == 0) return;
        var installer = CreateInstaller();
        EnsureGameClosed(list.SelectMany(m => m.Files.Select(f => f.Path)));
        foreach (var mod in list)
        {
            progress?.Report(new OperationProgress($"Uninstalling {mod.DisplayName}…"));
            installer.Uninstall(mod);
        }
    }

    /// <summary>
    /// Replaces the installed units of <paramref name="from"/> with the matching units of <paramref name="to"/>,
    /// keeping each pak's load-order slot. If installing the new version fails, the old one is put back.
    /// </summary>
    public void SwapInstalled(ModPackage from, ModPackage to, IProgress<OperationProgress>? progress = null)
    {
        var installer = CreateInstaller();
        var old = State.InstalledFrom(from.FileName).ToList();
        if (old.Count == 0) return;
        EnsureGameClosed(old.SelectMany(m => m.Files.Select(f => f.Path)).Concat(to.Units.SelectMany(u => u.Files.Select(f => f.Target))));

        progress?.Report(new OperationProgress($"Extracting {to.DisplayName}…"));
        using var extracted = Extract(to);
        foreach (var oldMod in old)
        {
            var newUnit = to.Units.FirstOrDefault(u => PathUtil.Comparer.Equals(u.Id, oldMod.UnitId))
                          ?? (from.Units.Count == 1 && to.Units.Count == 1 ? to.Units[0] : null);
            if (newUnit == null)
            {
                Log.Warn($"{to.FileName} has no counterpart for {oldMod.UnitId}; leaving the old version installed");
                continue;
            }

            var slots = Installer.PakSlotsOf(oldMod);
            progress?.Report(new OperationProgress($"Updating {oldMod.DisplayName}…"));
            installer.Uninstall(oldMod);
            try
            {
                // The new version may bundle a newer copy of a file other mods share; it replaces it but they keep using it.
                installer.Install(to, newUnit, extracted.Path, overwriteConflicts: true, slots);
            }
            catch (Exception ex)
            {
                Log.Error($"Installing the new version of {oldMod.DisplayName} failed ({ex.Message}); restoring the old version");
                var oldUnit = from.Units.FirstOrDefault(u => PathUtil.Comparer.Equals(u.Id, oldMod.UnitId)) ?? from.Units.FirstOrDefault();
                if (oldUnit != null)
                {
                    using var oldExtracted = Extract(from);
                    installer.Install(from, oldUnit, oldExtracted.Path, overwriteConflicts: true, slots);
                }
                throw;
            }
        }
    }

    public bool DeletePackage(ModPackage package)
    {
        if (State.InstalledFrom(package.FileName).Any())
            throw new InvalidOperationException("Uninstall it before deleting the archive.");
        if (RecycleFile == null || !RecycleFile(package.FullPath)) return false;
        State.Nexus.Remove(package.FileName);
        SaveState();
        Log.Info($"Moved {package.FileName} to the Recycle Bin");
        return true;
    }

    private static TempDirectory Extract(ModPackage package)
    {
        var dir = new TempDirectory(Path.Combine(AppPaths.Extract, Guid.NewGuid().ToString("N")));
        package.OpenSource().ExtractAll(dir.Path);
        return dir;
    }

    // ---------------------------------------------------------------- Nexus

    public NexusClient CreateClient() =>
        new(Settings.NexusApiKey ?? throw new InvalidOperationException("Add your Nexus API key in Settings first."));

    /// <summary>Links library files to Nexus, then refreshes file lists for every linked mod that changed.</summary>
    public async Task CheckUpdatesAsync(IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        using var client = CreateClient();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fetchedThisRun = new HashSet<int>();

        async Task<CachedNexusMod> FetchAsync(int modId)
        {
            if (fetchedThisRun.Contains(modId)) return Cache.Mods[modId];
            var files = await client.GetModFilesAsync(modId, ct);
            NexusMod? info = null;
            try { info = await client.GetModAsync(modId, ct); }
            catch (NexusApiException ex) when (ex is not NexusRateLimitException) { Log.Warn($"Mod {modId} page unavailable: {ex.Message}"); }
            fetchedThisRun.Add(modId);
            return Cache.Mods[modId] = new CachedNexusMod { FetchedUnix = now, Files = files, Info = info };
        }

        // 1. Identify packages we haven't matched to a Nexus file yet.
        var packages = Library.Where(p => p.Error == null).ToList();
        for (var i = 0; i < packages.Count; i++)
        {
            var package = packages[i];
            var link = LinkFor(package);
            if (link.FileId != null) continue;
            progress?.Report(new OperationProgress($"Identifying {package.DisplayName}…", (double)i / packages.Count * 0.5));

            if (!link.Md5Searched && !package.IsFolder)
            {
                link.Md5 ??= await Task.Run(() => Md5Of(package.FullPath), ct);
                var hits = await client.Md5SearchAsync(link.Md5, ct);
                link.Md5Searched = true;
                var hit = hits.FirstOrDefault(h => string.Equals(h.Mod.DomainName, NexusClient.GameDomain, StringComparison.OrdinalIgnoreCase))
                          ?? hits.FirstOrDefault();
                if (hit != null)
                {
                    (link.ModId, link.FileId, link.MatchedBy) = (hit.Mod.ModId, hit.FileDetails.FileId, "md5");
                    continue;
                }
            }

            var modId = link.ModId ?? package.ParsedName.NexusModId;
            if (modId == null) continue;
            try
            {
                var cached = Cache.Mods.TryGetValue(modId.Value, out var c) && now - c.FetchedUnix < 3600 ? c : await FetchAsync(modId.Value);
                link.ModId = modId;
                if (UpdateResolver.IdentifyByName(package, cached.Files) is { } file)
                    (link.FileId, link.MatchedBy) = (file.FileId, "name");
            }
            catch (NexusApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
            {
                Log.Warn($"{package.FileName}: mod {modId} not found on Nexus");
            }
        }
        SaveState();

        // 2. Refresh file lists. Mods fetched within 28 days are only refetched if Nexus reports a change since.
        var modIds = State.Nexus.Values.Select(l => l.ModId).OfType<int>().Distinct().Where(id => !fetchedThisRun.Contains(id)).ToList();
        var stale = modIds.Where(id => !Cache.Mods.TryGetValue(id, out var c) || now - c.FetchedUnix > FullRefetchAgeSeconds).ToList();
        var recent = modIds.Except(stale).ToList();
        if (recent.Count > 0)
        {
            progress?.Report(new OperationProgress("Asking Nexus what changed this month…", 0.55));
            var changed = (await client.GetUpdatedAsync("1m", ct)).ToDictionary(u => u.ModId);
            stale.AddRange(recent.Where(id => changed.TryGetValue(id, out var u) &&
                                              Math.Max(u.LatestFileUpdate, u.LatestModActivity) > Cache.Mods[id].FetchedUnix));
        }
        for (var i = 0; i < stale.Count; i++)
        {
            progress?.Report(new OperationProgress($"Fetching mod {stale[i]}…", 0.6 + 0.4 * i / Math.Max(1, stale.Count)));
            try { await FetchAsync(stale[i]); }
            catch (NexusApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound) { Log.Warn($"Mod {stale[i]} is gone from Nexus"); }
        }

        State.LastUpdateCheckUtc = DateTime.UtcNow;
        ApiDailyRemaining = client.DailyRemaining ?? ApiDailyRemaining;
        SaveState();
        SaveCache();
        progress?.Report(new OperationProgress("Checking mod requirements…", 1));
        await TryRefreshRequirementsAsync(ct: ct);
        Log.Info($"Update check done: {fetchedThisRun.Count} mods refreshed; API calls left today: {client.DailyRemaining?.ToString() ?? "?"}");
    }

    /// <summary>Brings a package up to date: downloads the newest file if needed, swaps the install, and tidies old archives.</summary>
    public async Task<ModPackage> UpdateAsync(PackageView view, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        if (view.Package == null) throw new InvalidOperationException("No archive to update from");
        ModPackage target;
        if (view.Update is { HasUpdate: true, Latest: { } latest } && view.ModId is { } modId)
            target = await DownloadAsync(modId, latest, null, null, progress, ct);
        else if (view.NewerLocal != null)
            target = view.NewerLocal;
        else
            throw new InvalidOperationException("No update available");

        // Every older version with installed parts moves to the target, not just the row's own archive.
        var family = view.Family.Count > 0 ? view.Family : [view.Package];
        var sources = family.Where(p => p != target && State.InstalledFrom(p.FileName).Any()).ToList();
        if (Game?.IsRunning == true &&
            NeedsGameClosed(sources.SelectMany(p => State.InstalledFrom(p.FileName)).SelectMany(m => m.Files.Select(f => f.Path))))
            throw new InvalidOperationException("Dragon's Dogma 2 is running. Close it and click Update again.");

        foreach (var source in sources) SwapInstalled(source, target, progress);

        if (Settings.RecycleReplacedArchives && RecycleFile != null)
        {
            foreach (var old in family.Where(p => p != target && UploadedAt(p) <= UploadedAt(target)))
            {
                if (!State.InstalledFrom(old.FileName).Any() && File.Exists(old.FullPath) && RecycleFile(old.FullPath))
                {
                    State.Nexus.Remove(old.FileName);
                    Log.Info($"Moved replaced archive {old.FileName} to the Recycle Bin");
                }
            }
            SaveState();
        }
        Refresh();
        return target;
    }

    /// <summary>Downloads a Nexus file into the library. Key/expires come from nxm:// links; Premium accounts can omit them.</summary>
    public async Task<ModPackage> DownloadAsync(int modId, NexusFile file, string? key, long? expires,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        using var client = CreateClient();
        progress?.Report(new OperationProgress($"Requesting download for {file.Name ?? file.FileName}…"));
        List<NexusDownloadLink> links;
        try
        {
            links = await client.GetDownloadLinksAsync(modId, file.FileId, key, expires, ct);
        }
        catch (NexusApiException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden && key == null)
        {
            throw new InvalidOperationException("Nexus only allows direct downloads for Premium accounts. Use \"Mod Manager Download\" on the mod page instead.", ex);
        }
        if (links.Count == 0) throw new InvalidOperationException("Nexus returned no download servers");

        var uri = new Uri(links[0].Uri);
        var fileName = file.FileName ?? Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
        var temp = Path.Combine(AppPaths.Downloads, Guid.NewGuid().ToString("N"), fileName);
        var reporter = new Progress<DownloadProgress>(p => progress?.Report(new OperationProgress(
            $"Downloading {fileName}… {p.BytesReceived / 1048576.0:0.0} MB" + (p.TotalBytes is { } t ? $" of {t / 1048576.0:0.0} MB" : ""),
            p.TotalBytes is { } total and > 0 ? (double)p.BytesReceived / total : null)));
        await client.DownloadAsync(uri, temp, reporter, ct);
        ApiDailyRemaining = client.DailyRemaining ?? ApiDailyRemaining;

        var destination = UniquePath(Path.Combine(LibraryPath, fileName));
        File.Move(temp, destination);
        Directory.Delete(Path.GetDirectoryName(temp)!, recursive: true);
        var info = new FileInfo(destination);
        State.Nexus[info.Name] = new NexusLink
        {
            ModId = modId, FileId = file.FileId, MatchedBy = "download", Md5Searched = true,
            Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc,
        };
        SaveState();
        Log.Info($"Downloaded {info.Name}");
        Refresh();
        await TryRefreshRequirementsAsync(ct: ct);
        return Library.First(p => PathUtil.Comparer.Equals(p.FileName, info.Name));
    }

    /// <summary>
    /// Handles a "Mod Manager Download" click. Returns the downloaded package and the installed package it replaces, if any,
    /// so the UI can offer to swap.
    /// </summary>
    public async Task<(ModPackage Downloaded, PackageView? Replaces)> HandleNxmAsync(NxmLink link, IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!link.Game.Equals(NexusClient.GameDomain, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"That link is for {link.Game}, not Dragon's Dogma 2.");

        using (var client = CreateClient())
        {
            var files = await client.GetModFilesAsync(link.ModId, ct);
            NexusMod? info = null;
            try { info = await client.GetModAsync(link.ModId, ct); } catch (NexusApiException) { }
            Cache.Mods[link.ModId] = new CachedNexusMod { FetchedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Files = files, Info = info };
            SaveCache();
        }
        var file = Cache.Mods[link.ModId].Files.Files.FirstOrDefault(f => f.FileId == link.FileId)
                   ?? new NexusFile { FileId = link.FileId };
        var downloaded = await DownloadAsync(link.ModId, file, link.Key, link.Expires, progress, ct);

        var replaces = BuildViews(collapseVersions: true)
            .FirstOrDefault(v => v.Package != null && v.Package != downloaded && v.InstallState != InstallState.NotInstalled &&
                                 v.NewerLocal == downloaded);
        return (downloaded, replaces);
    }

    public void SetIgnoreUpdates(ModPackage package, bool ignore)
    {
        LinkFor(package).IgnoreUpdates = ignore;
        SaveState();
    }

    private NexusLink LinkFor(ModPackage package)
    {
        if (State.Nexus.TryGetValue(package.FileName, out var link) && link.Size == package.Size && link.LastWriteUtc == package.LastWriteUtc)
            return link;
        // New file, or replaced in place: start over, keeping only the user's ignore choice.
        var fresh = new NexusLink { Size = package.Size, LastWriteUtc = package.LastWriteUtc, IgnoreUpdates = link?.IgnoreUpdates ?? false };
        State.Nexus[package.FileName] = fresh;
        return fresh;
    }

    private static string Md5Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory(string path)
    {
        Path = path;
        Directory.CreateDirectory(path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
