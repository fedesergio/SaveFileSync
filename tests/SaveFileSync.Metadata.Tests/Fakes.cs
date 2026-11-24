using System.Net;
using System.Text;
using SaveFileSync.Core;
using SaveFileSync.Core.Model;

namespace SaveFileSync.Metadata.Tests;

public sealed record SeenRequest(HttpMethod Method, string Url, string? Authorization, string? Body, string? ClientId);

/// <summary>Answers HTTP requests from a test-supplied function and remembers what was asked.</summary>
public sealed class FakeHttp(Func<SeenRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<SeenRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var seen = new SeenRequest(
            request.Method,
            request.RequestUri!.AbsoluteUri,
            request.Headers.Authorization?.ToString(),
            body,
            request.Headers.TryGetValues("Client-ID", out var ids) ? ids.Single() : null);
        lock (Requests)
        {
            Requests.Add(seen);
        }

        return respond(seen);
    }

    public HttpClient Client() => new(this);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Image(byte[] bytes, string type = "image/png")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

public sealed class FakeClock(DateTime start) : IClock
{
    public DateTime UtcNow { get; private set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FakeCatalog : IUnitCatalog
{
    private readonly Dictionary<string, UnitDefinition> _units = new();

    public void Add(string id, string name, UnitSource? source = null, UnitMetadata? metadata = null, bool removed = false) =>
        _units[id] = new UnitDefinition { UnitId = id, Name = name, Source = source, Metadata = metadata, Removed = removed };

    public int Saves { get; private set; }

    public UnitDefinition Get(string id) => _units[id];

    public IReadOnlyList<UnitDefinition> GetDefinitions() => _units.Values.ToList();

    public Task<bool> SaveMetadataAsync(string unitId, UnitMetadata metadata)
    {
        Saves++;
        var d = _units[unitId];
        _units[unitId] = new UnitDefinition { UnitId = d.UnitId, Name = d.Name, Source = d.Source, Metadata = metadata };
        return Task.FromResult(true);
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sfs-meta-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
