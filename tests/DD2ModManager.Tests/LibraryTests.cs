using System.Text;
using DD2ModManager.Core.Library;

namespace DD2ModManager.Tests;

public class ArchiveNameParserTests
{
    [Fact]
    public void ParsesSpacedNexusName()
    {
        var p = ArchiveNameParser.Parse("Bestiary 1196 2.8.11 2026-10-25T15-30Z lEHTkEUYS.zip");
        Assert.Equal("Bestiary", p.DisplayName);
        Assert.Equal(1196, p.NexusModId);
        Assert.Equal("2.8.11", p.Version);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 15, 30, 0, TimeSpan.Zero), p.UploadedAt);
    }

    [Fact]
    public void ParsesSpacedNameWithNumbersInIt()
    {
        var p = ArchiveNameParser.Parse("MenuPerformanceBoost 1.2 1837 1.2.1 2026-10-18T12-31Z QXvudX0G1.zip");
        Assert.Equal("MenuPerformanceBoost 1.2", p.DisplayName);
        Assert.Equal(1837, p.NexusModId);
        Assert.Equal("1.2.1", p.Version);
    }

    [Fact]
    public void ParsesClassicDashedName()
    {
        var p = ArchiveNameParser.Parse("Fluffy Mod Manager-1-3-027-1711077877.zip");
        Assert.Equal("Fluffy Mod Manager", p.DisplayName);
        Assert.Equal(1, p.NexusModId);
        Assert.Equal("3.027", p.Version);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1711077877), p.UploadedAt);
    }

    [Fact]
    public void FallsBackToFileStem()
    {
        var p = ArchiveNameParser.Parse("My Cool Mod.7z");
        Assert.Equal("My Cool Mod", p.DisplayName);
        Assert.Null(p.NexusModId);
    }

    [Theory]
    [InlineData("Immersive Lanterns v1.04", "immersivelanterns")]
    [InlineData("Better UI - AIO Part 1", "betteruiaiopart1")]
    [InlineData("Bestiary 2.8.11", "bestiary")]
    public void NormalizeStripsVersions(string input, string expected) =>
        Assert.Equal(expected, ArchiveNameParser.Normalize(input));
}

public class ModInfoTests
{
    [Fact]
    public void ParsesKeyValueWithSectionHeader()
    {
        var info = ModInfo.Parse("[ModInfo]\r\nName=Fewer Slips\r\nAuthor=Laast\r\nVersion=1.1\r\nDescription=Line one\\nLine two\r\n");
        Assert.Equal("Fewer Slips", info.Name);
        Assert.Equal("Laast", info.Author);
        Assert.Equal("1.1", info.Version);
        Assert.Equal("Line one\nLine two", info.Description);
    }

    [Fact]
    public void ParsesColonStyleAndKeepsColonsInValues()
    {
        var info = ModInfo.Parse("name: Pink Sherry\ndescription=See https://example.com: details\nscreenshot=shot.png");
        Assert.Equal("Pink Sherry", info.Name);
        Assert.Equal("See https://example.com: details", info.Description);
        Assert.Equal("shot.png", info.Screenshot);
    }

    [Fact]
    public void HandlesUtf8Bom() =>
        Assert.Equal("Mod", ModInfo.Parse([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("name=Mod")]).Name);
}

public class ModLayoutTests
{
    private static IReadOnlyList<ModUnit> Analyze(string packageName, Dictionary<string, string>? contents, params string[] paths) =>
        ModLayout.Analyze(Fixtures.Entries(paths), packageName,
            p => contents != null && contents.TryGetValue(p, out var c) ? Encoding.UTF8.GetBytes(c) : null);

    [Fact]
    public void RootLevelFilesBecomeOneUnitNamedAfterArchive()
    {
        var units = Analyze("Bestiary 1196 2.8.11", null, "reframework/autorun/Bestiary.lua", "reframework/autorun/Bestiary/Config.lua");
        var unit = Assert.Single(units);
        Assert.Equal("Bestiary 1196 2.8.11", unit.Id);
        Assert.Equal(["reframework/autorun/Bestiary.lua", "reframework/autorun/Bestiary/Config.lua"], unit.Files.Select(f => f.Target));
    }

    [Fact]
    public void EachShallowestModinfoFolderIsAUnit()
    {
        var units = Analyze("Content Editor", new() { ["content_editor/modinfo.ini"] = "name=Content editor\nscreenshot=editor.png" },
            "content_editor/modinfo.ini", "content_editor/editor.png", "content_editor/readme.md",
            "content_editor/reframework/autorun/content_editor.lua",
            "item_editor/modinfo.ini", "item_editor/reframework/autorun/editor_items.lua",
            "item_editor/nested/modinfo.ini");
        Assert.Equal(["content_editor", "item_editor"], units.Select(u => u.Id));
        var core = units[0];
        Assert.Equal("Content editor", core.DisplayName);
        Assert.Equal("content_editor/editor.png", core.ScreenshotPath);
        // Fluffy installs readme.md but not the screenshot or modinfo.ini.
        Assert.Equal(["readme.md", "reframework/autorun/content_editor.lua"], core.Files.Select(f => f.Target));
    }

    [Fact]
    public void PaksGoToPakModsAndTextReadmesAreSkipped()
    {
        var unit = Assert.Single(Analyze("Bridge", null,
            "DD2PakBridge/modinfo.ini", "DD2PakBridge/DD2 Pak Bridge - README.txt",
            "DD2PakBridge/pak_mods/zz-loose.pak", "DD2PakBridge/reframework/plugins/DD2PakBridge.dll"));
        Assert.Equal("DD2PakBridge", unit.Id);
        Assert.Contains(unit.Files, f => f is { IsPak: true, Target: "pak_mods/zz-loose.pak" });
        Assert.DoesNotContain(unit.Files, f => f.Target.EndsWith(".txt"));
    }

    [Fact]
    public void BarePakAtRoot()
    {
        var unit = Assert.Single(Analyze("Durnehviir 883 3.3", null, "Durnehviir.pak"));
        Assert.Equal("pak_mods/Durnehviir.pak", unit.Files.Single().Target);
        Assert.True(unit.HasPaks);
    }

    [Fact]
    public void SingleWrapperFolderWithoutModinfoIsSkipped()
    {
        var unit = Assert.Single(Analyze("Wrapped", null, "Wrapped/Inner/natives/stm/a.tex", "Wrapped/Inner/natives/stm/b.tex"));
        Assert.Equal("Wrapped", unit.Id);
        Assert.All(unit.Files, f => Assert.StartsWith("natives/", f.Target));
    }

    [Fact]
    public void SideBySideVariantsWithoutModinfoBecomeSeparateUnits()
    {
        var units = Analyze("Hair", null, "Short/natives/stm/hair.mesh", "Long/natives/stm/hair.mesh");
        Assert.Equal(["Short", "Long"], units.Select(u => u.Id));
        Assert.All(units, u => Assert.Equal("natives/stm/hair.mesh", u.Files.Single().Target));
    }

    [Fact]
    public void JunkAndGitFoldersAreIgnored()
    {
        var unit = Assert.Single(Analyze("Mod", null, "reframework/autorun/a.lua", ".git/config", "__MACOSX/._a.lua", "Thumbs.db"));
        Assert.Equal("reframework/autorun/a.lua", unit.Files.Single().Target);
    }
}
