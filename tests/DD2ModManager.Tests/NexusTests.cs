using System.Net;
using System.Text;
using DD2ModManager.Core.Fluffy;
using DD2ModManager.Core.Nexus;

namespace DD2ModManager.Tests;

public class UpdateResolverTests
{
    private static NexusFile File(int id, string name, string version, string category, long uploaded) =>
        new() { FileId = id, Name = name, Version = version, CategoryName = category, UploadedTimestamp = uploaded };

    [Fact]
    public void FollowsTheAuthorsUpdateChainToTheEnd()
    {
        var mod = new NexusModFiles
        {
            Files = [File(1, "Bestiary", "2.8.8", "OLD_VERSION", 100), File(2, "Bestiary", "2.8.9", "OLD_VERSION", 200), File(3, "Bestiary", "2.8.11", "MAIN", 300)],
            FileUpdates = [new() { OldFileId = 1, NewFileId = 2, UploadedTimestamp = 200 }, new() { OldFileId = 2, NewFileId = 3, UploadedTimestamp = 300 }],
        };
        var r = UpdateResolver.Resolve(1, mod);
        Assert.Equal(UpdateStatus.UpdateAvailable, r.Status);
        Assert.Equal(3, r.Latest!.FileId);
    }

    [Fact]
    public void ChainCyclesDoNotHang()
    {
        var mod = new NexusModFiles
        {
            Files = [File(1, "A", "1", "MAIN", 100)],
            FileUpdates = [new() { OldFileId = 1, NewFileId = 2 }, new() { OldFileId = 2, NewFileId = 1 }],
        };
        Assert.Equal(UpdateStatus.UpToDate, UpdateResolver.Resolve(1, mod).Status);
    }

    [Fact]
    public void LatestMainFileIsUpToDateEvenWithOtherFilesAround()
    {
        var mod = new NexusModFiles { Files = [File(1, "Bestiary", "2.8.11", "MAIN", 300), File(2, "Bestiary Variant Spawns", "2.8.6", "OPTIONAL", 400)] };
        Assert.Equal(UpdateStatus.UpToDate, UpdateResolver.Resolve(1, mod).Status);
    }

    [Fact]
    public void NewerSameNameUploadCountsAsUpdate()
    {
        var mod = new NexusModFiles { Files = [File(1, "Guild Contracts 4.0.2", "4.0.2", "MAIN", 100), File(2, "Guild Contracts 4.1.1", "4.1.1", "MAIN", 200)] };
        var r = UpdateResolver.Resolve(1, mod);
        Assert.Equal(UpdateStatus.UpdateAvailable, r.Status);
        Assert.Equal("4.1.1", r.Latest!.Version);
    }

    [Fact]
    public void DemotedFileWithUnrelatedMainFileIsOnlyAProbableUpdate()
    {
        var mod = new NexusModFiles { Files = [File(1, "Old Thing", "1.0", "OLD_VERSION", 100), File(2, "Totally Renamed", "2.0", "MAIN", 200)] };
        var r = UpdateResolver.Resolve(1, mod);
        Assert.Equal(UpdateStatus.ProbableUpdate, r.Status);
        Assert.Equal(2, r.Latest!.FileId);
    }

    [Fact]
    public void DeletedFileWithNoSuccessorIsNoLongerListed()
    {
        var mod = new NexusModFiles { Files = [File(5, "A", "1", "MAIN", 100), File(6, "B", "1", "MAIN", 100)] };
        Assert.Equal(UpdateStatus.NoLongerListed, UpdateResolver.Resolve(99, mod).Status);
    }
}

public class NxmLinkTests
{
    [Fact]
    public void ParsesModManagerDownloadLink()
    {
        Assert.True(NxmLink.TryParse("nxm://dragonsdogma2/mods/1196/files/4321?key=abc%2Bdef&expires=1761500000&user_id=42", out var link));
        Assert.Equal(new NxmLink("dragonsdogma2", 1196, 4321, "abc+def", 1761500000), link);
    }

    [Theory]
    [InlineData("https://www.nexusmods.com/dragonsdogma2/mods/1196")]
    [InlineData("nxm://dragonsdogma2/collections/abc/revisions/3")]
    [InlineData("")]
    public void RejectsOtherLinks(string text) => Assert.False(NxmLink.TryParse(text, out _));
}

public class FluffyImportTests
{
    [Fact]
    public void ReadsInstalledIni()
    {
        using var dir = new TempDir();
        var ini = dir.Write("installed.ini", """
            GameInstallService=Steam
            GameInstallPath=C:\Games\Steam\steamapps\common\Dragons Dogma 2
            [DD2 Method Library 1574 1.03 2026-10-16T06-11Z H0NfC0aLQ]
            ModID_v1=10888805463741980890
            ModName=DD2 Method Library
            installtype=normal
            file=reframework\plugins\DD2MethodLibrary.dll
            [Durnehviir]
            ModName=Durnehviir
            file=pak_mods\x0003-Durnehviir.pak
            """);
        var install = FluffyImport.ReadInstalled(ini);
        Assert.Equal(@"C:\Games\Steam\steamapps\common\Dragons Dogma 2", install.GamePath);
        Assert.Equal(2, install.Entries.Count);
        Assert.Equal("DD2 Method Library", install.Entries[0].ModName);
        Assert.Equal(["reframework/plugins/DD2MethodLibrary.dll"], install.Entries[0].Files);
        Assert.Equal("Durnehviir.pak", FluffyImport.StripPakPrefix("x0003-Durnehviir.pak"));
    }
}

public class NexusClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        r.Headers.Add("x-rl-daily-remaining", "2400");
        r.Headers.Add("x-rl-hourly-remaining", "99");
        return r;
    }

    [Fact]
    public async Task SendsKeyAndAppHeadersAndParsesFiles()
    {
        var handler = new FakeHandler(_ => Json("""
            {"files":[{"file_id":9,"name":"Bestiary","version":"2.8.11","category_id":1,"category_name":"MAIN","file_name":"Bestiary-1196-2-8-11-1.zip","uploaded_timestamp":300,"size_in_bytes":456169}],
             "file_updates":[{"old_file_id":8,"new_file_id":9,"old_file_name":"a","new_file_name":"b","uploaded_timestamp":300}]}
            """));
        using var client = new NexusClient("secret", handler);
        var files = await client.GetModFilesAsync(1196);

        var request = handler.Requests.Single();
        Assert.Equal("https://api.nexusmods.com/v1/games/dragonsdogma2/mods/1196/files.json", request.RequestUri!.ToString());
        Assert.Equal("secret", request.Headers.GetValues("apikey").Single());
        Assert.Equal("DD2ModManager", request.Headers.GetValues("Application-Name").Single());
        Assert.Equal("MAIN", files.Files.Single().CategoryName);
        Assert.Equal(456169, files.Files.Single().SizeInBytes);
        Assert.Equal(9, files.FileUpdates.Single().NewFileId);
        Assert.Equal(2400, client.DailyRemaining);
        Assert.Equal(99, client.HourlyRemaining);
    }

    [Fact]
    public async Task Md5MissReturnsEmpty()
    {
        using var client = new NexusClient("k", new FakeHandler(_ => Json("""{"message":"No file found"}""", HttpStatusCode.NotFound)));
        Assert.Empty(await client.Md5SearchAsync("d41d8cd98f00b204e9800998ecf8427e"));
    }

    [Fact]
    public async Task RateLimitSurfacesAsTypedError()
    {
        using var client = new NexusClient("k", new FakeHandler(_ => Json("""{"message":"Too many"}""", HttpStatusCode.TooManyRequests)));
        await Assert.ThrowsAsync<NexusRateLimitException>(() => client.GetModAsync(1));
    }

    [Fact]
    public async Task DownloadLinkPassesNxmKeyAndExpiry()
    {
        var handler = new FakeHandler(_ => Json("""[{"name":"Nexus CDN","short_name":"Nexus CDN","URI":"https://cdn.example/file.zip"}]"""));
        using var client = new NexusClient("k", handler);
        var links = await client.GetDownloadLinksAsync(1196, 9, "a+b", 1761500000);

        Assert.Equal("https://cdn.example/file.zip", links.Single().Uri);
        Assert.EndsWith("/mods/1196/files/9/download_link.json?key=a%2Bb&expires=1761500000", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task DownloadDoesNotSendApiKeyToCdn()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var client = new NexusClient("secret", handler);
        var target = Path.Combine(dir.Path, "file.zip");
        await client.DownloadAsync(new Uri("https://cdn.example/file.zip"), target);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.False(handler.Requests.Single().Headers.Contains("apikey"));
    }
}
