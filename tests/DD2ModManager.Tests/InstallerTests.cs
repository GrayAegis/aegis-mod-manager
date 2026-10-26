using DD2ModManager.Core.Install;
using DD2ModManager.Core.Library;
using DD2ModManager.Core.State;

namespace DD2ModManager.Tests;

public class InstallerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _game;
    private readonly string _backup;
    private readonly AppState _state = new();
    private int _saves;

    public InstallerTests()
    {
        _game = Fixtures.MakeGame(_dir);
        _backup = _dir.Sub("backup");
    }

    public void Dispose() => _dir.Dispose();

    private Installer NewInstaller() => new(_game, _backup, _state, () => _saves++);

    private (ModPackage Package, string Extracted) Package(string name, params (string Path, string Content)[] files)
    {
        var archive = Fixtures.Zip(Path.Combine(_dir.Path, "lib", name + ".zip"), files);
        var package = ModLibrary.Load(archive);
        var extracted = _dir.Sub("x-" + name);
        package.OpenSource().ExtractAll(extracted);
        return (package, extracted);
    }

    private string GameFile(string rel) => Path.Combine(_game, rel.Replace('/', '\\'));

    [Fact]
    public void InstallsFilesAndRecordsThem()
    {
        var (pkg, x) = Package("ModA", ("reframework/autorun/a.lua", "A"));
        var mod = NewInstaller().Install(pkg, pkg.Units[0], x);

        Assert.Equal("A", File.ReadAllText(GameFile("reframework/autorun/a.lua")));
        Assert.Equal("reframework/autorun/a.lua", mod.Files.Single().Path);
        Assert.Same(mod, _state.Installed.Single());
        Assert.True(_saves > 0);
    }

    [Fact]
    public void PaksGetIncreasingLoadOrderSlots()
    {
        var installer = NewInstaller();
        var (a, xa) = Package("PakA", ("A.pak", "a"));
        var (b, xb) = Package("PakB", ("B.pak", "b"));
        installer.Install(a, a.Units[0], xa);
        installer.Install(b, b.Units[0], xb);

        Assert.True(File.Exists(GameFile("pak_mods/x0000-A.pak")));
        Assert.True(File.Exists(GameFile("pak_mods/x0001-B.pak")));
    }

    [Fact]
    public void NextSlotAccountsForUntrackedPaks()
    {
        Directory.CreateDirectory(GameFile("pak_mods"));
        File.WriteAllText(GameFile("pak_mods/x0007-Other.pak"), "?");
        Assert.Equal(8, NewInstaller().NextPakSlot());
    }

    [Fact]
    public void BacksUpOriginalGameFilesAndRestoresThemOnUninstall()
    {
        File.WriteAllText(GameFile("dinput8.dll"), "original");
        var (pkg, x) = Package("Loader", ("dinput8.dll", "modded"));
        var installer = NewInstaller();
        var mod = installer.Install(pkg, pkg.Units[0], x);

        Assert.Equal("modded", File.ReadAllText(GameFile("dinput8.dll")));
        Assert.True(mod.Files.Single().HasBackup);

        installer.Uninstall(mod);
        Assert.Equal("original", File.ReadAllText(GameFile("dinput8.dll")));
        Assert.Empty(_state.Installed);
    }

    [Fact]
    public void UninstallRemovesEmptyFoldersButKeepsSharedOnes()
    {
        var (pkg, x) = Package("Deep", ("natives/stm/gui/ui01/a.gui", "a"), ("reframework/autorun/Deep/b.lua", "b"));
        var installer = NewInstaller();
        installer.Uninstall(installer.Install(pkg, pkg.Units[0], x));

        Assert.False(Directory.Exists(GameFile("natives")));
        Assert.False(Directory.Exists(GameFile("reframework/autorun/Deep")));
        Assert.True(Directory.Exists(GameFile("reframework/autorun")));
    }

    [Fact]
    public void ConflictsBlockInstallUntilOverwriteIsChosen()
    {
        var installer = NewInstaller();
        var (a, xa) = Package("First", ("reframework/autorun/shared.lua", "first"));
        var (b, xb) = Package("Second", ("reframework/autorun/shared.lua", "second"));
        var first = installer.Install(a, a.Units[0], xa);

        var ex = Assert.Throws<InstallConflictException>(() => installer.Install(b, b.Units[0], xb));
        Assert.Equal("reframework/autorun/shared.lua", ex.Conflicts.Single().Path);
        Assert.Equal("first", File.ReadAllText(GameFile("reframework/autorun/shared.lua")));

        var second = installer.Install(b, b.Units[0], xb, overwriteConflicts: true);
        Assert.Equal("second", File.ReadAllText(GameFile("reframework/autorun/shared.lua")));
        Assert.Empty(first.Files);
        Assert.Single(second.Files);
    }

    [Fact]
    public void FailedInstallRollsEverythingBack()
    {
        File.WriteAllText(GameFile("original.dll"), "original");
        var (pkg, x) = Package("Broken", ("original.dll", "new"), ("reframework/autorun/z.lua", "z"));
        File.Delete(Path.Combine(x, "reframework", "autorun", "z.lua"));

        Assert.ThrowsAny<IOException>(() => NewInstaller().Install(pkg, pkg.Units[0], x));
        Assert.Equal("original", File.ReadAllText(GameFile("original.dll")));
        Assert.Empty(_state.Installed);
        Assert.False(File.Exists(Path.Combine(_backup, "original.dll")));
    }

    [Fact]
    public void ReusingPakSlotsKeepsLoadOrder()
    {
        var installer = NewInstaller();
        var (a, xa) = Package("PakA", ("A.pak", "a"));
        var (b, xb) = Package("PakB", ("B.pak", "b"));
        var modA = installer.Install(a, a.Units[0], xa);
        installer.Install(b, b.Units[0], xb);

        var slots = Installer.PakSlotsOf(modA);
        installer.Uninstall(modA);
        var (a2, xa2) = Package("PakA2", ("A.pak", "a2"));
        installer.Install(a2, a2.Units[0], xa2, pakSlots: slots);

        Assert.Equal("a2", File.ReadAllText(GameFile("pak_mods/x0000-A.pak")));
    }
}
