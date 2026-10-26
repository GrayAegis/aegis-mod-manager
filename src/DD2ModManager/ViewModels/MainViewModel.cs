using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DD2ModManager.Core;
using DD2ModManager.Core.Archives;
using DD2ModManager.Core.Install;
using DD2ModManager.Core.Library;
using DD2ModManager.Core.Nexus;
using DD2ModManager.Core.State;
using DD2ModManager.Core.Util;
using DD2ModManager.Services;

namespace DD2ModManager.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public ModManager Manager { get; } = new() { RecycleFile = ShellOps.Recycle };

    public ObservableCollection<PackageItemViewModel> Items { get; } = [];
    public ICollectionView ItemsView { get; }

    [ObservableProperty] private PackageItemViewModel? _selected;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private bool _hideOlderVersions = true;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private bool _progressIndeterminate = true;
    [ObservableProperty] private double _progressValue;

    [ObservableProperty] private string _nexusText = "Nexus: no API key";
    [ObservableProperty] private bool _nexusConnected;
    [ObservableProperty] private string _quotaText = "";
    [ObservableProperty] private string _gamePathText = "Game folder not set";
    [ObservableProperty] private bool _hasFramework;
    [ObservableProperty] private bool _pakLoadingOn;
    [ObservableProperty] private string _pakLoadingTip = "";

    [ObservableProperty] private int _updateCount;
    [ObservableProperty] private int _installedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _withUpdatesCount;

    public MainViewModel()
    {
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is PackageItemViewModel item && Matches(item);
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(PackageItemViewModel.Name), ListSortDirection.Ascending));
    }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();
    partial void OnFilterChanged(string value) => ItemsView.Refresh();

    partial void OnHideOlderVersionsChanged(bool value)
    {
        if (Manager.Settings.HideOlderVersions == value) return;
        Manager.Settings.HideOlderVersions = value;
        Manager.SaveSettings();
        Rebuild();
    }

    partial void OnSelectedChanged(PackageItemViewModel? value) => _ = value?.LoadScreenshotAsync();

    private bool Matches(PackageItemViewModel item)
    {
        if (SearchText.Length > 0 &&
            !item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
            !item.ArchiveName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
            item.ModIdText != SearchText.Trim())
            return false;
        return Filter switch
        {
            "Installed" => item.AnyInstalled,
            "Updates" => item.CanUpdate,
            "NotInstalled" => !item.AnyInstalled,
            _ => true,
        };
    }

    // ---------------------------------------------------------------- startup

    public async Task InitializeAsync(string? nxmLink)
    {
        await RunAsync("Loading library…", async _ =>
        {
            await Task.Run(() =>
            {
                Manager.Load();
                Manager.Refresh();
            });
            HideOlderVersions = Manager.Settings.HideOlderVersions;
        });

        if (Manager.CanImportFromFluffy)
        {
            var install = Core.Fluffy.FluffyImport.ReadInstalled(Core.Fluffy.FluffyImport.InstalledIni(Manager.Settings.FluffyPath!));
            if (!Dialogs.Interactive || Dialogs.Confirm("Import from Fluffy Mod Manager",
                    $"Found Fluffy Mod Manager in {Manager.Settings.FluffyPath} with {install.Entries.Count} installed mods.\n\n" +
                    "Import them so this manager can update and uninstall them? Nothing in your game folder changes.\n\n" +
                    "After importing, manage DD2 mods here rather than in Fluffy so the two don't disagree about what's installed."))
            {
                await RunAsync("Importing from Fluffy…", async _ =>
                {
                    var matches = await Task.Run(Manager.ImportFromFluffy);
                    StatusText = $"Imported {matches.Count} mods from Fluffy";
                });
            }
            else
            {
                Manager.State.FluffyImported = true;
                Manager.SaveState();
            }
        }

        if (Manager.Game is not { IsValid: true } && Dialogs.Interactive)
        {
            Dialogs.Info("Game folder", "Couldn't find Dragon's Dogma 2 automatically. Set the game folder in Settings.");
            OpenSettings();
        }

        // Public data, no API key needed; only refetched once a day or for mods it hasn't seen.
        await RunAsync("Checking mod requirements…", _ => Manager.TryRefreshRequirementsAsync());

        if (!Dialogs.Interactive) return;
        await RefreshAccountAsync();
        if (nxmLink != null) await HandleExternalMessageAsync(nxmLink);
        else if (Manager.HasApiKey && Manager.Settings.CheckUpdatesOnStartup) await CheckUpdatesAsync();
        else if (!Manager.HasApiKey) StatusText = "Add your Nexus API key in Settings to check for updates.";
    }

    public async Task HandleExternalMessageAsync(string message)
    {
        if (Application.Current.MainWindow is { } window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
        if (!NxmLink.TryParse(message, out var link)) return;
        if (IsBusy)
        {
            Dialogs.Warn("Busy", "Finish the current operation, then click the download link again.");
            return;
        }

        ModPackage? downloaded = null;
        PackageView? replaces = null;
        await RunAsync("Downloading from Nexus…", async progress =>
        {
            (downloaded, replaces) = await Manager.HandleNxmAsync(link, progress);
        });
        if (downloaded == null) return;
        Select(downloaded.FileName);

        if (replaces != null)
        {
            if (Dialogs.Confirm("Update installed mod", $"{downloaded.FileName} is a newer version of {replaces.Name} ({replaces.Version}).\n\nSwitch to it now?"))
            {
                await RunAsync($"Updating {replaces.Name}…", progress => Manager.UpdateAsync(replaces, progress));
                Select(downloaded.FileName);
            }
        }
        else if (Dialogs.Confirm("Install mod", $"Downloaded {downloaded.DisplayName}. Install it now?"))
        {
            await InstallAsync(downloaded, downloaded.Units);
        }
    }

    // ---------------------------------------------------------------- list

    public void Rebuild()
    {
        var selectedKey = Selected?.Key;
        var views = Manager.BuildViews(HideOlderVersions);
        Items.Clear();
        foreach (var view in views) Items.Add(new PackageItemViewModel(view, Manager));
        if (selectedKey != null) Select(selectedKey);

        TotalCount = Items.Count;
        InstalledCount = Items.Count(i => i.AnyInstalled);
        WithUpdatesCount = Items.Count(i => i.CanUpdate);
        UpdateCount = Items.Count(i => i.AnyInstalled && i.View.UpdateIsConfident);

        var game = Manager.Game;
        GamePathText = game?.Root ?? "Game folder not set";
        HasFramework = game?.HasREFramework == true;
        PakLoadingOn = game?.PakFolderLoadingEnabled == true;
        PakLoadingTip = PakLoadingOn
            ? "REFramework loads paks from pak_mods"
            : "Pak mods won't load: turn on REFramework's \"Alternate PAK folder\" (IntegrityCheckBypass_LoadPakDirectory).";
    }

    private void Select(string key) =>
        Selected = Items.FirstOrDefault(i => PathUtil.Comparer.Equals(i.Key, key));

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private Task Refresh() => RunAsync("Rescanning library…", _ => Task.Run(Manager.Refresh));

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (!Manager.HasApiKey)
        {
            Dialogs.Info("Nexus API key needed", "Add your personal Nexus Mods API key in Settings to check for updates.");
            return;
        }
        await RunAsync("Checking Nexus for updates…", progress => Manager.CheckUpdatesAsync(progress));
        StatusText = WithUpdatesCount == 0 ? "Everything is up to date." : $"{WithUpdatesCount} update{(WithUpdatesCount == 1 ? "" : "s")} available.";
        if (Manager.Settings.AutoInstallUpdates && UpdateCount > 0) await UpdateAllAsync(automatic: true);
    }

    [RelayCommand]
    private Task UpdateAll() => UpdateAllAsync(automatic: false);

    private async Task UpdateAllAsync(bool automatic)
    {
        var targets = Items.Where(i => i.AnyInstalled && i.View.UpdateIsConfident && i.Package != null).Select(i => i.View).ToList();
        if (targets.Count == 0) return;
        if (!automatic && !Dialogs.Confirm("Update all", $"Update {targets.Count} mod{(targets.Count == 1 ? "" : "s")}?\n\n" +
                string.Join("\n", targets.Select(t => $"• {t.Name}  {t.Version} → {t.LatestVersion}"))))
            return;

        var failures = new List<string>();
        var updated = new List<ModPackage>();
        await RunAsync("Updating mods…", async progress =>
        {
            foreach (var view in targets)
            {
                try { updated.Add(await Manager.UpdateAsync(view, progress)); }
                catch (Exception ex) when (ex is not NexusRateLimitException)
                {
                    Log.Error($"Updating {view.Name} failed: {ex.Message}");
                    failures.Add($"{view.Name}: {ex.Message}");
                }
            }
        });
        StatusText = failures.Count == 0 ? $"Updated {targets.Count} mods." : $"Updated {targets.Count - failures.Count} of {targets.Count} mods.";
        if (failures.Count > 0) Dialogs.Warn("Some updates failed", string.Join("\n\n", failures));
        // A new version can add requirements ("Required since v1.5.1").
        await WarnMissingRequirementsAsync(updated.Select(p => (p, p.Units)).ToList());
    }

    /// <summary>
    /// Uninstalling something other installed mods need is allowed only after an explicit yes (No is the default).
    /// </summary>
    private bool ConfirmNothingBreaks(IReadOnlyCollection<InstalledMod> removing, string name)
    {
        var broken = Manager.DependentsBrokenBy(removing);
        if (broken.Count == 0) return true;
        var lines = string.Join("\n", broken.Take(12).Select(b => "• " + b)) + (broken.Count > 12 ? $"\n…and {broken.Count - 12} more" : "");
        return Dialogs.ConfirmRisky("Other mods need this",
            $"{name} is required by mods you still have installed:\n\n{lines}\n\nThey may stop working or crash the game. Uninstall anyway?");
    }

    /// <summary>
    /// After installing or updating, lists hard requirements that aren't installed. Ones already in the library can be
    /// installed straight away; the rest link to their Nexus pages.
    /// </summary>
    private async Task WarnMissingRequirementsAsync(IReadOnlyList<(ModPackage Package, IReadOnlyList<ModUnit> Units)> installed)
    {
        var missing = installed
            .SelectMany(i => Manager.MissingRequirements(i.Package, i.Units).Select(r => (i.Package, Requirement: r)))
            .ToList();
        if (missing.Count == 0) return;

        var summary = string.Join("\n", missing.GroupBy(m => m.Package.DisplayName)
            .Select(g => $"• {g.Key} needs {string.Join(", ", g.Select(m => m.Requirement.Name).Distinct())}"));
        var requirements = missing.Select(m => m.Requirement).DistinctBy(r => r.Name).ToList();
        var inLibrary = requirements.Select(r => (Requirement: r, Package: Manager.LibraryPackageFor(r)))
            .Where(x => x.Package != null).ToList();
        var toDownload = requirements.Where(r => inLibrary.All(x => x.Requirement != r) && r.Url != null).ToList();

        if (inLibrary.Count > 0 &&
            Dialogs.Confirm("Missing requirements", $"{summary}\n\nInstall {string.Join(", ", inLibrary.Select(x => x.Package!.DisplayName))} from your library now?"))
        {
            foreach (var (_, package) in inLibrary) await InstallAsync(package!, package!.Units);
        }
        if (toDownload.Count > 0)
        {
            if (Dialogs.Confirm("Missing requirements",
                    $"{summary}\n\nNot in your library: {string.Join(", ", toDownload.Select(r => r.Name))}. Open their pages to download them?"))
                foreach (var r in toDownload.Take(5)) ShellOps.Open(r.Url!);
        }
        else if (inLibrary.Count == 0)
        {
            Dialogs.Warn("Missing requirements", summary);
        }
    }

    [RelayCommand]
    private async Task ToggleInstall(PackageItemViewModel? item)
    {
        if (item == null) return;
        if (item.AnyInstalled)
        {
            if ((item.Package == null && !Dialogs.Confirm("Uninstall", $"The archive for {item.Name} is gone, so you won't be able to reinstall it from here. Uninstall anyway?")) ||
                !ConfirmNothingBreaks(item.View.Installed, item.Name))
            {
                Rebuild();
                return;
            }
            await RunAsync($"Uninstalling {item.Name}…", progress => Task.Run(() => Manager.Uninstall(item.View.Installed, progress)));
            return;
        }

        var package = item.Package!;
        if (package.Units.Count > 1 && UnitsOverlap(package.Units))
        {
            Selected = item;
            Rebuild();
            Dialogs.Info("Choose a version", $"{item.Name} contains alternative versions that replace the same files. Pick one under Parts on the right.");
            return;
        }
        await InstallAsync(package, package.Units);
    }

    [RelayCommand]
    private async Task ToggleUnit(UnitItemViewModel? unit)
    {
        if (unit == null) return;
        if (unit.Installed != null)
        {
            if (!ConfirmNothingBreaks([unit.Installed], unit.Name))
            {
                Rebuild();
                return;
            }
            await RunAsync($"Uninstalling {unit.Name}…", progress => Task.Run(() => Manager.Uninstall([unit.Installed], progress)));
        }
        else
        {
            await InstallAsync(unit.Owner.Package!, [unit.Unit]);
        }
    }

    private async Task InstallAsync(ModPackage package, IReadOnlyList<ModUnit> units)
    {
        var overwrite = false;
        try
        {
            var conflicts = Manager.FindConflicts(package, units);
            if (conflicts.Count > 0)
            {
                var lines = conflicts.Take(10).Select(c => $"• {c.Path}  ({c.OwnerName})");
                var more = conflicts.Count > 10 ? $"\n…and {conflicts.Count - 10} more" : "";
                if (!Dialogs.Confirm("File conflict",
                        $"{conflicts.Count} file(s) already belong to other installed mods:\n\n{string.Join("\n", lines)}{more}\n\n" +
                        "Install anyway? This mod's copies will replace theirs."))
                {
                    Rebuild();
                    return;
                }
                overwrite = true;
            }
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message);
            Rebuild();
            return;
        }

        await RunAsync($"Installing {package.DisplayName}…", progress => Task.Run(() => Manager.Install(package, units, overwrite, progress)));
        var installedNow = units.All(u => Manager.State.InstalledFrom(package.FileName).Any(m => PathUtil.Comparer.Equals(m.UnitId, u.Id)));
        if (!installedNow) return;
        if (units.Any(u => u.HasPaks) && Manager.Game?.PakFolderLoadingEnabled != true)
            Dialogs.Warn("Pak loading is off",
                "This mod includes .pak files. Dragon's Dogma 2 only loads mods from pak_mods when REFramework's " +
                "\"Alternate PAK folder\" option is on (IntegrityCheckBypass_LoadPakDirectory=true in re2_fw_config.txt).");
        await WarnMissingRequirementsAsync([(package, units)]);
    }

    private static bool UnitsOverlap(IReadOnlyList<ModUnit> units)
    {
        var seen = new HashSet<string>(PathUtil.Comparer);
        return units.SelectMany(u => u.Files.Where(f => !f.IsPak).Select(f => f.Target).Distinct(PathUtil.Comparer)).Any(t => !seen.Add(t));
    }

    [RelayCommand]
    private async Task Update(PackageItemViewModel? item)
    {
        if (item?.Package == null || !item.CanUpdate) return;
        if (item.Status == PackageStatus.ProbableUpdate &&
            !Dialogs.Confirm("Possible update", $"Nexus doesn't say which file replaces yours. The best guess is \"{item.View.Update?.Latest?.Name}\" ({item.View.LatestVersion}).\n\nUpdate to it?"))
            return;
        ModPackage? updated = null;
        await RunAsync($"Updating {item.Name}…", async progress => updated = await Manager.UpdateAsync(item.View, progress));
        if (updated != null) await WarnMissingRequirementsAsync([(updated, updated.Units)]);
    }

    [RelayCommand]
    private void OpenNexus(PackageItemViewModel? item)
    {
        if (item?.NexusUrl is { } url) ShellOps.Open(url);
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") ShellOps.Open(url);
    }

    [RelayCommand]
    private async Task DeleteArchive(PackageItemViewModel? item)
    {
        if (item?.Package == null) return;
        if (item.AnyInstalled)
        {
            Dialogs.Info("Delete archive", "Uninstall the mod before deleting its archive.");
            return;
        }
        if (!Dialogs.Confirm("Delete archive", $"Move {item.Package.FileName} to the Recycle Bin?")) return;
        await RunAsync("Deleting…", _ => Task.Run(() =>
        {
            if (!Manager.DeletePackage(item.Package)) throw new IOException($"Couldn't move {item.Package.FileName} to the Recycle Bin.");
            Manager.Refresh();
        }));
    }

    [RelayCommand]
    private async Task RemoveOlderVersions(PackageItemViewModel? item)
    {
        if (item == null || item.RemovableVersions.Count == 0) return;
        var names = item.RemovableVersions.Select(p => p.FileName).ToList();
        if (!Dialogs.Confirm("Remove older versions", $"Move these to the Recycle Bin?\n\n{string.Join("\n", names)}")) return;
        await RunAsync("Removing older versions…", _ => Task.Run(() =>
        {
            foreach (var p in item.RemovableVersions) Manager.DeletePackage(p);
            Manager.Refresh();
        }));
    }

    [RelayCommand]
    private void ToggleIgnoreUpdates(PackageItemViewModel? item)
    {
        if (item?.Package == null) return;
        Manager.SetIgnoreUpdates(item.Package, !item.IgnoreUpdates);
        Rebuild();
    }

    [RelayCommand]
    private async Task AddMods()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add mod archives",
            Filter = "Mod archives (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar",
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true) await AddFilesAsync(dialog.FileNames);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var archives = paths.Where(ModSources.IsArchive).ToList();
        if (archives.Count == 0) return;
        string? last = null;
        await RunAsync("Adding mods…", _ => Task.Run(() =>
        {
            Directory.CreateDirectory(Manager.LibraryPath);
            foreach (var path in archives)
            {
                var target = Path.Combine(Manager.LibraryPath, Path.GetFileName(path));
                if (PathUtil.Comparer.Equals(Path.GetFullPath(path), Path.GetFullPath(target))) continue;
                if (File.Exists(target)) throw new IOException($"{Path.GetFileName(path)} is already in the library.");
                File.Copy(path, target);
                last = Path.GetFileName(target);
                Log.Info($"Added {last} to the library");
            }
            Manager.Refresh();
        }));
        if (last != null) Select(last);
    }

    [RelayCommand]
    private void OpenModsFolder() => ShellOps.Open(Manager.LibraryPath);

    [RelayCommand]
    private void OpenGameFolder()
    {
        if (Manager.Game is { } game) ShellOps.Open(game.Root);
    }

    [RelayCommand]
    private void OpenLog()
    {
        if (File.Exists(AppPaths.Log)) ShellOps.Open(AppPaths.Log);
    }

    [RelayCommand]
    private void LaunchGame() => ShellOps.LaunchGame(GameFolder.SteamAppId);

    [RelayCommand]
    private void OpenSettings()
    {
        var vm = new SettingsViewModel(Manager);
        var window = new SettingsWindow { DataContext = vm, Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        _ = RunAsync("Applying settings…", async _ =>
        {
            await Task.Run(Manager.Refresh);
            if (vm.ImportRequested) await Task.Run(Manager.ImportFromFluffy);
        }).ContinueWith(_ => RefreshAccountAsync(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task RefreshAccountAsync()
    {
        if (!Manager.HasApiKey)
        {
            NexusConnected = false;
            NexusText = "Nexus: no API key";
            return;
        }
        try
        {
            using var client = Manager.CreateClient();
            var user = await client.ValidateAsync();
            NexusConnected = true;
            NexusText = $"{user.Name} · {(user.IsPremium ? "Premium" : "Free")}";
        }
        catch (Exception ex)
        {
            NexusConnected = false;
            NexusText = "Nexus: key not accepted";
            Log.Warn($"Nexus key check failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- plumbing

    private async Task RunAsync(string label, Func<IProgress<OperationProgress>, Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = label;
        ProgressIndeterminate = true;
        var progress = new Progress<OperationProgress>(p =>
        {
            StatusText = p.Message;
            ProgressIndeterminate = p.Fraction == null;
            if (p.Fraction is { } f) ProgressValue = f * 100;
        });
        try
        {
            await work(progress);
            if (StatusText == label || StatusText.EndsWith('…')) StatusText = "Ready";
        }
        catch (InstallConflictException ex)
        {
            Dialogs.Error(ex.Message);
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Rebuild();
            var parts = new List<string>();
            if (Manager.State.LastUpdateCheckUtc is { } last) parts.Add($"Checked {last.ToLocalTime():g}");
            if (Manager.ApiDailyRemaining is { } daily) parts.Add($"{daily} API calls left today");
            if (parts.Count > 0) QuotaText = string.Join(" · ", parts);
        }
    }
}
