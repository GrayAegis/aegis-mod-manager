using System.Text;

namespace DD2ModManager.Core.Library;

/// <summary>
/// Fluffy-style modinfo.ini. Accepts "key=value" and the older "key: value", with or without a [ModInfo] header.
/// </summary>
public sealed class ModInfo
{
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? Screenshot { get; init; }
    public string? Category { get; init; }
    public string? NameAsBundle { get; init; }
    public string? AddonFor { get; init; }
    public string? Requirement { get; init; }
    public bool DummyMod { get; init; }

    public static ModInfo Parse(byte[] data) => Parse(Decode(data));

    public static ModInfo Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '[' or ';' or '#') continue;
            var eq = line.IndexOf('=');
            var colon = line.IndexOf(':');
            var sep = eq < 0 ? colon : colon < 0 ? eq : Math.Min(eq, colon);
            if (sep <= 0) continue;
            var key = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim();
            if (value.Length > 0) values.TryAdd(key, value);
        }
        string? Get(params string[] keys) => keys.Select(k => values.GetValueOrDefault(k)).FirstOrDefault(v => v != null);
        return new ModInfo
        {
            Name = Get("name"),
            Version = Get("version"),
            // Fluffy treats "\n" inside descriptions as a line break.
            Description = Get("description")?.Replace("\\n", "\n"),
            Author = Get("author"),
            Screenshot = Get("screenshot"),
            Category = Get("category"),
            NameAsBundle = Get("NameAsBundle"),
            AddonFor = Get("AddonFor"),
            Requirement = Get("requirement", "requires"),
            DummyMod = string.Equals(Get("DummyMod"), "true", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string Decode(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        return Encoding.UTF8.GetString(data);
    }
}
