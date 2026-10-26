using DD2ModManager.Core.Install;

namespace DD2ModManager.Core;

// REFramework (dinput8.dll) version check, update and restore.
public sealed partial class ModManager
{
    private static readonly TimeSpan REFrameworkCheckInterval = TimeSpan.FromHours(6);
    private IReadOnlyList<REFrameworkBuild> _reframeworkReleases = [];
    private DateTime _reframeworkCheckedUtc;

    /// <summary>Null until the first check has run.</summary>
    public REFrameworkStatus? REFramework { get; private set; }

    /// <summary>Test seam for GitHub requests.</summary>
    public HttpMessageHandler? REFrameworkHandler { get; set; }

    public async Task CheckREFrameworkAsync(bool force = false, CancellationToken ct = default)
    {
        if (Game is not { IsValid: true } game) return;
        if (force || _reframeworkReleases.Count == 0 || DateTime.UtcNow - _reframeworkCheckedUtc > REFrameworkCheckInterval)
        {
            using var service = new REFrameworkService(REFrameworkHandler);
            _reframeworkReleases = await service.GetReleasesAsync(ct);
            _reframeworkCheckedUtc = DateTime.UtcNow;
        }
        REFramework = REFrameworkService.Evaluate(game, State.REFramework, _reframeworkReleases);
        if (REFramework.UpdateAvailable)
            Log.Info($"REFramework: installed {REFramework.Installed?.Label ?? "unknown build"}, latest {REFramework.Latest?.Label}");
    }

    /// <summary>The check is advisory; GitHub being unreachable is only logged.</summary>
    public async Task TryCheckREFrameworkAsync(bool force = false, CancellationToken ct = default)
    {
        try { await CheckREFrameworkAsync(force, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Warn($"Couldn't check for REFramework updates: {ex.Message}");
        }
    }

    private GameFolder ClosedGame()
    {
        var game = Game is { IsValid: true } g ? g : throw new InvalidOperationException("Set the Dragon's Dogma 2 folder in Settings first.");
        if (game.IsRunning) throw new InvalidOperationException("Close Dragon's Dogma 2 first: it keeps REFramework's dinput8.dll open.");
        return game;
    }

    public async Task UpdateREFrameworkAsync(IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var game = ClosedGame();
        if (REFramework?.Latest == null) await CheckREFrameworkAsync(force: true, ct);
        var latest = REFramework?.Latest ?? throw new InvalidOperationException("Couldn't find a REFramework release to install.");
        using var service = new REFrameworkService(REFrameworkHandler);
        State.REFramework = await service.InstallAsync(game, latest, REFramework?.Installed?.Commit, progress, ct);
        SaveState();
        REFramework = REFrameworkService.Evaluate(game, State.REFramework, _reframeworkReleases);
    }

    public bool CanRestoreREFramework => REFrameworkService.Backups().Count > 0;

    public void RestoreREFramework()
    {
        var game = ClosedGame();
        State.REFramework = REFrameworkService.RestorePrevious(game, REFramework?.Installed?.Commit);
        SaveState();
        REFramework = REFrameworkService.Evaluate(game, State.REFramework, _reframeworkReleases);
    }
}
