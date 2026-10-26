using System.Net;
using System.Text;
using DD2ModManager.Core;
using DD2ModManager.Core.Nexus;
using DD2ModManager.Core.State;

namespace DD2ModManager.Tests;

public class RequirementClassificationTests
{
    // Notes taken from real Dragon's Dogma 2 mod pages.
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Required", true)]
    [InlineData("Required since v1.5.1 (game patch 3.2)", true)]
    [InlineData("*REQUIRED* for quest journal integration", true)]
    [InlineData("Requires REFramework to run - I always recommend the nightly build", true)]
    [InlineData("Not required, but highly recommended to fix some issues that this mod couldn't cover", false)]
    [InlineData("Not strictly required but you'll get pummeled without it", false)]
    [InlineData("*SOFT REQUIREMENT* Content editor only - and only for the \"head\" item when doing bounty quests.", false)]
    [InlineData("Optional - this allows LeoovayGore to control the head removal for bounties", false)]
    [InlineData("Recommended for the variants to spawn in the monster culling requests", false)]
    [InlineData("Without this, some of our new boss enemies won't have a boss healthbar", false)]
    public void OnlyUnqualifiedRequirementsAreHard(string? notes, bool required) =>
        Assert.Equal(required, new NexusRequirement { ModId = 1311, ModName = "_NickCore", GameId = NexusGraphQl.GameId, Notes = notes }.IsRequired);

    [Fact]
    public void RecognisesBothFormsOfREFramework()
    {
        Assert.True(new NexusRequirement { ModName = "REFramework", ExternalRequirement = true, Url = "https://github.com/praydog/REFramework-nightly" }.IsREFramework);
        Assert.True(new NexusRequirement { ModId = 8, ModName = "REFramework", GameId = NexusGraphQl.GameId }.IsREFramework);
        Assert.False(new NexusRequirement { ModId = 1311, ModName = "_NickCore", GameId = NexusGraphQl.GameId }.IsREFramework);
    }
}

public class NexusGraphQlTests
{
    private sealed class FakeHandler(string body) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task ParsesRequirementsPerMod()
    {
        var handler = new FakeHandler("""
            {"data":{"legacyModsByDomain":{"nodes":[
              {"modId":1196,"modRequirements":{"nexusRequirements":{"nodes":[
                {"modId":"1311","modName":"_NickCore","gameId":"6234","externalRequirement":false,"url":"","notes":""},
                {"modId":"0","modName":"REFramework","gameId":"0","externalRequirement":true,"url":"https://github.com/praydog/REFramework-nightly","notes":"Required"}]}}},
              {"modId":1311,"modRequirements":{"nexusRequirements":{"nodes":[]}}}]}}}
            """);
        using var graph = new NexusGraphQl(handler);
        var result = await graph.GetRequirementsAsync([1196, 1311]);

        Assert.Contains("modId: 1196", handler.RequestBody);
        Assert.Equal(["_NickCore", "REFramework"], result[1196].Select(r => r.ModName));
        Assert.True(result[1196][0].IsSameGame);
        Assert.Empty(result[1311]);
    }

    [Fact]
    public async Task SurfacesGraphQlErrors()
    {
        using var graph = new NexusGraphQl(new FakeHandler("""{"errors":[{"message":"Field 'x' doesn't exist"}]}"""));
        await Assert.ThrowsAsync<NexusApiException>(() => graph.GetRequirementsAsync([1]));
    }
}

public class ChangelogTests
{
    private static NexusFile File(int id, string version, long uploaded, string? changelog, string name = "Bestiary") =>
        new() { FileId = id, Name = name, Version = version, UploadedTimestamp = uploaded, CategoryName = "MAIN", ChangelogHtml = changelog };

    [Fact]
    public void ConvertsNexusHtmlToText() =>
        Assert.Equal("Fixed &crashes\n• one\n• two", Changelog.ToPlainText("Fixed &amp;crashes<br /><ul><li>one</li><li>two</li></ul>"));

    [Fact]
    public void ListsEveryVersionSkippedAlongTheUpdateChain()
    {
        var mod = new NexusModFiles
        {
            Files = [File(1, "2.8.8", 100, "old"), File(2, "2.8.9", 200, "New move for Wyvern"), File(3, "2.8.10", 300, null), File(4, "2.8.11", 400, "Rebalanced Wyrm")],
            FileUpdates = [new() { OldFileId = 1, NewFileId = 2, UploadedTimestamp = 200 }, new() { OldFileId = 2, NewFileId = 3, UploadedTimestamp = 300 },
                           new() { OldFileId = 3, NewFileId = 4, UploadedTimestamp = 400 }],
        };
        var entries = Changelog.Between(mod, 1, 4);
        Assert.Equal(["2.8.11", "2.8.9"], entries.Select(e => e.Version));
    }

    [Fact]
    public void FallsBackToSameNamedFilesWithoutAChain()
    {
        var mod = new NexusModFiles
        {
            Files = [File(1, "1.0", 100, "first"), File(2, "1.1", 200, "second"), File(9, "1.0", 250, "unrelated", name: "Other file"), File(3, "1.2", 300, "third")],
        };
        Assert.Equal(["1.2", "1.1"], Changelog.Between(mod, 1, 3).Select(e => e.Version));
    }
}

[Collection("AppPaths")]
public class DependencyTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _game;
    private readonly string _library;

    public DependencyTests()
    {
        _game = Fixtures.MakeGame(_dir);
        _library = _dir.Sub("library");
        AppPaths.DataDir = _dir.Sub("data");
        JsonStore.Save(AppPaths.Settings, new AppSettings { GamePath = _game, LibraryPath = _library, FluffyPath = _dir.Path });
        JsonStore.Save(AppPaths.State, new AppState { FluffyImported = true });
    }

    public void Dispose() => _dir.Dispose();

    private static NexusRequirement Needs(int modId, string name, string? notes = null) =>
        new() { ModId = modId, ModName = name, GameId = NexusGraphQl.GameId, Notes = notes };

    private ModManager Manager(params (string Archive, (string, string)[] Files)[] archives)
    {
        foreach (var (archive, files) in archives) Fixtures.Zip(Path.Combine(_library, archive), files);
        var manager = new ModManager();
        manager.Load();
        manager.Refresh();
        return manager;
    }

    private const string NickCore = "_NickCore 1311 2.6.2 2026-10-25T12-32Z mue3Uu9c5.zip";
    private const string Bestiary = "Bestiary 1196 2.8.11 2026-10-25T15-30Z lEHTkEUYS.zip";

    private void InstallAll(ModManager manager)
    {
        foreach (var package in manager.Library) manager.Install(package, package.Units, overwriteConflicts: false);
    }

    [Fact]
    public void WarnsBeforeRemovingALibraryAnotherModNeeds()
    {
        var manager = Manager((NickCore, [("reframework/autorun/_NickCore.lua", "n")]), (Bestiary, [("reframework/autorun/Bestiary.lua", "b")]));
        manager.Cache.Requirements[1196] = [Needs(1311, "_NickCore"), Needs(883, "Durnehviir", "Recommended for the variants to spawn")];
        InstallAll(manager);

        var nick = manager.State.InstalledFrom(NickCore).ToList();
        Assert.Equal(["Bestiary needs _NickCore"], manager.DependentsBrokenBy(nick));
        // Removing both together is fine, and removing Bestiary alone breaks nothing.
        Assert.Empty(manager.DependentsBrokenBy(manager.State.Installed.ToList()));
        Assert.Empty(manager.DependentsBrokenBy(manager.State.InstalledFrom(Bestiary).ToList()));

        var bestiaryView = manager.BuildViews(collapseVersions: true).Single(v => v.Name == "Bestiary");
        var nickView = manager.BuildViews(collapseVersions: true).Single(v => v.Name == "_NickCore");
        Assert.Equal(["Bestiary"], manager.RequiredBy(nickView));
        var reqs = manager.RequirementsOf(bestiaryView);
        Assert.Contains(reqs, r => r is { Name: "_NickCore", Required: true, Status: RequirementStatus.Installed });
        Assert.Contains(reqs, r => r is { Name: "Durnehviir", Required: false, Status: RequirementStatus.Missing });
    }

    [Fact]
    public void AnotherInstalledFileOfTheSameModStillCounts()
    {
        var manager = Manager(
            ("Bestiary Variant Spawns 1196 2.8.6 2026-10-17T11-42Z EL0rgL8lS.zip", [("BestiaryVariantSpawns.pak", "p")]),
            (Bestiary, [("reframework/autorun/Bestiary.lua", "b")]),
            ("Apex 1388 1.3.13 2026-10-26T12-53Z LeyDWeng3.zip", [("reframework/autorun/Apex.lua", "a")]));
        manager.Cache.Requirements[1388] = [Needs(1196, "Bestiary - Aggressive Enemies and Variants")];
        InstallAll(manager);

        Assert.Empty(manager.DependentsBrokenBy(manager.State.InstalledFrom("Bestiary Variant Spawns 1196 2.8.6 2026-10-17T11-42Z EL0rgL8lS.zip").ToList()));
        Assert.NotEmpty(manager.DependentsBrokenBy(manager.State.Installed.Where(m => m.PackageFileName!.StartsWith("Bestiary")).ToList()));
    }

    [Fact]
    public void FluffyListedAsARequirementIsSatisfiedByThisApp()
    {
        var manager = Manager(("Fluffy Mod Manager-1-3-027-1711077877.zip", [("Modmanager.exe", "f")]), (NickCore, [("reframework/autorun/_NickCore.lua", "n")]));
        manager.Cache.Requirements[1311] = [Needs(1, "Fluffy Mod Manager")];
        InstallAll(manager);

        Assert.Empty(manager.DependentsBrokenBy(manager.State.InstalledFrom("Fluffy Mod Manager-1-3-027-1711077877.zip").ToList()));
        manager.Uninstall(manager.State.InstalledFrom("Fluffy Mod Manager-1-3-027-1711077877.zip").ToList());
        var nick = manager.Library.Single(p => p.FileName == NickCore);
        Assert.Empty(manager.MissingRequirements(nick, nick.Units));
        var view = manager.BuildViews(collapseVersions: true).Single(v => v.Name == "_NickCore");
        Assert.Contains(manager.RequirementsOf(view), r => r is { IsModManager: true, Status: RequirementStatus.Installed });
    }

    [Fact]
    public void ModinfoAddonsNeedTheirCorePart()
    {
        var manager = Manager(("Content Editor 1031 1.5.8 2026-10-26T04-40Z sZxYOZ7Mx.zip",
        [
            ("content_editor/modinfo.ini", "name=Content editor"), ("content_editor/reframework/autorun/content_editor.lua", "c"),
            ("event_editor/modinfo.ini", "name=Event editor\naddonfor=Content editor"), ("event_editor/reframework/autorun/editor_events.lua", "e"),
        ]));
        InstallAll(manager);
        var core = manager.State.Installed.Single(m => m.UnitId == "content_editor");

        Assert.Equal(["Event editor (Content Editor) needs Content editor"], manager.DependentsBrokenBy([core]));
        Assert.Empty(manager.DependentsBrokenBy(manager.State.Installed.ToList()));
    }

    [Fact]
    public void FlagsMissingRequirementsAndFindsThemInTheLibrary()
    {
        var manager = Manager((NickCore, [("reframework/autorun/_NickCore.lua", "n")]), (Bestiary, [("reframework/autorun/Bestiary.lua", "b")]));
        manager.Cache.Requirements[1196] =
        [
            Needs(1311, "_NickCore"), Needs(30, "_ScriptCore"), Needs(1147, "UDD2P", "Not required, but highly recommended"),
            new NexusRequirement { ModName = "REFramework", ExternalRequirement = true, Url = "https://github.com/praydog/REFramework-nightly" },
        ];
        var bestiary = manager.Library.Single(p => p.FileName == Bestiary);
        manager.Install(bestiary, bestiary.Units, overwriteConflicts: false);

        var missing = manager.MissingRequirements(bestiary, bestiary.Units);
        Assert.Equal(["_NickCore", "_ScriptCore", "REFramework"], missing.Select(m => m.Name));
        Assert.Equal(NickCore, manager.LibraryPackageFor(missing[0])?.FileName);
        Assert.Null(manager.LibraryPackageFor(missing[1]));

        // With REFramework present in the game folder, it no longer counts as missing.
        File.WriteAllText(Path.Combine(_game, "dinput8.dll"), "");
        Directory.CreateDirectory(Path.Combine(_game, "reframework"));
        Assert.DoesNotContain(manager.MissingRequirements(bestiary, bestiary.Units), m => m.Name == "REFramework");
    }
}
