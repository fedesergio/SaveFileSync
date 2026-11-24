using System.Security.Cryptography;
using System.Text;
using SaveFileSync.Core.IO;

namespace SaveFileSync.Metadata;

/// <summary>
/// Covers downloaded to <c>cache\covers\</c> on this PC. File names come from a hash of the URL, so a
/// changed cover is simply a different file; nothing here is ever written to the shared repository.
/// </summary>
public sealed class CoverCache(string directory, HttpClient http)
{
    public const long MaxBytes = 4 * 1024 * 1024;

    public string PathFor(string url) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant() + ".img");

    public string? Find(string? url) => url is not null && File.Exists(PathFor(url)) ? PathFor(url) : null;

    /// <summary>
    /// Only https images served by SteamGridDB are fetched: the URL comes from a shared document, so it is
    /// checked before the app connects to it.
    /// </summary>
    public static bool IsAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return host == "steamgriddb.com"
            || host.EndsWith(".steamgriddb.com", StringComparison.Ordinal)
            || (host == "s3.amazonaws.com" && uri.AbsolutePath.StartsWith("/steamgriddb/", StringComparison.Ordinal));
    }

    /// <summary>Downloads the cover if it is not cached yet. Returns its path, or null if it cannot be used.</summary>
    public async Task<string?> EnsureAsync(string url, CancellationToken token)
    {
        if (Find(url) is { } cached)
        {
            return cached;
        }

        if (!IsAllowedUrl(url))
        {
            return null;
        }

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
                || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBytes)
                {
                    return null;
                }
            }

            AtomicFile.WriteAllBytes(PathFor(url), buffer.ToArray(), "cover");
            return PathFor(url);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Deletes cached covers no game uses any more (only files older than a day, so a pick in progress survives).</summary>
    public int Prune(IReadOnlySet<string> usedUrls, DateTime utcNow)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var keep = usedUrls.Select(PathFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.img"))
        {
            if (!keep.Contains(file) && utcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1))
            {
                AtomicFile.TryDelete(file);
                deleted++;
            }
        }

        return deleted;
    }
}
