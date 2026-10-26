using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DD2ModManager.Core;
using DD2ModManager.Core.Library;
using DD2ModManager.Core.Nexus;
using DD2ModManager.Core.State;
using DD2ModManager.Core.Util;

namespace DD2ModManager.ViewModels;

public sealed class UnitItemViewModel(PackageItemViewModel owner, ModUnit unit, InstalledMod? installed)
{
    public PackageItemViewModel Owner { get; } = owner;
    public ModUnit Unit { get; } = unit;
    public InstalledMod? Installed { get; } = installed;
    public string Name => Unit.DisplayName;
    public bool IsInstalled => Installed != null;
    public string Detail => $"{Unit.Files.Count} file{(Unit.Files.Count == 1 ? "" : "s")}" + (Unit.HasPaks ? " · pak" : "");
}

public sealed class ChangelogItemViewModel(ChangelogEntry entry)
{
    public string VersionText => entry.Version is { Length: > 0 } v ? "v" + v.TrimStart('v', 'V') : "Update";
    public string DateText => entry.Uploaded.LocalDateTime.ToString("d MMM yyyy");
    public string Text => entry.Text;
}

public sealed class RequirementItemViewModel(RequirementInfo info)
{
    public RequirementInfo Info { get; } = info;
    public string Name => Info.Name;
    public string? Url => Info.Url;
    public bool HasUrl => Info.Url != null;
    public bool IsMissingRequired => Info is { Required: true, Status: RequirementStatus.Missing };

    /// <summary>"ok", "missing", "optional" or "unknown"; drives the dot and label colour.</summary>
    public string Kind => Info.Status switch
    {
        RequirementStatus.Installed => "ok",
        RequirementStatus.Missing when Info.Required => "missing",
        RequirementStatus.Missing => "optional",
        _ => "unknown",
    };

    public string StatusText => Info.Status switch
    {
        _ when Info.IsModManager => "This app",
        RequirementStatus.Installed => Name.Contains("REFramework", StringComparison.OrdinalIgnoreCase) ? "Found" : "Installed",
        RequirementStatus.Missing when Info.Required => "Missing",
        RequirementStatus.Missing => "Not installed",
        _ => "External",
    };

    public string Detail
    {
        get
        {
            var note = Info.Notes?.Trim() ?? "";
            if (note.Length > 160) note = note[..157].TrimEnd() + "…";
            if (Info.Required) return note;
            return note.Length == 0 ? "Recommended" : "Recommended · " + note;
        }
    }
}

public sealed partial class PackageItemViewModel : ObservableObject
{
    private readonly ModManager _manager;

    public PackageItemViewModel(PackageView view, ModManager manager)
    {
        View = view;
        _manager = manager;
        Units = view.Package?.Units
            .Select(u => new UnitItemViewModel(this, u, view.Installed.FirstOrDefault(m => PathUtil.Comparer.Equals(m.UnitId, u.Id))))
            .ToList() ?? [];
        var nexusFile = _manager.Cache.FindFile(view.Link?.ModId, view.Link?.FileId);
        Updated = nexusFile?.Uploaded.LocalDateTime
                  ?? view.Package?.ParsedName.UploadedAt?.LocalDateTime
                  ?? view.Package?.LastWriteUtc.ToLocalTime();
        NexusFile = nexusFile;

        Changelog = manager.ChangelogFor(view);
        ChangelogEntries = Changelog?.Entries.Select(e => new ChangelogItemViewModel(e)).ToList() ?? [];
        Requirements = manager.RequirementsOf(view)
            .OrderBy(r => r.Status == RequirementStatus.Missing && r.Required ? 0 : r.Required ? 1 : 2)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new RequirementItemViewModel(r))
            .ToList();
        RequiredBy = manager.RequiredBy(view);
    }

    public ChangelogView? Changelog { get; }
    public IReadOnlyList<ChangelogItemViewModel> ChangelogEntries { get; }
    public string ChangelogTitle => Changelog?.Title ?? "";
    /// <summary>"What's new" goes right under the Update button; a no-update changelog sits lower down.</summary>
    public bool ShowChangelogFirst => Changelog?.IsUpdate == true;
    public bool ShowChangelogLater => Changelog is { IsUpdate: false };

    public IReadOnlyList<RequirementItemViewModel> Requirements { get; }
    public IReadOnlyList<string> RequiredBy { get; }
    public string RequiredByText => string.Join(", ", RequiredBy);

    public bool HasMissingRequired => AnyInstalled && Requirements.Any(r => r.IsMissingRequired);
    public string MissingRequiredTip => "Missing requirements: " + string.Join(", ", Requirements.Where(r => r.IsMissingRequired).Select(r => r.Name));

    public PackageView View { get; }
    public ModPackage? Package => View.Package;
    public NexusFile? NexusFile { get; }
    public string Key => View.Package?.FileName ?? "missing:" + View.Name;

    public string Name => View.Name;
    public string Version => View.Version ?? "";
    public PackageStatus Status => View.Status;
    public string StatusText => View.StatusText;
    public string StatusReason => View.Update?.Reason ?? (View.NewerLocal != null ? $"{View.NewerLocal.FileName} is newer" : StatusText);
    public int StatusSort => Status switch
    {
        PackageStatus.UpdateAvailable or PackageStatus.UpdateDownloaded => 0,
        PackageStatus.ProbableUpdate => 1,
        PackageStatus.NoLongerListed or PackageStatus.ArchiveMissing or PackageStatus.Unreadable => 2,
        PackageStatus.UpToDate => 4,
        _ => 3,
    };

    public bool? IsInstalled => View.InstallState switch
    {
        InstallState.Installed => true,
        InstallState.NotInstalled => false,
        _ => null,
    };
    public bool AnyInstalled => View.InstallState != InstallState.NotInstalled;
    public bool CanToggle => Package != null || AnyInstalled;

    public string ModIdText => View.ModId?.ToString() ?? "";
    public DateTime? Updated { get; }
    public string UpdatedText => Updated?.ToString("d MMM yyyy") ?? "";

    public IReadOnlyList<UnitItemViewModel> Units { get; }
    public bool HasMultipleUnits => Units.Count > 1;
    public string PartsSummary => Units.Count > 1 ? $"{Units.Count(u => u.IsInstalled)} of {Units.Count} parts installed" : "";

    public string? Author => Units.Select(u => u.Unit.Info?.Author).FirstOrDefault(a => a != null) ?? View.NexusInfo?.Author;
    public string Byline => string.Join(" · ", new[] { Author is { } a ? "by " + a : null, Version.Length > 0 ? "v" + Version.TrimStart('v', 'V') : null }.Where(s => s != null));

    public string? Description
    {
        get
        {
            var local = Units.Count == 1 ? Units[0].Unit.Info?.Description : null;
            return local ?? View.NexusInfo?.Summary?.Replace("<br />", "\n");
        }
    }

    public bool CanUpdate => View.CanUpdate && Package != null;
    public string UpdateButtonText => Status switch
    {
        PackageStatus.UpdateDownloaded when View.NewerLocal == Package => $"Update parts to {View.LatestVersion ?? "newest"}",
        PackageStatus.UpdateDownloaded => $"Switch to {View.LatestVersion ?? "newer copy"}",
        _ => $"Update to {View.LatestVersion ?? "latest"}",
    };
    public string ToggleButtonText => AnyInstalled ? "Uninstall" : "Install";

    public string? NexusUrl => View.ModId is { } id ? $"https://www.nexusmods.com/{NexusClient.GameDomain}/mods/{id}" : null;
    public bool IgnoreUpdates => View.Link?.IgnoreUpdates == true;

    public IReadOnlyList<ModPackage> OtherVersions => View.OtherVersions;
    public IReadOnlyList<ModPackage> RemovableVersions =>
        View.OtherVersions.Where(p => !_manager.State.InstalledFrom(p.FileName).Any()).ToList();

    public string ArchiveName => Package?.FileName ?? "(archive missing)";
    public string SizeText => Package is { IsFolder: false } p ? FormatSize(p.Size) : "";
    public int FileCount => View.Installed.Sum(m => m.Files.Count) is var n and > 0 ? n : Units.Sum(u => u.Unit.Files.Count);
    public string MatchText => View.Link?.MatchedBy switch
    {
        "md5" => "Matched on Nexus by file hash",
        "name" => "Matched on Nexus by file name",
        "download" or "nxm" => "Downloaded from Nexus",
        _ => View.ModId != null ? "Nexus mod ID from file name" : "No Nexus link",
    };

    public IEnumerable<string> FileList => View.Installed.Count > 0
        ? View.Installed.SelectMany(m => m.Files.Select(f => f.Path)).Order(StringComparer.OrdinalIgnoreCase)
        : Units.SelectMany(u => u.Unit.Files.Select(f => f.Target)).Order(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private ImageSource? _screenshot;

    private bool _screenshotRequested;

    public async Task LoadScreenshotAsync()
    {
        if (_screenshotRequested) return;
        _screenshotRequested = true;
        var package = Package;
        var path = Units.Select(u => u.Unit.ScreenshotPath).FirstOrDefault(p => p != null);
        if (package != null && path != null)
        {
            var image = await Task.Run(() =>
            {
                try
                {
                    var bytes = package.OpenSource().ReadFile(path, 12 * 1024 * 1024);
                    return bytes == null ? null : Decode(new MemoryStream(bytes));
                }
                catch (Exception) { return null; }
            });
            if (image != null) { Screenshot = image; return; }
        }
        if (View.NexusInfo?.PictureUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.DecodePixelWidth = 760;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            Screenshot = bitmap;
        }
    }

    private static BitmapImage? Decode(Stream stream)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = stream;
        bitmap.DecodePixelWidth = 760;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
