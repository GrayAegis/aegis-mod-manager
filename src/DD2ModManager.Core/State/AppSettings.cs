using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DD2ModManager.Core.State;

public sealed class AppSettings
{
    public string? GamePath { get; set; }
    public string? LibraryPath { get; set; }
    public string? FluffyPath { get; set; }

    /// <summary>The Nexus API key, encrypted with Windows DPAPI for the current user.</summary>
    public string? NexusApiKeyProtected { get; set; }

    public bool CheckUpdatesOnStartup { get; set; } = true;
    /// <summary>Download and install confident updates without asking.</summary>
    public bool AutoInstallUpdates { get; set; }
    /// <summary>
    /// How many replaced versions of each mod to keep for one-click rollback. Older ones go to the Recycle Bin;
    /// 0 sends every replaced archive there straight away.
    /// </summary>
    public int KeepPreviousVersions { get; set; } = 2;
    public bool HideOlderVersions { get; set; } = true;
    /// <summary>The nxm:// handler that was registered before this app took over (e.g. Vortex), for restoring.</summary>
    public string? PreviousNxmHandler { get; set; }

    private static readonly byte[] Entropy = "DD2ModManager.Nexus"u8.ToArray();

    [JsonIgnore]
    public string? NexusApiKey
    {
        get
        {
            if (string.IsNullOrEmpty(NexusApiKeyProtected)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(NexusApiKeyProtected), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (CryptographicException) { return null; }
            catch (FormatException) { return null; }
        }
        set => NexusApiKeyProtected = string.IsNullOrWhiteSpace(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser));
    }
}

public static class AppPaths
{
    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DD2ModManager");

    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string State => Path.Combine(DataDir, "state.json");
    public static string NexusCache => Path.Combine(DataDir, "nexus-cache.json");
    public static string Log => Path.Combine(DataDir, "log.txt");
    public static string Backup => Path.Combine(DataDir, "Backup");
    /// <summary>Archives replaced by updates, kept for rollback. Outside the library so they don't show up as mods.</summary>
    public static string Previous => Path.Combine(DataDir, "Previous");
    public static string REFrameworkBackups => Path.Combine(DataDir, "REFramework");
    public static string DefaultLibrary => Path.Combine(DataDir, "Mods");
    public static string Downloads => Path.Combine(Path.GetTempPath(), "DD2ModManager", "downloads");
    public static string Extract => Path.Combine(Path.GetTempPath(), "DD2ModManager", "extract");
}
