using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SaveFileSync.Metadata;

public sealed record SgdbGame(long Id, string Name, bool Verified);

public sealed record SgdbGrid(long Id, string Url, string Thumb, int Width, int Height, int Score, string? Author);

/// <summary>
/// SteamGridDB API v2 (https://www.steamgriddb.com/api/v2): game search and cover ("grid") lists.
/// Only game names and Steam app ids are ever sent; the key is read at call time and never logged.
/// </summary>
public sealed class SteamGridDbClient(HttpClient http, Func<string?> apiKey)
{
    public const string BaseUrl = "https://www.steamgriddb.com/api/v2";

    /// <summary>Portrait covers, the sizes Steam itself uses.</summary>
    public const string CoverDimensions = "600x900,342x482,660x930";

    public bool HasKey => !string.IsNullOrWhiteSpace(apiKey());

    public async Task<IReadOnlyList<SgdbGame>> SearchAsync(string term, CancellationToken token)
    {
        var data = await GetAsync($"/search/autocomplete/{Uri.EscapeDataString(term)}", token).ConfigureAwait(false);
        return data is { ValueKind: JsonValueKind.Array } ? data.Value.EnumerateArray().Select(ParseGame).OfType<SgdbGame>().ToList() : [];
    }

    /// <summary>The game for a Steam app id, or null when SteamGridDB does not know it.</summary>
    public async Task<SgdbGame?> GetGameBySteamIdAsync(int steamAppId, CancellationToken token)
    {
        var data = await GetAsync($"/games/steam/{steamAppId}", token).ConfigureAwait(false);
        return data is { ValueKind: JsonValueKind.Object } ? ParseGame(data.Value) : null;
    }

    /// <summary>Static portrait covers, best rated first. NSFW, humor and epilepsy-triggering images are excluded.</summary>
    public async Task<IReadOnlyList<SgdbGrid>> GetCoversAsync(long gameId, CancellationToken token)
    {
        var query = $"?dimensions={CoverDimensions}&types=static&nsfw=false&humor=false&epilepsy=false";
        var data = await GetAsync($"/grids/game/{gameId}{query}", token).ConfigureAwait(false);
        if (data is not { ValueKind: JsonValueKind.Array })
        {
            return [];
        }

        return data.Value.EnumerateArray()
            .Select(ParseGrid)
            .OfType<SgdbGrid>()
            .OrderByDescending(g => g.Score)
            .ThenBy(g => g.Id)
            .ToList();
    }

    /// <summary>Checks the key with the cheapest possible call.</summary>
    public async Task TestKeyAsync(CancellationToken token) => await SearchAsync("test", token).ConfigureAwait(false);

    private static SgdbGame? ParseGame(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var id) && id.TryGetInt64(out var value)
            && e.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? new SgdbGame(value, name.GetString()!, e.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True)
            : null;

    private static SgdbGrid? ParseGrid(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object
            || !e.TryGetProperty("id", out var id) || !id.TryGetInt64(out var gridId)
            || !e.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String
            || !e.TryGetProperty("thumb", out var thumb) || thumb.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? author = null;
        if (e.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object
            && a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            author = n.GetString();
        }

        return new SgdbGrid(gridId, url.GetString()!, thumb.GetString()!, Int(e, "width"), Int(e, "height"), Int(e, "score"), author);
    }

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private async Task<JsonElement?> GetAsync(string path, CancellationToken token)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new MetadataException(MetadataFailure.InvalidKey, "No SteamGridDB key is set.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new MetadataException(MetadataFailure.Unavailable, "SteamGridDB is not reachable.", inner: ex);
        }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new MetadataException(MetadataFailure.Unavailable, "SteamGridDB did not answer in time.", inner: ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return null;
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new MetadataException(MetadataFailure.InvalidKey, "SteamGridDB rejected the API key.");
                case HttpStatusCode.TooManyRequests:
                    throw new MetadataException(MetadataFailure.RateLimited, "SteamGridDB asked to slow down.", RetryAfter(response));
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MetadataException(MetadataFailure.Unavailable, $"SteamGridDB answered {(int)response.StatusCode}.");
            }

            try
            {
                var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success))
                {
                    throw new MetadataException(MetadataFailure.BadResponse, "Unexpected answer from SteamGridDB.");
                }

                if (success.ValueKind != JsonValueKind.True)
                {
                    return null;
                }

                return root.TryGetProperty("data", out var data) ? data.Clone() : null;
            }
            catch (JsonException ex)
            {
                throw new MetadataException(MetadataFailure.BadResponse, "Unexpected answer from SteamGridDB.", inner: ex);
            }
        }
    }

    internal static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date?.Subtract(DateTimeOffset.UtcNow);
}
