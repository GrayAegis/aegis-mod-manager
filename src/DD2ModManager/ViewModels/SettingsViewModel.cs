using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DD2ModManager.Core;
using DD2ModManager.Core.Fluffy;
using DD2ModManager.Core.Install;
using DD2ModManager.Core.Nexus;
using DD2ModManager.Services;

namespace DD2ModManager.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    public const string ApiKeyPage = "https://www.nexusmods.com/users/myaccount?tab=api";

    private readonly ModManager _manager;

    public SettingsViewModel(ModManager manager)
    {
        _manager = manager;
        var s = manager.Settings;
        _gamePath = s.GamePath ?? "";
        _libraryPath = s.LibraryPath ?? manager.LibraryPath;
        _fluffyPath = s.FluffyPath ?? "";
        _checkOnStartup = s.CheckUpdatesOnStartup;
        _autoInstall = s.AutoInstallUpdates;
        _recycle = s.RecycleReplacedArchives;
        _keyStatus = manager.HasApiKey ? "A key is saved. Paste a new one to replace it." : "No key saved yet.";
        RefreshNxm();
    }

    public event Action<bool>? CloseRequested;

    [ObservableProperty] private string _gamePath;
    [ObservableProperty] private string _libraryPath;
    [ObservableProperty] private string _fluffyPath;
    [ObservableProperty] private bool _checkOnStartup;
    [ObservableProperty] private bool _autoInstall;
    [ObservableProperty] private bool _recycle;
    [ObservableProperty] private string _keyStatus;
    [ObservableProperty] private string _nxmStatus = "";
    [ObservableProperty] private bool _nxmIsOurs;
    [ObservableProperty] private bool _canRestoreNxm;

    /// <summary>Set from the PasswordBox; never bound so the key isn't kept in a dependency property.</summary>
    public string NewApiKey { get; set; } = "";

    public bool ImportRequested { get; private set; }

    public string GameStatus => new GameFolder(GamePath) is { IsValid: true } g
        ? g.HasREFramework ? "DD2.exe found · REFramework installed" : "DD2.exe found · REFramework not installed"
        : "DD2.exe not found in this folder";

    public bool CanImportFluffy => !string.IsNullOrWhiteSpace(FluffyPath) && File.Exists(FluffyImport.InstalledIni(FluffyPath));

    partial void OnGamePathChanged(string value) => OnPropertyChanged(nameof(GameStatus));
    partial void OnFluffyPathChanged(string value) => OnPropertyChanged(nameof(CanImportFluffy));

    [RelayCommand]
    private void BrowseGame() => GamePath = PickFolder("Dragon's Dogma 2 folder (contains DD2.exe)", GamePath) ?? GamePath;

    [RelayCommand]
    private void BrowseLibrary() => LibraryPath = PickFolder("Folder where mod archives are kept", LibraryPath) ?? LibraryPath;

    [RelayCommand]
    private void BrowseFluffy() => FluffyPath = PickFolder("Fluffy Mod Manager folder (contains Modmanager.exe)", FluffyPath) ?? FluffyPath;

    [RelayCommand]
    private void OpenKeyPage() => ShellOps.Open(ApiKeyPage);

    [RelayCommand]
    private async Task TestKey()
    {
        var key = NewApiKey.Trim().Length > 0 ? NewApiKey.Trim() : _manager.Settings.NexusApiKey;
        if (string.IsNullOrEmpty(key))
        {
            KeyStatus = "Paste your API key first.";
            return;
        }
        KeyStatus = "Checking…";
        try
        {
            using var client = new NexusClient(key);
            var user = await client.ValidateAsync();
            KeyStatus = $"Connected as {user.Name} ({(user.IsPremium ? "Premium: updates download automatically" : "Free: use \"Mod Manager Download\" on Nexus")})";
        }
        catch (Exception ex)
        {
            KeyStatus = ex.Message;
        }
    }

    [RelayCommand]
    private void RegisterNxm()
    {
        var previous = NxmProtocol.Register();
        if (previous != null) _manager.Settings.PreviousNxmHandler = previous;
        _manager.SaveSettings();
        RefreshNxm();
    }

    [RelayCommand]
    private void RestoreNxm()
    {
        if (_manager.Settings.PreviousNxmHandler is not { } previous) return;
        NxmProtocol.Restore(previous);
        _manager.Settings.PreviousNxmHandler = null;
        _manager.SaveSettings();
        RefreshNxm();
    }

    [RelayCommand]
    private void ImportFluffy()
    {
        ImportRequested = true;
        _manager.State.FluffyImported = false;
        Save();
    }

    [RelayCommand]
    private void Save()
    {
        var s = _manager.Settings;
        s.GamePath = string.IsNullOrWhiteSpace(GamePath) ? null : GamePath.Trim();
        s.LibraryPath = string.IsNullOrWhiteSpace(LibraryPath) ? null : LibraryPath.Trim();
        s.FluffyPath = string.IsNullOrWhiteSpace(FluffyPath) ? null : FluffyPath.Trim();
        s.CheckUpdatesOnStartup = CheckOnStartup;
        s.AutoInstallUpdates = AutoInstall;
        s.RecycleReplacedArchives = Recycle;
        if (NewApiKey.Trim().Length > 0) s.NexusApiKey = NewApiKey.Trim();
        _manager.SaveSettings();
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void ClearKey()
    {
        _manager.Settings.NexusApiKey = null;
        _manager.SaveSettings();
        KeyStatus = "Key removed.";
    }

    private void RefreshNxm()
    {
        NxmStatus = NxmProtocol.Describe();
        NxmIsOurs = NxmProtocol.IsOurs;
        CanRestoreNxm = NxmIsOurs && _manager.Settings.PreviousNxmHandler != null;
    }

    private static string? PickFolder(string title, string current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
