using System.IO.Compression;
using System.Net;
using System.Text;
using DD2ModManager.Core;
using DD2ModManager.Core.Install;
using DD2ModManager.Core.State;

namespace DD2ModManager.Tests;

[Collection("AppPaths")]
public class REFrameworkTests : IDisposable
{
    private const string OldCommit = "0a07faae46a6fc1c51cd1699a0cf99a3f9c2679f";
    private const string MidCommit = "d052e8fd3fd41d0a1a33394fcf6e6c19be39b1ee";
    private const string NewCommit = "d4611aef1d63effd3a736bdf9404ea702428017d";

    private readonly TempDir _dir = new();
    private readonly string _game;
    private readonly GitHub _github = new();

    public REFrameworkTests()
    {
        _game = Fixtures.MakeGame(_dir);
        AppPaths.DataDir = _dir.Sub("data");
        JsonStore.Save(AppPaths.Settings, new AppSettings { GamePath = _game, LibraryPath = _dir.Sub("library"), FluffyPath = _dir.Path });
        JsonStore.Save(AppPaths.State, new AppState { FluffyImported = true });
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>Serves a release list shaped like praydog/REFramework-nightly and the REFramework.zip download.</summary>
    private sealed class GitHub : HttpMessageHandler
    {
        public int Downloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("api.github.com"))
            {
                var json = $$"""
                    [{"tag_name":"nightly-01433-{{NewCommit}}","published_at":"2026-10-21T04:37:39Z","assets":[
                        {"name":"csharp-api.zip","size":5,"browser_download_url":"https://example/csharp-api.zip"},
                        {"name":"REFramework.zip","size":12,"browser_download_url":"https://example/1433/REFramework.zip"}]},
                     {"tag_name":"nightly-01432-{{MidCommit}}","published_at":"2026-10-11T01:59:23Z","assets":[
                        {"name":"REFramework.zip","size":12,"browser_download_url":"https://example/1432/REFramework.zip"}]},
                     {"tag_name":"nightly-01422-{{OldCommit}}","published_at":"2026-09-12T11:47:25Z","assets":[
                        {"name":"REFramework.zip","size":12,"browser_download_url":"https://example/1422/REFramework.zip"}]},
                     {"tag_name":"not-a-nightly","assets":[]}]
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            Downloads++;
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in new[] { ("dinput8.dll", "dll 1433"), ("openvr_api.dll", "vr") })
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write(content);
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) });
        }
    }

    private string Dll => Path.Combine(_game, "dinput8.dll");

    /// <summary>A REFramework install whose log (written after the DLL) names <paramref name="commit"/>.</summary>
    private void InstallFramework(string commit, bool logIsStale = false)
    {
        Directory.CreateDirectory(Path.Combine(_game, "reframework"));
        File.WriteAllText(Dll, "dll " + commit[..7]);
        var log = Path.Combine(_game, "re2_framework_log.txt");
        File.WriteAllText(log, $"[info] REFramework entry\n[info] Commit hash: {commit}\n[info] Tag: v1.5.9.1\n");
        var dllTime = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Dll, dllTime);
        File.SetLastWriteTimeUtc(log, logIsStale ? dllTime.AddDays(-1) : dllTime.AddDays(1));
    }

    private async Task<ModManager> NewManager()
    {
        var manager = new ModManager { REFrameworkHandler = _github };
        manager.Load();
        manager.Refresh();
        await manager.CheckREFrameworkAsync();
        return manager;
    }

    [Fact]
    public async Task ReadsNightliesAndTheirZip()
    {
        using var service = new REFrameworkService(_github);
        var builds = await service.GetReleasesAsync();
        Assert.Equal([1433, 1432, 1422], builds.Select(b => b.Number));
        Assert.Equal("https://example/1433/REFramework.zip", builds[0].DownloadUrl);
        Assert.Equal("nightly 01433", builds[0].Label);
    }

    [Fact]
    public async Task IdentifiesTheInstalledBuildFromTheLog()
    {
        InstallFramework(OldCommit);
        var status = (await NewManager()).REFramework!;
        Assert.Equal(1422, status.Installed!.Number);
        Assert.Equal(1433, status.Latest!.Number);
        Assert.Equal(2, status.BuildsBehind);
        Assert.True(status.UpdateAvailable);
    }

    [Fact]
    public async Task ALogOlderThanTheDllDoesNotCount()
    {
        InstallFramework(OldCommit, logIsStale: true);
        var status = (await NewManager()).REFramework!;
        Assert.True(status.IsInstalled);
        Assert.Null(status.Installed);
    }

    [Fact]
    public async Task ReportsMissingREFramework()
    {
        var status = (await NewManager()).REFramework!;
        Assert.False(status.IsInstalled);
        Assert.True(status.UpdateAvailable);
    }

    [Fact]
    public async Task UpdateReplacesOnlyTheDllAndKeepsTheOldOne()
    {
        InstallFramework(OldCommit);
        var manager = await NewManager();
        await manager.UpdateREFrameworkAsync();

        Assert.Equal("dll 1433", File.ReadAllText(Dll));
        Assert.False(File.Exists(Path.Combine(_game, "openvr_api.dll")));
        var backup = REFrameworkService.Backups().Single();
        Assert.Equal(OldCommit, backup.Commit);
        Assert.Equal("dll 0a07faa", File.ReadAllText(Path.Combine(backup.Folder, "dinput8.dll")));

        // The game hasn't run since, so the stale log still says 01422; the DLL hash identifies the new build.
        Assert.Equal(1433, manager.REFramework!.Installed!.Number);
        Assert.False(manager.REFramework.UpdateAvailable);
        Assert.Equal(NewCommit, (await NewManager()).REFramework!.Installed!.Commit);
    }

    [Fact]
    public async Task RestorePutsThePreviousBuildBack()
    {
        InstallFramework(OldCommit);
        var manager = await NewManager();
        await manager.UpdateREFrameworkAsync();
        Assert.True(manager.CanRestoreREFramework);

        manager.RestoreREFramework();

        Assert.Equal("dll 0a07faa", File.ReadAllText(Dll));
        Assert.Equal(1422, manager.REFramework!.Installed!.Number);
        // The build it replaced is kept in turn, so the restore can be undone.
        Assert.Equal(NewCommit, REFrameworkService.Backups().Single().Commit);
    }
}
