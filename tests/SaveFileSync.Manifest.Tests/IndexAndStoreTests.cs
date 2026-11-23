using System.Net;
using static SaveFileSync.Manifest.Tests.Games;

namespace SaveFileSync.Manifest.Tests;

public class IndexTests
{
    private static ManifestIndex Index() => new([
        new GameRecord { Name = "Hollow Knight", Paths = [Save("<winDocuments>/hk")], Aliases = ["HK Classic"] },
        new GameRecord { Name = "Hollow Knight: Silksong", Paths = [Save("<winDocuments>/ss")] },
        new GameRecord { Name = "Knight Hollow Tales", Paths = [Save("<winDocuments>/kt")] },
        new GameRecord { Name = "Celeste", Paths = [Save("<winDocuments>/c")] },
        new GameRecord { Name = "Dark Souls III", Paths = [Save("<winDocuments>/d")] },
    ]);

    [Fact]
    public void Search_RanksExactThenPrefixThenContains()
    {
        var names = Index().Search("hollow knight").Select(g => g.Name).ToList();
        Assert.Equal("Hollow Knight", names[0]);
        Assert.Equal("Hollow Knight: Silksong", names[1]);
        Assert.Equal("Knight Hollow Tales", names[2]);
    }

    [Fact]
    public void Search_IgnoresCasePunctuationAndWordOrder()
    {
        Assert.Contains(Index().Search("DARK-souls  iii"), g => g.Name == "Dark Souls III");
        Assert.Contains(Index().Search("souls dark"), g => g.Name == "Dark Souls III");
        Assert.Empty(Index().Search("   "));
        Assert.Empty(Index().Search("zzzz"));
    }

    [Fact]
    public void Search_FindsAliases()
    {
        Assert.Equal("Hollow Knight", Index().Search("hk classic").Single().Name);
    }

    [Fact]
    public void Cache_RoundTrips_AndRejectsOtherFormats()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sfs-idx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "idx.json");
            Index().Save(path);

            var loaded = ManifestIndex.Load(path)!;
            Assert.Equal(5, loaded.Games.Count);
            Assert.Equal("<winDocuments>/hk", loaded.Find("hollow knight")!.Paths.Single().Template);

            File.WriteAllText(path, """{"format": 999, "games": []}""");
            Assert.Null(ManifestIndex.Load(path));
            File.WriteAllText(path, "not json");
            Assert.Null(ManifestIndex.Load(path));
            Assert.Null(ManifestIndex.Load(Path.Combine(dir, "missing.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class ManifestStoreTests : IDisposable
{
    private const string Yaml = """
        "Test Game":
          files:
            "<winAppData>/TestGame":
              tags: [save]
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sfs-store-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeServer : HttpMessageHandler
    {
        public string Body { get; set; } = Yaml;

        public string ETag { get; set; } = "\"v1\"";

        public bool Fail { get; set; }

        public List<string?> IfNoneMatch { get; } = [];

        public List<string?> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IfNoneMatch.Add(request.Headers.TryGetValues("If-None-Match", out var v) ? v.Single() : null);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            if (Fail)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            if (IfNoneMatch[^1] == ETag)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(ETag);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task FirstUpdateDownloads_ThenEtagGivesNotModified()
    {
        var server = new FakeServer();
        var store = new ManifestStore(_dir, "1.2.3", server);
        var t0 = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(store.IsStale(TimeSpan.FromDays(7), t0));
        Assert.Equal(ManifestUpdateKind.Updated, (await store.UpdateAsync(false, t0)).Kind);
        Assert.Equal("Test Game", store.Load()!.Games.Single().Name);
        Assert.Equal("\"v1\"", store.GetStatus().ETag);
        Assert.Equal(1, store.GetStatus().GameCount);
        Assert.False(store.IsStale(TimeSpan.FromDays(7), t0.AddDays(1)));
        Assert.True(store.IsStale(TimeSpan.FromDays(7), t0.AddDays(8)));

        var t1 = t0.AddDays(8);
        Assert.Equal(ManifestUpdateKind.NotModified, (await store.UpdateAsync(false, t1)).Kind);
        Assert.Equal("\"v1\"", server.IfNoneMatch[^1]);
        Assert.Equal(t1, store.GetStatus().CheckedUtc);
        Assert.Equal(t0, store.GetStatus().UpdatedUtc);
        Assert.False(store.IsStale(TimeSpan.FromDays(7), t1.AddDays(1)));
    }

    [Fact]
    public async Task Update_SendsOnlyAGenericUserAgent()
    {
        var server = new FakeServer();
        await new ManifestStore(_dir, "1.2.3", server).UpdateAsync(false, DateTime.UtcNow);
        Assert.Equal("SaveFileSync/1.2.3", server.UserAgents.Single());
    }

    [Fact]
    public async Task ForceIgnoresTheEtag_AndNewDataReplacesTheIndex()
    {
        var server = new FakeServer();
        var store = new ManifestStore(_dir, "1", server);
        await store.UpdateAsync(false, DateTime.UtcNow);

        server.Body = Yaml.Replace("Test Game", "Renamed Game");
        server.ETag = "\"v2\"";
        Assert.Equal(ManifestUpdateKind.Updated, (await store.UpdateAsync(true, DateTime.UtcNow)).Kind);
        Assert.Null(server.IfNoneMatch[^1]);
        Assert.Equal("Renamed Game", store.Load()!.Games.Single().Name);
    }

    [Fact]
    public async Task FailedDownload_KeepsThePreviousIndex()
    {
        var server = new FakeServer();
        var store = new ManifestStore(_dir, "1", server);
        await store.UpdateAsync(false, DateTime.UtcNow);

        server.Fail = true;
        server.ETag = "\"v2\"";
        var result = await store.UpdateAsync(true, DateTime.UtcNow);

        Assert.Equal(ManifestUpdateKind.Failed, result.Kind);
        Assert.Equal("Test Game", store.Load()!.Games.Single().Name);
        Assert.Empty(Directory.GetFiles(_dir, "manifest.download-*"));
    }

    [Fact]
    public async Task GarbageContent_IsRejected_AndKeepsThePreviousIndex()
    {
        var server = new FakeServer();
        var store = new ManifestStore(_dir, "1", server);
        await store.UpdateAsync(false, DateTime.UtcNow);

        server.Body = "<html>not yaml: [unclosed";
        server.ETag = "\"v2\"";
        Assert.Equal(ManifestUpdateKind.Failed, (await store.UpdateAsync(true, DateTime.UtcNow)).Kind);
        Assert.Equal("Test Game", store.Load()!.Games.Single().Name);
        Assert.Equal("\"v1\"", store.GetStatus().ETag);
    }
}
