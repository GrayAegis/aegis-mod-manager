using System.Diagnostics;

namespace DD2ModManager.Core.Install;

/// <summary>The Dragon's Dogma 2 install folder plus the REFramework settings mods depend on.</summary>
public sealed class GameFolder(string root)
{
    public const string ExeName = "DD2.exe";
    public const int SteamAppId = 2054970;

    public string Root { get; } = Path.GetFullPath(root);

    public bool IsValid => File.Exists(Path.Combine(Root, ExeName));
    public bool IsRunning => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)).Length > 0;
    public bool HasREFramework => File.Exists(Path.Combine(Root, "dinput8.dll")) && Directory.Exists(Path.Combine(Root, "reframework"));

    /// <summary>REFramework's "Alternate PAK folder" option, which DD2 needs to load pak_mods.</summary>
    public bool? PakFolderLoadingEnabled => ReadFrameworkFlag("IntegrityCheckBypass_LoadPakDirectory");
    public bool? LooseFileLoaderEnabled => ReadFrameworkFlag("LooseFileLoader_Enabled");

    private bool? ReadFrameworkFlag(string key)
    {
        var config = Path.Combine(Root, "re2_fw_config.txt");
        if (!File.Exists(config)) return null;
        foreach (var line in File.ReadLines(config))
        {
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }
}
