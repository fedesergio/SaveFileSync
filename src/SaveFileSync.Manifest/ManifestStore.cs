using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SaveFileSync.Core.IO;

namespace SaveFileSync.Manifest;

public sealed class ManifestStatus
{
    public string? ETag { get; set; }

    /// <summary>Last time the server was asked (even when nothing changed).</summary>
    public DateTime? CheckedUtc { get; set; }

    /// <summary>Last time new data was downloaded and indexed.</summary>
    public DateTime? UpdatedUtc { get; set; }

    public int GameCount { get; set; }

    public int Format { get; set; }
}

public enum ManifestUpdateKind
{
    /// <summary>Downloaded and indexed a new version.</summary>
    Updated,
    /// <summary>The server said nothing changed (HTTP 304).</summary>
    NotModified,
    /// <summary>Could not download or read it; the previous index, if any, is kept.</summary>
    Failed,
}

public sealed record ManifestUpdateResult(ManifestUpdateKind Kind, string? Error = null);

/// <summary>
/// Downloads the Ludusavi manifest with a conditional request (ETag) and keeps only the
/// compact index in the cache folder (PLAN.md §7.3). The manifest itself is never stored
/// next to the project and never committed: it is data compiled from PCGamingWiki.
/// </summary>
public sealed class ManifestStore
{
    /// <summary>The data branch, as recommended by the manifest's README.</summary>
    public static readonly Uri DefaultUrl = new(
        "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/refs/heads/data/data/manifest.yaml");

    private readonly string _cacheDir;
    private readonly HttpMessageHandler? _handler;
    private readonly Uri _url;
    private readonly string _userAgent;

    public ManifestStore(string cacheDir, string appVersion = "dev", HttpMessageHandler? handler = null, Uri? url = null)
    {
        _cacheDir = cacheDir;
        _handler = handler;
        _url = url ?? DefaultUrl;
        _userAgent = "SaveFileSync/" + appVersion;
        Directory.CreateDirectory(cacheDir);
    }

    private string IndexPath => Path.Combine(_cacheDir, "manifest-index.json");

    private string MetaPath => Path.Combine(_cacheDir, "manifest-meta.json");

    public ManifestStatus GetStatus()
    {
        try
        {
            return File.Exists(MetaPath)
                ? JsonSerializer.Deserialize<ManifestStatus>(File.ReadAllBytes(MetaPath)) ?? new ManifestStatus()
                : new ManifestStatus();
        }
        catch (JsonException)
        {
            return new ManifestStatus();
        }
    }

    public bool HasIndex => File.Exists(IndexPath);

    /// <summary>True when there is no index or it was last checked more than <paramref name="maxAge"/> ago.</summary>
    public bool IsStale(TimeSpan maxAge, DateTime utcNow)
    {
        var status = GetStatus();
        return !HasIndex || status.Format != ManifestIndex.CacheFormat || status.CheckedUtc is not { } checkedUtc || utcNow - checkedUtc > maxAge;
    }

    /// <summary>Loads the cached index (a few MB of JSON); null when there is none yet.</summary>
    public ManifestIndex? Load() => ManifestIndex.Load(IndexPath);

    /// <summary>
    /// Asks the server for a newer manifest. With <paramref name="force"/> the ETag is not sent.
    /// Parsing happens while reading the downloaded file, entry by entry.
    /// </summary>
    public async Task<ManifestUpdateResult> UpdateAsync(bool force, DateTime utcNow, CancellationToken ct = default)
    {
        var status = GetStatus();
        var download = Path.Combine(_cacheDir, "manifest.download-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromMinutes(10);
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SaveFileSync", _userAgent["SaveFileSync/".Length..]));
            if (!force && HasIndex && status.Format == ManifestIndex.CacheFormat && status.ETag is { } etag)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                status.CheckedUtc = utcNow;
                SaveStatus(status);
                return new ManifestUpdateResult(ManifestUpdateKind.NotModified);
            }

            response.EnsureSuccessStatusCode();
            await using (var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(download, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await body.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            var newEtag = response.Headers.ETag?.ToString();
            var games = await Task.Run(() =>
            {
                using var reader = new StreamReader(download, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return ManifestParser.Parse(reader);
            }, ct).ConfigureAwait(false);

            if (games.Count == 0)
            {
                return new ManifestUpdateResult(ManifestUpdateKind.Failed, "The downloaded manifest contains no games.");
            }

            new ManifestIndex(games).Save(IndexPath);
            SaveStatus(new ManifestStatus
            {
                ETag = newEtag,
                CheckedUtc = utcNow,
                UpdatedUtc = utcNow,
                GameCount = games.Count,
                Format = ManifestIndex.CacheFormat,
            });
            return new ManifestUpdateResult(ManifestUpdateKind.Updated);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or YamlDotNet.Core.YamlException or TaskCanceledException or InvalidOperationException)
        {
            return new ManifestUpdateResult(ManifestUpdateKind.Failed, ex.Message);
        }
        finally
        {
            AtomicFile.TryDelete(download);
        }
    }

    private void SaveStatus(ManifestStatus status) =>
        AtomicFile.WriteAllBytes(MetaPath, JsonSerializer.SerializeToUtf8Bytes(status), "meta");
}
