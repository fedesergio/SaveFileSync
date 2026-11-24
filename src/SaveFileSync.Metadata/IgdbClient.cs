using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SaveFileSync.Metadata;

public sealed record IgdbGame(long Id, string Name, int? Year, IReadOnlyList<string> AlternativeNames, IReadOnlyList<string> Platforms)
{
    public string Display => Year is { } y ? $"{Name} ({y})" : Name;
}

/// <summary>
/// IGDB API v4 through a Twitch client-credentials token (https://api-docs.igdb.com/).
/// Limited to about 4 requests per second, which this client enforces itself.
/// </summary>
public sealed class IgdbClient(HttpClient http, Func<(string Id, string Secret)?> credentials, Func<DateTime>? now = null, RateLimiter? limiter = null)
{
    public const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    public const string GamesUrl = "https://api.igdb.com/v4/games";

    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private readonly RateLimiter _limiter = limiter ?? new RateLimiter(TimeSpan.FromMilliseconds(300));
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private string? _tokenFor;
    private DateTime _tokenExpiry;

    public bool HasCredentials => credentials() is { } c && c.Id.Trim().Length > 0 && c.Secret.Trim().Length > 0;

    public async Task<IReadOnlyList<IgdbGame>> SearchAsync(string term, CancellationToken token)
    {
        var body = $"search \"{Escape(term)}\"; fields name,first_release_date,platforms.name,alternative_names.name; limit 10;";
        for (var attempt = 0; ; attempt++)
        {
            var (id, bearer) = await EnsureTokenAsync(token).ConfigureAwait(false);
            await _limiter.WaitAsync(token).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, GamesUrl) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
            request.Headers.Add("Client-ID", id);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _token = null; // expired early: get a new one once
                continue;
            }

            Check(response);
            try
            {
                var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return Parse(text);
            }
            catch (JsonException ex)
            {
                throw new MetadataException(MetadataFailure.BadResponse, "Unexpected answer from IGDB.", inner: ex);
            }
        }
    }

    /// <summary>Checks the credentials by asking Twitch for a token.</summary>
    public async Task TestCredentialsAsync(CancellationToken token)
    {
        _token = null;
        await EnsureTokenAsync(token).ConfigureAwait(false);
    }

    internal static IReadOnlyList<IgdbGame> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<IgdbGame>();
        foreach (var e in document.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var id) || !id.TryGetInt64(out var gameId)
                || !e.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            int? year = e.TryGetProperty("first_release_date", out var date) && date.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).Year
                : null;
            result.Add(new IgdbGame(gameId, name.GetString()!, year, Names(e, "alternative_names"), Names(e, "platforms")));
        }

        return result;
    }

    private static List<string> Names(JsonElement e, string property)
    {
        var names = new List<string>();
        if (e.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                // Expanded references are objects; a plain number means the field was not expanded.
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    names.Add(n.GetString()!);
                }
            }
        }

        return names;
    }

    /// <summary>Apicalypse strings use double quotes: escape the characters that could end or alter one.</summary>
    internal static string Escape(string term) => term.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

    private async Task<(string ClientId, string Token)> EnsureTokenAsync(CancellationToken token)
    {
        var creds = credentials();
        if (creds is not { } c || c.Id.Trim().Length == 0 || c.Secret.Trim().Length == 0)
        {
            throw new MetadataException(MetadataFailure.InvalidKey, "No IGDB credentials are set.");
        }

        var clientId = c.Id.Trim();
        var identity = clientId + "\n" + c.Secret.Trim();
        await _tokenGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_token is not null && _tokenFor == identity && _now() < _tokenExpiry)
            {
                return (clientId, _token);
            }

            var url = $"{TokenUrl}?client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(c.Secret.Trim())}&grant_type=client_credentials";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            using var response = await SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new MetadataException(MetadataFailure.InvalidKey, "Twitch rejected the client id or secret.");
            }

            Check(response);
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                var root = document.RootElement;
                _token = root.GetProperty("access_token").GetString() ?? throw new JsonException("no token");
                var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) ? s : 3600;
                // Renew a minute early so a request never carries a token that expires on the way.
                _tokenExpiry = _now().AddSeconds(Math.Max(60, seconds - 60));
                _tokenFor = identity;
                return (clientId, _token);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new MetadataException(MetadataFailure.BadResponse, "Unexpected answer from Twitch.", inner: ex);
            }
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            return await http.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new MetadataException(MetadataFailure.Unavailable, "IGDB/Twitch is not reachable.", inner: ex);
        }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new MetadataException(MetadataFailure.Unavailable, "IGDB/Twitch did not answer in time.", inner: ex);
        }
    }

    private static void Check(HttpResponseMessage response)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                throw new MetadataException(MetadataFailure.InvalidKey, "IGDB rejected the credentials.");
            case HttpStatusCode.TooManyRequests:
                throw new MetadataException(MetadataFailure.RateLimited, "IGDB asked to slow down.", SteamGridDbClient.RetryAfter(response));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new MetadataException(MetadataFailure.Unavailable, $"IGDB answered {(int)response.StatusCode}.");
        }
    }
}
