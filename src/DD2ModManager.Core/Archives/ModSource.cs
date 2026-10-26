using System.Diagnostics;
using System.Text;
using DD2ModManager.Core.Util;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace DD2ModManager.Core.Archives;

/// <summary>A file inside a mod archive or folder. Path uses forward slashes.</summary>
public sealed record SourceEntry(string Path, long Size, uint? Crc);

/// <summary>Read access to a mod package: an archive (zip/7z/rar) or an extracted folder.</summary>
public interface IModSource
{
    string FullPath { get; }
    IReadOnlyList<SourceEntry> ListFiles();
    byte[]? ReadFile(string path, long maxBytes = 16 * 1024 * 1024);
    /// <summary>Extracts every file into <paramref name="directory"/>, keeping relative paths.</summary>
    void ExtractAll(string directory);
}

public static class ModSources
{
    public static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar"];

    public static bool IsArchive(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IModSource Open(string path) =>
        Directory.Exists(path) ? new FolderModSource(path) : new ArchiveModSource(path);
}

public sealed class FolderModSource(string root) : IModSource
{
    public string FullPath { get; } = Path.GetFullPath(root);

    public IReadOnlyList<SourceEntry> ListFiles() =>
        Directory.EnumerateFiles(FullPath, "*", SearchOption.AllDirectories)
            .Select(f => new SourceEntry(
                PathUtil.NormalizeRelative(Path.GetRelativePath(FullPath, f))!,
                new FileInfo(f).Length, null))
            .ToList();

    public byte[]? ReadFile(string path, long maxBytes = 16 * 1024 * 1024)
    {
        var full = PathUtil.Combine(FullPath, path);
        if (!File.Exists(full) || new FileInfo(full).Length > maxBytes) return null;
        return File.ReadAllBytes(full);
    }

    public void ExtractAll(string directory)
    {
        foreach (var entry in ListFiles())
        {
            var target = PathUtil.Combine(directory, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(PathUtil.Combine(FullPath, entry.Path), target, overwrite: true);
        }
    }
}

/// <summary>
/// Archive reader backed by SharpCompress, falling back to an installed 7-Zip when SharpCompress
/// can't handle a format variant (some RAR5 and 7z filter combinations).
/// </summary>
public sealed class ArchiveModSource(string path) : IModSource
{
    public string FullPath { get; } = Path.GetFullPath(path);

    public IReadOnlyList<SourceEntry> ListFiles()
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(FullPath, new ReaderOptions());
            return archive.Entries
                .Where(e => !e.IsDirectory)
                .Select(e => (Entry: e, Path: PathUtil.NormalizeRelative(e.Key ?? "")))
                .Where(x => x.Path != null)
                .Select(x => new SourceEntry(x.Path!, x.Entry.Size, x.Entry.Crc == 0 ? null : (uint)x.Entry.Crc))
                .ToList();
        }
        catch (Exception ex) when (SevenZip.IsAvailable)
        {
            Log.Warn($"SharpCompress could not list {Path.GetFileName(FullPath)} ({ex.Message}); using 7-Zip");
            return SevenZip.List(FullPath);
        }
    }

    public byte[]? ReadFile(string path, long maxBytes = 16 * 1024 * 1024)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(FullPath, new ReaderOptions());
            var entry = archive.Entries.FirstOrDefault(e =>
                !e.IsDirectory && PathUtil.Comparer.Equals(PathUtil.NormalizeRelative(e.Key ?? ""), path));
            if (entry == null || entry.Size > maxBytes) return null;
            using var stream = entry.OpenEntryStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception) when (SevenZip.IsAvailable)
        {
            return SevenZip.ReadFile(FullPath, path);
        }
    }

    public void ExtractAll(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            using var archive = ArchiveFactory.OpenArchive(FullPath, new ReaderOptions());
            // Sequential extraction is required for solid 7z/rar archives to stay fast.
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;
                var rel = PathUtil.NormalizeRelative(reader.Entry.Key ?? "");
                if (rel == null) continue;
                var target = PathUtil.Combine(directory, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = File.Create(target);
                reader.WriteEntryTo(output);
            }
        }
        catch (Exception ex) when (SevenZip.IsAvailable)
        {
            Log.Warn($"SharpCompress could not extract {Path.GetFileName(FullPath)} ({ex.Message}); using 7-Zip");
            Directory.Delete(directory, recursive: true);
            Directory.CreateDirectory(directory);
            SevenZip.Extract(FullPath, directory);
        }
    }
}

internal static class SevenZip
{
    private static readonly Lazy<string?> Exe = new(() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => Path.Combine(p, "7-Zip", "7z.exe"))
        .FirstOrDefault(File.Exists));

    public static bool IsAvailable => Exe.Value != null;

    public static IReadOnlyList<SourceEntry> List(string archive)
    {
        var output = Run(["l", "-slt", "-ba", archive]);
        var entries = new List<SourceEntry>();
        string? path = null; long size = 0; uint? crc = null; bool isDir = false;
        void Flush()
        {
            var rel = path == null ? null : PathUtil.NormalizeRelative(path);
            if (rel != null && !isDir) entries.Add(new SourceEntry(rel, size, crc));
            path = null; size = 0; crc = null; isDir = false;
        }
        foreach (var line in Encoding.UTF8.GetString(output).Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.StartsWith("Path = ")) { Flush(); path = l[7..]; }
            else if (l.StartsWith("Size = ")) long.TryParse(l[7..], out size);
            else if (l.StartsWith("CRC = ") && uint.TryParse(l[6..], System.Globalization.NumberStyles.HexNumber, null, out var c)) crc = c;
            else if (l.StartsWith("Folder = ")) isDir = l[9..] == "+";
            else if (l.StartsWith("Attributes = ") && l.Contains('D') && l[13..].StartsWith('D')) isDir = true;
        }
        Flush();
        return entries;
    }

    public static byte[]? ReadFile(string archive, string path)
    {
        var data = Run(["e", "-so", archive, path.Replace('/', '\\')]);
        return data.Length == 0 ? null : data;
    }

    public static void Extract(string archive, string directory) =>
        Run(["x", "-y", $"-o{directory}", archive]);

    private static byte[] Run(string[] args)
    {
        var psi = new ProcessStartInfo(Exe.Value!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        using var buffer = new MemoryStream();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.WaitForExit();
        if (process.ExitCode > 1)
            throw new IOException($"7-Zip failed ({process.ExitCode}): {stderr.Result.Trim()}");
        return buffer.ToArray();
    }
}
