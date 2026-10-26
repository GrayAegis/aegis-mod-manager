using System.IO.Compression;
using DD2ModManager.Core.Archives;

namespace DD2ModManager.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dd2mm-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(string relative)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(p);
        return p;
    }

    public string Write(string relative, string content)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal static class Fixtures
{
    public static IReadOnlyList<SourceEntry> Entries(params string[] paths) =>
        paths.Select(p => new SourceEntry(p, 10, null)).ToList();

    /// <summary>Creates a zip whose entries are (path, content) pairs.</summary>
    public static string Zip(string path, params (string Entry, string Content)[] files)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(content);
        }
        return path;
    }

    public static string MakeGame(TempDir dir)
    {
        var game = dir.Sub("game");
        File.WriteAllText(System.IO.Path.Combine(game, "DD2.exe"), "exe");
        return game;
    }
}
