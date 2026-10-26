using DD2ModManager.Core;
using DD2ModManager.Core.Fluffy;
using DD2ModManager.Core.Library;
using DD2ModManager.Core.Util;

// Developer tool for checking the core logic against a real library without touching the game folder.
if (args.Length == 0)
{
    Console.WriteLine("usage: scan <library> | verify-fluffy <fluffyRoot> [gameDir] | check-updates");
    return 1;
}

switch (args[0])
{
    case "check-updates":
    {
        // Uses the app's saved settings (including the encrypted API key). Talks to Nexus; never installs anything.
        var manager = new ModManager();
        manager.Load();
        manager.Refresh();
        if (!manager.HasApiKey)
        {
            Console.WriteLine("No Nexus API key saved. Add it in the app's Settings first.");
            return 1;
        }
        await manager.CheckUpdatesAsync(new Progress<OperationProgress>(p => Console.Error.WriteLine(p.Message)));
        foreach (var v in manager.BuildViews(collapseVersions: true).OrderBy(v => v.Status))
            Console.WriteLine($"{v.Status,-16} {v.Name,-45} {v.Version,-10} -> {v.LatestVersion,-10} [{v.Link?.MatchedBy ?? "-"}] {v.Update?.Reason}");
        Console.WriteLine($"API calls left today: {manager.ApiDailyRemaining}");
        return 0;
    }

    case "scan":
        foreach (var p in ModLibrary.Scan(args[1]))
        {
            Console.WriteLine($"{p.FileName}");
            Console.WriteLine($"    parsed: name='{p.ParsedName.DisplayName}' id={p.ParsedName.NexusModId} ver={p.ParsedName.Version}");
            if (p.Error != null) Console.WriteLine($"    ERROR: {p.Error}");
            foreach (var u in p.Units)
                Console.WriteLine($"    unit '{u.Id}' root='{u.Root}' name='{u.DisplayName}' files={u.Files.Count} paks={u.Files.Count(f => f.IsPak)} shot={u.ScreenshotPath}");
        }
        return 0;

    case "verify-fluffy":
        return VerifyFluffy(args[1], args.Length > 2 ? args[2] : null);

    case "extract-check":
    {
        // Extracts every archive with SharpCompress only (no 7-Zip fallback) into a temp folder.
        DD2ModManager.Core.Archives.SevenZip.Enabled = false;
        var failures = 0;
        foreach (var p in ModLibrary.Scan(args[1]).Where(p => !p.IsFolder))
        {
            var temp = Path.Combine(Path.GetTempPath(), "dd2mm-extract-check", Guid.NewGuid().ToString("N"));
            try
            {
                var source = p.OpenSource();
                source.ExtractAll(temp);
                var expected = source.ListFiles().Count;
                var actual = Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).Count();
                var ok = expected == actual;
                if (!ok) failures++;
                Console.WriteLine($"[{(ok ? "OK" : "COUNT")}] {p.FileName} ({actual}/{expected} files)");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"[FAIL] {p.FileName}: {ex.Message}");
            }
            finally
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            }
        }
        Console.WriteLine($"{failures} failures");
        return failures == 0 ? 0 : 2;
    }

    case "reconcile-preview":
    {
        // Runs the shared-file repair on a copy of the app's data folder and reports what it would add.
        var real = DD2ModManager.Core.State.AppPaths.DataDir;
        var copy = Path.Combine(Path.GetTempPath(), "dd2mm-reconcile-preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        foreach (var f in new[] { "settings.json", "state.json", "nexus-cache.json" })
            if (File.Exists(Path.Combine(real, f))) File.Copy(Path.Combine(real, f), Path.Combine(copy, f));
        DD2ModManager.Core.State.AppPaths.DataDir = copy;
        DD2ModManager.Core.Log.Written += (_, m) => { if (m.Contains(" also uses ")) Console.WriteLine(m); };
        var manager = new ModManager();
        manager.Load();
        manager.Refresh();
        Directory.Delete(copy, recursive: true);
        return 0;
    }
}
return 1;

static int VerifyFluffy(string fluffyRoot, string? gameDir)
{
    var install = FluffyImport.ReadInstalled(FluffyImport.InstalledIni(fluffyRoot));
    gameDir ??= install.GamePath!;
    var library = ModLibrary.Scan(FluffyImport.ModsDir(fluffyRoot));
    var matches = FluffyImport.Match(install, library, gameDir);
    var problems = 0;

    foreach (var m in matches)
    {
        if (m.Unit == null)
        {
            Console.WriteLine($"[NO MATCH] {m.Entry.Section}");
            problems++;
            continue;
        }
        var fluffy = m.Entry.Files.Select(f => f.StartsWith("pak_mods/", StringComparison.OrdinalIgnoreCase)
            ? "pak_mods/" + FluffyImport.StripPakPrefix(PathUtil.FileName(f)) : f).ToHashSet(PathUtil.Comparer);
        var ours = m.Unit.Files.Select(f => f.Target).ToHashSet(PathUtil.Comparer);
        var missing = fluffy.Except(ours, PathUtil.Comparer).ToList();
        var extra = ours.Except(fluffy, PathUtil.Comparer).ToList();
        var status = missing.Count == 0 && extra.Count == 0 ? "OK" : "DIFF";
        if (status != "OK") problems++;
        Console.WriteLine($"[{status}] {m.Entry.Section} -> {m.Package!.FileName} :: {m.Unit.Id} (score {m.Score:0.00}, {ours.Count} files)");
        foreach (var f in missing.Take(8)) Console.WriteLine($"      fluffy-only: {f}");
        foreach (var f in extra.Take(8)) Console.WriteLine($"      ours-only:   {f}");
    }
    Console.WriteLine($"{matches.Count} Fluffy entries, {problems} with problems, {library.Count} packages, {library.Count(p => p.Error != null)} unreadable");
    return problems == 0 ? 0 : 2;
}
