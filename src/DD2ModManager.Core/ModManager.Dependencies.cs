using DD2ModManager.Core.Library;
using DD2ModManager.Core.Nexus;
using DD2ModManager.Core.State;
using DD2ModManager.Core.Util;

namespace DD2ModManager.Core;

public enum RequirementStatus { Installed, Missing, Unknown }

/// <param name="Required">False for requirements the author marks as optional or recommended.</param>
/// <param name="FromModInfo">From a modinfo.ini AddonFor/requirement line rather than the Nexus page.</param>
/// <param name="IsModManager">A mod manager the author packaged for (e.g. Fluffy); this app stands in for it.</param>
public sealed record RequirementInfo(string Name, string? Notes, bool Required, RequirementStatus Status, string? Url,
    bool FromModInfo = false, int? ModId = null, bool IsModManager = false);

public sealed record ChangelogView(string Title, IReadOnlyList<ChangelogEntry> Entries, bool IsUpdate);

// Changelogs and dependencies between installed mods.
public sealed partial class ModManager
{
    private const long RequirementsMaxAgeSeconds = 24 * 3600;

    /// <summary>
    /// Fetches requirements from Nexus for every mod in the library: all of them once a day, otherwise only new ones.
    /// Uses the public v2 API, so it works without an API key.
    /// </summary>
    public async Task RefreshRequirementsAsync(bool force = false, CancellationToken ct = default)
    {
        var ids = Library.Select(ModIdOf).Concat(State.Installed.Select(ModIdOf)).OfType<int>().Distinct().ToList();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var stale = force || now - Cache.RequirementsFetchedUnix > RequirementsMaxAgeSeconds;
        var toFetch = stale ? ids : ids.Where(id => !Cache.Requirements.ContainsKey(id)).ToList();
        if (toFetch.Count == 0) return;

        using var graph = new NexusGraphQl();
        var fetched = await graph.GetRequirementsAsync(toFetch, ct);
        foreach (var (id, requirements) in fetched) Cache.Requirements[id] = requirements;
        if (stale) Cache.RequirementsFetchedUnix = now;
        SaveCache();
        Log.Info($"Requirements refreshed for {fetched.Count} mods");
    }

    /// <summary>Requirements are advisory, so failing to fetch them is logged and otherwise ignored.</summary>
    public async Task TryRefreshRequirementsAsync(bool force = false, CancellationToken ct = default)
    {
        try { await RefreshRequirementsAsync(force, ct); }
        catch (Exception ex) when (ex is NexusApiException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Warn($"Couldn't fetch mod requirements from Nexus: {ex.Message}");
        }
    }

    public int? ModIdOf(ModPackage package) =>
        State.Nexus.GetValueOrDefault(package.FileName)?.ModId ?? package.ParsedName.NexusModId;

    public int? ModIdOf(InstalledMod mod) =>
        Locate(mod).Package is { } p ? ModIdOf(p) : mod.PackageFileName is { } f ? ArchiveNameParser.Parse(f).NexusModId : null;

    private (ModPackage? Package, ModUnit? Unit) Locate(InstalledMod mod)
    {
        var package = Library.FirstOrDefault(p => PathUtil.Comparer.Equals(p.FileName, mod.PackageFileName));
        return (package, package?.Units.FirstOrDefault(u => PathUtil.Comparer.Equals(u.Id, mod.UnitId)));
    }

    private HashSet<int> ModIdsOf(IEnumerable<InstalledMod> mods) => mods.Select(ModIdOf).OfType<int>().ToHashSet();

    /// <summary>
    /// Names other mods use to point at an installed part in modinfo.ini: its modinfo name and folder, plus the archive
    /// name when the archive holds only this part. (Content Editor's archive name matches its core part's name, so
    /// letting every part claim it would hide that the core part is gone.)
    /// </summary>
    private IEnumerable<string> NamesOf(InstalledMod mod)
    {
        var (package, unit) = Locate(mod);
        var names = new List<string?> { unit?.Info?.Name, unit?.Id };
        if (package?.Units.Count == 1) names.AddRange([package.DisplayName, package.ParsedName.DisplayName]);
        return names.OfType<string>();
    }

    /// <summary>Fluffy's in-archive dependencies: "AddonFor=Content editor" and "requirement=…".</summary>
    private static IEnumerable<string> LocalNeeds(ModUnit? unit) =>
        new[] { unit?.Info?.AddonFor, unit?.Info?.Requirement }.OfType<string>().Where(n => n.Length > 0);

    private string PackageName(InstalledMod mod) =>
        Locate(mod).Package?.DisplayName ?? (mod.PackageFileName is { } f ? ArchiveNameParser.Parse(f).DisplayName : mod.DisplayName);

    private RequirementInfo Describe(NexusRequirement requirement, HashSet<int> installedIds)
    {
        if (requirement.IsModManager)
            return new RequirementInfo(requirement.ModName, requirement.Notes, requirement.IsRequired, RequirementStatus.Installed,
                requirement.PageUrl, IsModManager: true);
        var status = requirement.IsREFramework
            ? Game?.HasREFramework == true ? RequirementStatus.Installed : RequirementStatus.Missing
            : requirement.IsSameGame
                ? installedIds.Contains(requirement.ModId) ? RequirementStatus.Installed : RequirementStatus.Missing
                : RequirementStatus.Unknown;
        return new RequirementInfo(requirement.ModName, requirement.Notes, requirement.IsRequired, status, requirement.PageUrl,
            ModId: requirement.IsSameGame && !requirement.IsREFramework ? requirement.ModId : null);
    }

    /// <summary>
    /// An uninstalled library archive that satisfies a requirement. When a Nexus mod has several files (Bestiary and
    /// Bestiary Variant Spawns), the one whose name matches the requirement's mod name wins.
    /// </summary>
    public ModPackage? LibraryPackageFor(RequirementInfo requirement)
    {
        var name = requirement.Name;
        var candidates = requirement.ModId is { } id
            ? Library.Where(p => ModIdOf(p) == id)
            : requirement.FromModInfo
                ? Library.Where(p => string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase) ||
                                     p.Units.Any(u => string.Equals(u.Info?.Name, name, StringComparison.OrdinalIgnoreCase) ||
                                                      string.Equals(u.Id, name, StringComparison.OrdinalIgnoreCase)))
                : [];
        var key = ArchiveNameParser.Normalize(name);
        return candidates
            .Where(p => p.Error == null && !State.InstalledFrom(p.FileName).Any())
            .OrderByDescending(p => ArchiveNameParser.Normalize(p.ParsedName.DisplayName) is { Length: > 0 } n && key.StartsWith(n))
            .ThenByDescending(UploadedAt)
            .FirstOrDefault();
    }

    /// <summary>What a mod needs, from its Nexus page and its modinfo.ini add-on lines that point outside the archive.</summary>
    public IReadOnlyList<RequirementInfo> RequirementsOf(PackageView view)
    {
        var result = new List<RequirementInfo>();
        var installedIds = ModIdsOf(State.Installed);
        if (view.ModId is { } id && Cache.Requirements.TryGetValue(id, out var requirements))
            result.AddRange(requirements.Where(r => r.ModId != id || !r.IsSameGame).Select(r => Describe(r, installedIds)));

        if (view.Package is { } package)
        {
            var ownNames = package.Units.SelectMany(u => new[] { u.Info?.Name, u.Id }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var installedNames = State.Installed.SelectMany(NamesOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var need in package.Units.SelectMany(LocalNeeds).Distinct(StringComparer.OrdinalIgnoreCase).Where(n => !ownNames.Contains(n)))
                result.Add(new RequirementInfo(need, "Listed in the mod's modinfo.ini", true,
                    installedNames.Contains(need) ? RequirementStatus.Installed : RequirementStatus.Missing, null, FromModInfo: true));
        }
        return result;
    }

    /// <summary>Installed mods (by name) that list this one as a hard requirement.</summary>
    public IReadOnlyList<string> RequiredBy(PackageView view)
    {
        if (view.ModId is not { } id) return [];
        return State.Installed
            .Where(m => ModIdOf(m) is { } other && other != id &&
                        Cache.Requirements.TryGetValue(other, out var reqs) &&
                        reqs.Any(r => r.IsRequired && r.IsSameGame && !r.IsModManager && r.ModId == id))
            .Select(PackageName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// "Bestiary needs _NickCore" lines for every installed mod that would lose a hard requirement if
    /// <paramref name="removing"/> were uninstalled. Another installed copy of the same Nexus mod still counts.
    /// </summary>
    public IReadOnlyList<string> DependentsBrokenBy(IReadOnlyCollection<InstalledMod> removing)
    {
        var remaining = State.Installed.Except(removing).ToList();
        var lostIds = ModIdsOf(removing);
        lostIds.ExceptWith(ModIdsOf(remaining));
        var lines = new List<string>();

        foreach (var mod in remaining)
        {
            if (ModIdOf(mod) is not { } id || !Cache.Requirements.TryGetValue(id, out var requirements)) continue;
            foreach (var r in requirements.Where(r => r.IsRequired && r.IsSameGame && !r.IsREFramework && !r.IsModManager &&
                                                      r.ModId != id && lostIds.Contains(r.ModId)))
                lines.Add($"{PackageName(mod)} needs {r.ModName}");
        }

        var removedNames = removing.SelectMany(NamesOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keptNames = remaining.SelectMany(NamesOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in remaining)
            foreach (var need in LocalNeeds(Locate(mod).Unit).Where(n => removedNames.Contains(n) && !keptNames.Contains(n)))
                lines.Add($"{mod.DisplayName} needs {need}");

        return lines.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Hard requirements of a package that aren't installed (call after installing it).</summary>
    public IReadOnlyList<RequirementInfo> MissingRequirements(ModPackage package, IEnumerable<ModUnit> units)
    {
        var installedIds = ModIdsOf(State.Installed);
        var missing = new List<RequirementInfo>();
        if (ModIdOf(package) is { } id && Cache.Requirements.TryGetValue(id, out var requirements))
            missing.AddRange(requirements.Where(r => r.IsRequired && r.ModId != id)
                .Select(r => Describe(r, installedIds))
                .Where(r => r.Status == RequirementStatus.Missing));

        var installedNames = State.Installed.SelectMany(NamesOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        missing.AddRange(units.SelectMany(LocalNeeds).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !installedNames.Contains(n))
            .Select(n => new RequirementInfo(n, "Listed in the mod's modinfo.ini", true, RequirementStatus.Missing, null, FromModInfo: true)));
        return missing;
    }

    /// <summary>
    /// What changed between the installed version and the available update, or the current version's notes
    /// when there's nothing newer.
    /// </summary>
    public ChangelogView? ChangelogFor(PackageView view)
    {
        if (view.ModId is not { } modId || !Cache.Mods.TryGetValue(modId, out var cached)) return null;
        var currentId = view.Link?.FileId;

        NexusFile? target = view.Update is { HasUpdate: true, Latest: { } latest } ? latest
            : view.NewerLocal is { } newer ? Cache.FindFile(modId, State.Nexus.GetValueOrDefault(newer.FileName)?.FileId)
            : null;
        if (target != null && target.FileId != currentId)
        {
            var entries = Changelog.Between(cached.Files, currentId, target.FileId);
            return entries.Count == 0 ? null : new ChangelogView("WHAT'S NEW", entries, IsUpdate: true);
        }

        var current = Cache.FindFile(modId, currentId);
        return current != null && Changelog.Of(current) is { } entry
            ? new ChangelogView($"CHANGES IN {(current.Version is { } v ? "V" + v.TrimStart('v', 'V') : "THIS VERSION")}", [entry], IsUpdate: false)
            : null;
    }
}
