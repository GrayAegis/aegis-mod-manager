using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DD2ModManager.Core.Nexus;
using DD2ModManager.Core.State;
using DD2ModManager.Core.Util;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace DD2ModManager.Core.Install;

/// <summary>A REFramework nightly build: "nightly-01433-d4611aef…".</summary>
public sealed record REFrameworkBuild(int? Number, string Commit, DateTimeOffset? Published, string? DownloadUrl = null, long? Size = null)
{
    public string ShortCommit => Commit.Length > 7 ? Commit[..7] : Commit;
    public string Label => Number is { } n ? $"nightly {n:D5}" : $"build {ShortCommit}";
}

/// <param name="Installed">Null when REFramework is installed but the build can't be identified yet.</param>
/// <param name="BuildsBehind">How many nightlies newer than the installed one, when both appear in the release list.</param>
public sealed record REFrameworkStatus(bool IsInstalled, REFrameworkBuild? Installed, REFrameworkBuild? Latest, int? BuildsBehind)
{
    public bool UpdateAvailable => Latest != null && (!IsInstalled || Installed == null ||
                                                      !Installed.Commit.Equals(Latest.Commit, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Checks praydog/REFramework-nightly on GitHub and updates the game's dinput8.dll. The monolithic REFramework.zip
/// covers every RE Engine game, and its instructions are to extract only dinput8.dll unless you use VR, so that is
/// the only file this touches. The replaced DLL is kept for restoring.
/// </summary>
public sealed partial class REFrameworkService(HttpMessageHandler? handler = null) : IDisposable
{
    private const string ReleasesUrl = "https://api.github.com/repos/praydog/REFramework-nightly/releases?per_page=100";
    private const string AssetName = "REFramework.zip";
    private const string DllName = "dinput8.dll";
    private const int BackupsKept = 3;

    [GeneratedRegex(@"^nightly-(\d+)-([0-9a-f]{7,40})$", RegexOptions.IgnoreCase)]
    private static partial Regex NightlyTag();

    [GeneratedRegex(@"Commit hash:\s*([0-9a-f]{7,40})", RegexOptions.IgnoreCase)]
    private static partial Regex LogCommit();

    private readonly HttpClient _http = CreateClient(handler);

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{NexusClient.AppName}/{NexusClient.AppVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Recent nightlies, newest first. One unauthenticated GitHub request.</summary>
    public async Task<IReadOnlyList<REFrameworkBuild>> GetReleasesAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(ReleasesUrl, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub returned {(int)response.StatusCode} for REFramework releases");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var builds = new List<REFrameworkBuild>();
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var match = NightlyTag().Match(release.GetProperty("tag_name").GetString() ?? "");
            if (!match.Success) continue;
            string? url = null;
            long? size = null;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                url = asset.GetProperty("browser_download_url").GetString();
                size = asset.GetProperty("size").GetInt64();
            }
            DateTimeOffset? published = release.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetDateTimeOffset() : null;
            builds.Add(new REFrameworkBuild(int.Parse(match.Groups[1].Value), match.Groups[2].Value.ToLowerInvariant(), published, url, size));
        }
        return builds.OrderByDescending(b => b.Number).ToList();
    }

    /// <summary>
    /// Which build the game has: the one this app installed if the DLL still matches it, otherwise the commit
    /// REFramework wrote to its log, as long as the game has run since the DLL last changed.
    /// </summary>
    public static string? InstalledCommit(GameFolder game, REFrameworkRecord? record)
    {
        var dll = Path.Combine(game.Root, DllName);
        if (!File.Exists(dll)) return null;
        if (record != null && Sha256Of(dll) == record.DllSha256) return record.Commit;
        var log = Path.Combine(game.Root, "re2_framework_log.txt");
        if (!File.Exists(log) || File.GetLastWriteTimeUtc(log) < File.GetLastWriteTimeUtc(dll)) return null;
        using var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        for (var i = 0; i < 40 && reader.ReadLine() is { } line; i++)
            if (LogCommit().Match(line) is { Success: true } m) return m.Groups[1].Value.ToLowerInvariant();
        return null;
    }

    public static REFrameworkStatus Evaluate(GameFolder game, REFrameworkRecord? record, IReadOnlyList<REFrameworkBuild> releases)
    {
        var latest = releases.FirstOrDefault(b => b.DownloadUrl != null);
        if (!File.Exists(Path.Combine(game.Root, DllName))) return new REFrameworkStatus(false, null, latest, null);
        var commit = InstalledCommit(game, record);
        if (commit == null) return new REFrameworkStatus(true, null, latest, null);

        var known = releases.FirstOrDefault(b => b.Commit.StartsWith(commit, StringComparison.OrdinalIgnoreCase) ||
                                                 commit.StartsWith(b.Commit, StringComparison.OrdinalIgnoreCase));
        var installed = known ?? new REFrameworkBuild(record?.Commit == commit ? record.Number : null, commit, null);
        int? behind = known != null && latest != null ? releases.TakeWhile(b => b != known).Count(b => b.DownloadUrl != null) : null;
        return new REFrameworkStatus(true, installed, latest, behind);
    }

    /// <summary>
    /// Downloads <paramref name="build"/>, backs up the current dinput8.dll and puts the new one in place.
    /// The game must be closed (it keeps the DLL open).
    /// </summary>
    public async Task<REFrameworkRecord> InstallAsync(GameFolder game, REFrameworkBuild build, string? currentCommit,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        if (build.DownloadUrl == null) throw new InvalidOperationException($"{build.Label} has no {AssetName}");
        var work = Path.Combine(Path.GetTempPath(), "DD2ModManager", "reframework", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            progress?.Report(new OperationProgress($"Downloading REFramework {build.Label}…"));
            var zip = Path.Combine(work, AssetName);
            using (var response = await _http.GetAsync(build.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var output = File.Create(zip);
                await response.Content.CopyToAsync(output, ct);
            }

            var newDll = Path.Combine(work, DllName);
            using (var archive = ArchiveFactory.OpenArchive(zip, new ReaderOptions()))
            {
                var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory &&
                    PathUtil.Comparer.Equals(PathUtil.FileName(PathUtil.NormalizeRelative(e.Key ?? "") ?? ""), DllName))
                    ?? throw new InvalidOperationException($"{AssetName} doesn't contain {DllName}");
                await using var output = File.Create(newDll);
                await using var input = entry.OpenEntryStream();
                await input.CopyToAsync(output, ct);
            }

            var target = Path.Combine(game.Root, DllName);
            if (File.Exists(target)) SaveBackup(target, currentCommit);
            progress?.Report(new OperationProgress($"Installing REFramework {build.Label}…"));
            File.Copy(newDll, target, overwrite: true);
            Log.Info($"Installed REFramework {build.Label} ({build.Commit})");
            return new REFrameworkRecord { Commit = build.Commit, Number = build.Number, DllSha256 = Sha256Of(target) };
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    public sealed record Backup(string Folder, string? Commit);

    /// <summary>Kept DLLs, newest first. Folder names start with a sortable timestamp.</summary>
    public static IReadOnlyList<Backup> Backups()
    {
        if (!Directory.Exists(AppPaths.REFrameworkBackups)) return [];
        return Directory.GetDirectories(AppPaths.REFrameworkBackups)
            .Where(d => File.Exists(Path.Combine(d, DllName)))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Select(d =>
            {
                var commitFile = Path.Combine(d, "commit.txt");
                var commit = File.Exists(commitFile) ? File.ReadAllText(commitFile).Trim() : "";
                return new Backup(d, commit.Length == 0 ? null : commit);
            })
            .ToList();
    }

    /// <summary>Puts the newest kept DLL back, keeping the current one as a backup so this can be undone too.</summary>
    public static REFrameworkRecord? RestorePrevious(GameFolder game, string? currentCommit)
    {
        var latest = Backups().FirstOrDefault() ?? throw new InvalidOperationException("There's no earlier REFramework build to restore.");
        var target = Path.Combine(game.Root, DllName);
        var staged = Path.Combine(Path.GetTempPath(), "DD2ModManager", $"dinput8-{Guid.NewGuid():N}.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(Path.Combine(latest.Folder, DllName), staged);
        if (File.Exists(target)) SaveBackup(target, currentCommit);
        File.Move(staged, target, overwrite: true);
        if (Directory.Exists(latest.Folder)) Directory.Delete(latest.Folder, recursive: true);
        Log.Info($"Restored REFramework {latest.Commit ?? "build"} from backup");
        return latest.Commit is { } commit ? new REFrameworkRecord { Commit = commit, DllSha256 = Sha256Of(target) } : null;
    }

    private static void SaveBackup(string dll, string? commit)
    {
        var folder = Path.Combine(AppPaths.REFrameworkBackups, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{(commit is { Length: >= 7 } c ? c[..7] : "unknown")}");
        Directory.CreateDirectory(folder);
        File.Copy(dll, Path.Combine(folder, DllName), overwrite: true);
        if (commit != null) File.WriteAllText(Path.Combine(folder, "commit.txt"), commit);
        foreach (var old in Backups().Skip(BackupsKept)) Directory.Delete(old.Folder, recursive: true);
        Log.Info($"Backed up REFramework {(commit is { } x ? x[..Math.Min(7, x.Length)] : "build")} to {folder}");
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public void Dispose() => _http.Dispose();
}
