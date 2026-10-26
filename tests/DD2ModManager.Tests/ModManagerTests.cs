using DD2ModManager.Core;
using DD2ModManager.Core.State;

namespace DD2ModManager.Tests;

public class ModManagerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _game;
    private readonly string _library;
    private readonly List<string> _recycled = [];

    public ModManagerTests()
    {
        _game = Fixtures.MakeGame(_dir);
        _library = _dir.Sub("library");
        AppPaths.DataDir = _dir.Sub("data");
        // Pre-seed settings so Load() never goes looking for a real Fluffy or Steam install.
        JsonStore.Save(AppPaths.Settings, new AppSettings { GamePath = _game, LibraryPath = _library, FluffyPath = _dir.Path });
        JsonStore.Save(AppPaths.State, new AppState { FluffyImported = true });
    }

    public void Dispose() => _dir.Dispose();

    private ModManager NewManager()
    {
        var manager = new ModManager
        {
            RecycleFile = path => { _recycled.Add(Path.GetFileName(path)); File.Delete(path); return true; },
        };
        manager.Load();
        manager.Refresh();
        return manager;
    }

    [Fact]
    public async Task UpdatesToNewerLocalCopyKeepingPakSlotAndRecyclingOldArchive()
    {
        Fixtures.Zip(Path.Combine(_library, "Other 50 1.0 2026-10-01T00-00Z aaaaaaaaa.zip"), ("Other.pak", "other"));
        Fixtures.Zip(Path.Combine(_library, "Bestiary 1196 2.8.8 2026-10-20T14-18Z vcRqGcwCv.zip"),
            ("Bestiary.pak", "old pak"), ("reframework/autorun/Bestiary.lua", "old"), ("reframework/autorun/Bestiary/Removed.lua", "gone soon"));
        var manager = NewManager();

        var oldPackage = manager.Library.Single(p => p.FileName.StartsWith("Bestiary"));
        manager.Install(oldPackage, oldPackage.Units, overwriteConflicts: false);
        var other = manager.Library.Single(p => p.FileName.StartsWith("Other"));
        manager.Install(other, other.Units, overwriteConflicts: false);
        Assert.True(File.Exists(Path.Combine(_game, "pak_mods", "x0000-Bestiary.pak")));

        // The newer version lands in the library (as a manual download would).
        Fixtures.Zip(Path.Combine(_library, "Bestiary 1196 2.8.11 2026-10-25T15-30Z lEHTkEUYS.zip"),
            ("Bestiary.pak", "new pak"), ("reframework/autorun/Bestiary.lua", "new"));
        manager.Refresh();

        var view = manager.BuildViews(collapseVersions: true).Single(v => v.Name == "Bestiary");
        Assert.Equal(PackageStatus.UpdateDownloaded, view.Status);
        Assert.Equal(InstallState.Installed, view.InstallState);
        Assert.Equal("2.8.11", view.LatestVersion);
        Assert.True(view.UpdateIsConfident);

        await manager.UpdateAsync(view);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_game, "reframework", "autorun", "Bestiary.lua")));
        Assert.Equal("new pak", File.ReadAllText(Path.Combine(_game, "pak_mods", "x0000-Bestiary.pak")));
        Assert.False(File.Exists(Path.Combine(_game, "reframework", "autorun", "Bestiary", "Removed.lua")));
        Assert.Equal(["Bestiary 1196 2.8.8 2026-10-20T14-18Z vcRqGcwCv.zip"], _recycled);

        var after = manager.BuildViews(collapseVersions: true).Single(v => v.Name == "Bestiary");
        Assert.Equal("2.8.11", after.Version);
        Assert.Equal(InstallState.Installed, after.InstallState);
        Assert.Empty(after.OtherVersions);
    }

    [Fact]
    public async Task PartsSplitAcrossVersionsCountAsInstalledAndUpdateTogether()
    {
        (string, string)[] Parts(string version) =>
        [
            ("content_editor/modinfo.ini", $"name=Content editor\nversion={version}"),
            ("content_editor/reframework/autorun/content_editor.lua", version),
            ("item_editor/modinfo.ini", "name=Item editor"),
            ("item_editor/reframework/autorun/editor_items.lua", version),
        ];
        Fixtures.Zip(Path.Combine(_library, "Content Editor 1031 1.5.6 2026-10-14T06-22Z 9AOxMAVol.zip"), Parts("1.5.6"));
        Fixtures.Zip(Path.Combine(_library, "Content Editor 1031 1.5.8 2026-10-26T04-40Z sZxYOZ7Mx.zip"), Parts("1.5.8"));
        var manager = NewManager();
        var v156 = manager.Library.Single(p => p.FileName.Contains("1.5.6"));
        var v158 = manager.Library.Single(p => p.FileName.Contains("1.5.8"));
        manager.Install(v156, [v156.Units.Single(u => u.Id == "content_editor")], overwriteConflicts: false);
        manager.Install(v158, [v158.Units.Single(u => u.Id == "item_editor")], overwriteConflicts: false);

        var view = manager.BuildViews(collapseVersions: true).Single();
        Assert.Same(v158, view.Package);
        Assert.Equal(InstallState.Installed, view.InstallState);
        Assert.Equal(PackageStatus.UpdateDownloaded, view.Status);
        Assert.StartsWith("Some parts outdated", view.StatusText);

        await manager.UpdateAsync(view);

        Assert.Equal("1.5.8", File.ReadAllText(Path.Combine(_game, "reframework", "autorun", "content_editor.lua")));
        Assert.All(manager.State.Installed, m => Assert.Contains("1.5.8", m.PackageFileName));
        Assert.Contains(_recycled, f => f.Contains("1.5.6"));
        Assert.Equal(PackageStatus.Unchecked, manager.BuildViews(collapseVersions: true).Single().Status);
    }

    [Fact]
    public async Task UpdatingAModThatBundlesAnotherModsFontWorks()
    {
        // Reproduces the Fluffy import: both mods ship the font, but Fluffy recorded it only under Take Notice.
        Fixtures.Zip(Path.Combine(_library, "Take Notice 1696 1.3.2 2026-10-15T12-53Z PjbdQjpJo.zip"),
            ("reframework/fonts/Alegreya.ttf", "font"), ("reframework/autorun/Notices.lua", "notices"));
        Fixtures.Zip(Path.Combine(_library, "Immersive Interactables 1518 1.7.6 2026-10-17T17-50Z ihV1KhUrZ.zip"),
            ("reframework/fonts/Alegreya.ttf", "font"), ("reframework/autorun/Interactables.lua", "old"));
        Directory.CreateDirectory(Path.Combine(_game, "reframework", "fonts"));
        Directory.CreateDirectory(Path.Combine(_game, "reframework", "autorun"));
        File.WriteAllText(Path.Combine(_game, "reframework", "fonts", "Alegreya.ttf"), "font");
        File.WriteAllText(Path.Combine(_game, "reframework", "autorun", "Notices.lua"), "notices");
        File.WriteAllText(Path.Combine(_game, "reframework", "autorun", "Interactables.lua"), "old");
        JsonStore.Save(AppPaths.State, new AppState
        {
            FluffyImported = true,
            Installed =
            [
                new() { UnitId = "Take Notice 1696 1.3.2 2026-10-15T12-53Z PjbdQjpJo", PackageFileName = "Take Notice 1696 1.3.2 2026-10-15T12-53Z PjbdQjpJo.zip",
                        DisplayName = "Take Notice", Files = [new() { Path = "reframework/fonts/Alegreya.ttf" }, new() { Path = "reframework/autorun/Notices.lua" }] },
                new() { UnitId = "Immersive Interactables 1518 1.7.6 2026-10-17T17-50Z ihV1KhUrZ", PackageFileName = "Immersive Interactables 1518 1.7.6 2026-10-17T17-50Z ihV1KhUrZ.zip",
                        DisplayName = "Immersive Interactables", Files = [new() { Path = "reframework/autorun/Interactables.lua" }] },
            ],
        });
        var manager = NewManager();
        Assert.Contains(manager.State.Installed[1].Files, f => f.Path == "reframework/fonts/Alegreya.ttf");

        Fixtures.Zip(Path.Combine(_library, "Immersive Interactables 1518 1.8.1 2026-10-25T14-43Z KHSz1HWgj.zip"),
            ("reframework/fonts/Alegreya.ttf", "font v2"), ("reframework/autorun/Interactables.lua", "new"));
        manager.Refresh();
        await manager.UpdateAsync(manager.BuildViews(collapseVersions: true).Single(v => v.Name == "Immersive Interactables"));

        Assert.Equal("new", File.ReadAllText(Path.Combine(_game, "reframework", "autorun", "Interactables.lua")));
        var notices = manager.State.Installed.Single(m => m.DisplayName == "Take Notice");
        manager.Uninstall([notices]);
        Assert.Equal("font v2", File.ReadAllText(Path.Combine(_game, "reframework", "fonts", "Alegreya.ttf")));
    }

    [Fact]
    public void ShowsUninstallRowWhenArchiveIsMissing()
    {
        var manager = NewManager();
        manager.State.Installed.Add(new InstalledMod
        {
            UnitId = "Ghost", PackageFileName = "Ghost.zip", DisplayName = "Ghost",
            Files = [new InstalledFile { Path = "reframework/autorun/ghost.lua" }],
        });
        var view = manager.BuildViews(collapseVersions: true).Single();
        Assert.Equal(PackageStatus.ArchiveMissing, view.Status);
        Assert.Null(view.Package);
    }

    [Theory]
    [InlineData("reframework/autorun/a.lua", false)]
    [InlineData("natives/stm/a.tex", true)]
    [InlineData("pak_mods/x0001-a.pak", true)]
    [InlineData("dinput8.dll", true)]
    public void KnowsWhichFilesNeedTheGameClosed(string path, bool expected) =>
        Assert.Equal(expected, ModManager.NeedsGameClosed([path]));
}
