using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DD2ModManager.Core.Nexus;

/// <summary>A mod listed under "Requirements" on a Nexus mod page.</summary>
public sealed partial class NexusRequirement
{
    public int ModId { get; set; }
    public string ModName { get; set; } = "";
    public int GameId { get; set; }
    public bool ExternalRequirement { get; set; }
    public string? Url { get; set; }
    public string? Notes { get; set; }

    [GeneratedRegex(@"\brequire[sd]?\b", RegexOptions.IgnoreCase)]
    private static partial Regex RequiredWord();

    [GeneratedRegex(@"\b(not|isn'?t|no longer)\s+(strictly\s+|really\s+|actually\s+)?require|\bsoft\b|\boptional\b", RegexOptions.IgnoreCase)]
    private static partial Regex SoftWords();

    /// <summary>
    /// Authors list optional add-ons as requirements too, explaining in the note ("Not required, but highly
    /// recommended", "*SOFT REQUIREMENT*"). Only an empty note or an unqualified "required" counts as a hard need.
    /// </summary>
    [JsonIgnore]
    public bool IsRequired => string.IsNullOrWhiteSpace(Notes) || (RequiredWord().IsMatch(Notes) && !SoftWords().IsMatch(Notes));

    /// <summary>REFramework is listed both as an external GitHub link and as Nexus mod 8.</summary>
    [JsonIgnore]
    public bool IsREFramework => ModName.Contains("REFramework", StringComparison.OrdinalIgnoreCase) ||
                                 Url?.Contains("REFramework", StringComparison.OrdinalIgnoreCase) == true ||
                                 (ModId == 8 && GameId == NexusGraphQl.GameId);

    /// <summary>
    /// Authors often list the mod manager they package for ("Fluffy Mod Manager"). That's about installing,
    /// not a dependency of the game, and this app fills the role.
    /// </summary>
    [JsonIgnore]
    public bool IsModManager => ModName.Contains("Fluffy", StringComparison.OrdinalIgnoreCase) ||
                                ModName.Contains("Vortex", StringComparison.OrdinalIgnoreCase) ||
                                ModName.Contains("Mod Organizer", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsSameGame => !ExternalRequirement && ModId > 0 && (GameId == NexusGraphQl.GameId || GameId == 0);

    [JsonIgnore]
    public string? PageUrl => IsSameGame ? $"https://www.nexusmods.com/{NexusClient.GameDomain}/mods/{ModId}" : Url;
}

/// <summary>
/// Nexus Mods v2 GraphQL API. Used for mod requirements, which v1 doesn't expose; public data, so no API key is sent.
/// </summary>
public sealed class NexusGraphQl(HttpMessageHandler? handler = null) : IDisposable
{
    public const int GameId = 6234;
    private const string Endpoint = "https://api.nexusmods.com/v2/graphql";
    private const int BatchSize = 50;

    private readonly HttpClient _http = CreateClient(handler);

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Add("Application-Name", NexusClient.AppName);
        http.DefaultRequestHeaders.Add("Application-Version", NexusClient.AppVersion);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{NexusClient.AppName}/{NexusClient.AppVersion}");
        return http;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Requirements for each requested mod, batched into as few requests as possible.</summary>
    public async Task<Dictionary<int, List<NexusRequirement>>> GetRequirementsAsync(IEnumerable<int> modIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, List<NexusRequirement>>();
        foreach (var batch in modIds.Distinct().Where(id => id > 0).Chunk(BatchSize))
        {
            // IDs are integers, so building the literal list is safe.
            var ids = string.Join(", ", batch.Select(id => $"{{gameDomain: \"{NexusClient.GameDomain}\", modId: {id}}}"));
            var query = $$"""
                query { legacyModsByDomain(ids: [{{ids}}], count: {{batch.Length}}) { nodes { modId
                  modRequirements { nexusRequirements { nodes { modId modName gameId externalRequirement url notes } } } } } }
                """;
            using var response = await _http.PostAsJsonAsync(Endpoint, new { query }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new NexusApiException(response.StatusCode, $"Nexus GraphQL {(int)response.StatusCode}");
            var parsed = JsonSerializer.Deserialize<Response>(body, Json);
            if (parsed?.Errors is { Count: > 0 } errors)
                throw new NexusApiException(response.StatusCode, $"Nexus GraphQL: {errors[0].Message}");
            foreach (var node in parsed?.Data?.LegacyModsByDomain?.Nodes ?? [])
                result[node.ModId] = node.ModRequirements?.NexusRequirements?.Nodes ?? [];
        }
        return result;
    }

    public void Dispose() => _http.Dispose();

    private sealed record Response(ResponseData? Data, List<GraphError>? Errors);
    private sealed record GraphError(string Message);
    private sealed record ResponseData(ModPage? LegacyModsByDomain);
    private sealed record ModPage(List<ModNode>? Nodes);
    private sealed record ModNode(int ModId, Requirements? ModRequirements);
    private sealed record Requirements(RequirementPage? NexusRequirements);
    private sealed record RequirementPage(List<NexusRequirement>? Nodes);
}
