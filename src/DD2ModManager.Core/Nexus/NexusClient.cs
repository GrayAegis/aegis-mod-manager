using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DD2ModManager.Core.Nexus;

public class NexusApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class NexusRateLimitException(string message) : NexusApiException(HttpStatusCode.TooManyRequests, message);

/// <summary>Client for the Nexus Mods v1 API, authenticated with a personal API key.</summary>
public sealed class NexusClient : IDisposable
{
    public const string GameDomain = "dragonsdogma2";
    public const string AppName = "DD2ModManager";
    private const string BaseUrl = "https://api.nexusmods.com/v1/";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly HttpClient _download;

    public int? DailyRemaining { get; private set; }
    public int? HourlyRemaining { get; private set; }

    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    public NexusClient(string apiKey, HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _download = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _download.Timeout = TimeSpan.FromMinutes(30);
        _download.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppName}/{AppVersion}");
        _http.BaseAddress = new Uri(BaseUrl);
        _http.Timeout = TimeSpan.FromMinutes(30);
        _http.DefaultRequestHeaders.Add("apikey", apiKey);
        _http.DefaultRequestHeaders.Add("Application-Name", AppName);
        _http.DefaultRequestHeaders.Add("Application-Version", AppVersion);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"{AppName}/{AppVersion} ({RuntimeInformation.OSDescription.Replace('(', ' ').Replace(')', ' ').Trim()}; {RuntimeInformation.OSArchitecture})");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<NexusUser> ValidateAsync(CancellationToken ct = default) =>
        GetAsync<NexusUser>("users/validate.json", ct);

    public Task<NexusMod> GetModAsync(int modId, CancellationToken ct = default) =>
        GetAsync<NexusMod>($"games/{GameDomain}/mods/{modId}.json", ct);

    public Task<NexusModFiles> GetModFilesAsync(int modId, CancellationToken ct = default) =>
        GetAsync<NexusModFiles>($"games/{GameDomain}/mods/{modId}/files.json", ct);

    /// <summary>Looks up an archive by MD5. Returns an empty list when Nexus doesn't know the file.</summary>
    public async Task<IReadOnlyList<NexusMd5Result>> Md5SearchAsync(string md5, CancellationToken ct = default)
    {
        try
        {
            return await GetAsync<List<NexusMd5Result>>($"games/{GameDomain}/mods/md5_search/{md5}.json", ct);
        }
        catch (NexusApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    /// <summary>Mods with file or page changes in the last day, week or month ("1d", "1w", "1m").</summary>
    public Task<List<NexusUpdateEntry>> GetUpdatedAsync(string period, CancellationToken ct = default) =>
        GetAsync<List<NexusUpdateEntry>>($"games/{GameDomain}/mods/updated.json?period={period}", ct);

    /// <summary>
    /// CDN links for a file. Premium accounts can call this directly; free accounts need the key/expires pair
    /// from a "Mod Manager Download" (nxm://) link.
    /// </summary>
    public Task<List<NexusDownloadLink>> GetDownloadLinksAsync(int modId, int fileId, string? key = null, long? expires = null,
        CancellationToken ct = default)
    {
        var query = key != null && expires != null ? $"?key={Uri.EscapeDataString(key)}&expires={expires}" : "";
        return GetAsync<List<NexusDownloadLink>>($"games/{GameDomain}/mods/{modId}/files/{fileId}/download_link.json{query}", ct);
    }

    public async Task DownloadAsync(Uri uri, string destination, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var partial = destination + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // CDN links are pre-signed, so the download client never carries the API key.
        using (var response = await _download.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!response.IsSuccessStatusCode)
                throw new NexusApiException(response.StatusCode, $"Download failed: HTTP {(int)response.StatusCode}");
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(partial);
            var buffer = new byte[81920];
            long received = 0;
            var lastReport = 0L;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (received - lastReport > 256 * 1024 || received == total)
                {
                    progress?.Report(new DownloadProgress(received, total));
                    lastReport = received;
                }
            }
        }
        File.Move(partial, destination, overwrite: true);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        ReadRateLimits(response);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = ExtractMessage(body) ?? response.ReasonPhrase ?? "Request failed";
            throw response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => new NexusRateLimitException($"Nexus rate limit reached: {message}"),
                HttpStatusCode.Unauthorized => new NexusApiException(response.StatusCode, $"Nexus rejected the API key: {message}"),
                _ => new NexusApiException(response.StatusCode, $"Nexus API {(int)response.StatusCode}: {message}"),
            };
        }
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new NexusApiException(response.StatusCode, "Empty response from Nexus");
    }

    private void ReadRateLimits(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-rl-daily-remaining", out var d) && int.TryParse(d.FirstOrDefault(), out var daily))
            DailyRemaining = daily;
        if (response.Headers.TryGetValues("x-rl-hourly-remaining", out var h) && int.TryParse(h.FirstOrDefault(), out var hourly))
            HourlyRemaining = hourly;
    }

    private static string? ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var key in new[] { "message", "error" })
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
        _download.Dispose();
    }
}
