using System.Net;
using SaveFileSync.Core.Config;
using SaveFileSync.Core.Model;
using SaveFileSync.Windows;

namespace SaveFileSync.Metadata.Tests;

public class CoverCacheTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("https://cdn2.steamgriddb.com/thumb/a.jpg", true)]
    [InlineData("https://www.steamgriddb.com/x.png", true)]
    [InlineData("https://s3.amazonaws.com/steamgriddb/thumb/a.png", true)]
    [InlineData("https://s3.amazonaws.com/other-bucket/a.png", false)]
    [InlineData("http://cdn2.steamgriddb.com/thumb/a.jpg", false)]
    [InlineData("https://evilsteamgriddb.com/a.png", false)]
    [InlineData("https://steamgriddb.com.evil.example/a.png", false)]
    [InlineData("https://user:pw@cdn2.steamgriddb.com/a.png", false)]
    [InlineData("file:///C:/secret.png", false)]
    [InlineData("not a url", false)]
    public void OnlySteamGridDbImagesOverHttps_AreAllowed(string url, bool allowed) =>
        Assert.Equal(allowed, CoverCache.IsAllowedUrl(url));

    [Fact]
    public async Task Download_IsCached_AndNotRepeated()
    {
        var http = new FakeHttp(_ => FakeHttp.Image(Png));
        var cache = new CoverCache(_dir.Path, http.Client());
        const string url = "https://cdn2.steamgriddb.com/thumb/a.jpg";

        var first = await cache.EnsureAsync(url, default);
        var second = await cache.EnsureAsync(url, default);

        Assert.Equal(first, second);
        Assert.Equal(Png, await File.ReadAllBytesAsync(first!));
        Assert.Single(http.Requests);
        Assert.Equal(first, cache.Find(url));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task ForbiddenHost_IsNeverContacted()
    {
        var http = new FakeHttp(_ => FakeHttp.Image(Png));
        var cache = new CoverCache(_dir.Path, http.Client());
        Assert.Null(await cache.EnsureAsync("https://example.com/a.png", default));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task NotAnImage_OrTooBig_OrFailing_IsRejected()
    {
        const string url = "https://cdn2.steamgriddb.com/thumb/a.jpg";
        Assert.Null(await new CoverCache(_dir.Path, new FakeHttp(_ => FakeHttp.Json("{}")).Client()).EnsureAsync(url, default));
        Assert.Null(await new CoverCache(_dir.Path, new FakeHttp(_ => FakeHttp.Image(new byte[CoverCache.MaxBytes + 1])).Client()).EnsureAsync(url, default));
        Assert.Null(await new CoverCache(_dir.Path, new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).Client()).EnsureAsync(url, default));
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Prune_RemovesOnlyOldUnusedCovers()
    {
        var http = new FakeHttp(_ => FakeHttp.Image(Png));
        var cache = new CoverCache(_dir.Path, http.Client());
        const string keep = "https://cdn2.steamgriddb.com/thumb/keep.jpg";
        const string old = "https://cdn2.steamgriddb.com/thumb/old.jpg";
        const string recent = "https://cdn2.steamgriddb.com/thumb/recent.jpg";
        foreach (var url in new[] { keep, old, recent })
        {
            await cache.EnsureAsync(url, default);
        }

        File.SetLastWriteTimeUtc(cache.PathFor(keep), DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(cache.PathFor(old), DateTime.UtcNow.AddDays(-10));

        Assert.Equal(1, cache.Prune(new HashSet<string> { keep }, DateTime.UtcNow));
        Assert.NotNull(cache.Find(keep));
        Assert.Null(cache.Find(old));
        Assert.NotNull(cache.Find(recent));
    }
}

public class SecretStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Secrets_AreEncryptedOnDisk_AndSurviveARestart()
    {
        var path = Path.Combine(_dir.Path, "secrets.dat");
        var store = new DpapiSecretStore(path);
        store.Set(SecretNames.SteamGridDbKey, "super-secret-key-123");
        store.Set(SecretNames.IgdbClientId, "client-abc");

        var raw = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain("super-secret", raw);
        Assert.DoesNotContain("client-abc", raw);

        var again = new DpapiSecretStore(path);
        Assert.Equal("super-secret-key-123", again.Get(SecretNames.SteamGridDbKey));
        Assert.Equal("client-abc", again.Get(SecretNames.IgdbClientId));
        Assert.Null(again.Get(SecretNames.IgdbClientSecret));
    }

    [Fact]
    public void RemovingTheLastSecret_DeletesTheFile()
    {
        var path = Path.Combine(_dir.Path, "secrets.dat");
        var store = new DpapiSecretStore(path);
        store.Set("a", "1");
        store.Set("a", null);
        Assert.False(File.Exists(path));
        Assert.Null(store.Get("a"));
    }

    [Fact]
    public void ACorruptFile_MeansNoKeys_NotACrash()
    {
        var path = Path.Combine(_dir.Path, "secrets.dat");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var store = new DpapiSecretStore(path);
        Assert.Null(store.Get("a"));
        store.Set("a", "1"); // and it can be written again
        Assert.Equal("1", new DpapiSecretStore(path).Get("a"));
    }
}

public class MetadataServiceTests : IDisposable
{
    private const string Thumb = "https://cdn2.steamgriddb.com/thumb/c1.jpg";
    private readonly TempDir _dir = new();
    private readonly FakeCatalog _catalog = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc));
    private string? _sgdbKey = "key";
    private (string, string)? _igdb = ("cid", "sec");
    private readonly List<string> _changed = [];

    public void Dispose() => _dir.Dispose();

    private HttpResponseMessage Route(SeenRequest r)
    {
        var url = r.Url;
        if (url.StartsWith(IgdbClient.TokenUrl, StringComparison.Ordinal))
        {
            return FakeHttp.Json("""{"access_token":"t","expires_in":99999,"token_type":"bearer"}""");
        }

        if (url == IgdbClient.GamesUrl)
        {
            return r.Body!.Contains("\"Hades\"", StringComparison.Ordinal)
                ? FakeHttp.Json("""[{"id":113112,"name":"Hades","first_release_date":1600300800}]""")
                : FakeHttp.Json("[]");
        }

        if (url.Contains("/search/autocomplete/Hades", StringComparison.Ordinal))
        {
            return FakeHttp.Json("""{"success":true,"data":[{"id":5,"name":"Hades II","verified":true},{"id":4,"name":"Hades","verified":true}]}""");
        }

        if (url.Contains("/games/steam/1145360", StringComparison.Ordinal))
        {
            return FakeHttp.Json("""{"success":true,"data":{"id":4,"name":"Hades"}}""");
        }

        if (url.Contains("/grids/game/4?", StringComparison.Ordinal))
        {
            return FakeHttp.Json("""{"success":true,"data":[{"id":1,"score":3,"url":"https://cdn2.steamgriddb.com/grid/c1.png","thumb":"https://cdn2.steamgriddb.com/thumb/c1.jpg","author":{"name":"Ann"}}]}""");
        }

        if (url == Thumb)
        {
            return FakeHttp.Image([1, 2, 3]);
        }

        return FakeHttp.Json("""{"success":true,"data":[]}""");
    }

    private (MetadataService Service, FakeHttp Http) Create()
    {
        var http = new FakeHttp(Route);
        var client = http.Client();
        var service = new MetadataService(
            _catalog,
            new SteamGridDbClient(client, () => _sgdbKey),
            new IgdbClient(client, () => _igdb, () => _clock.UtcNow, new RateLimiter(TimeSpan.Zero)),
            new CoverCache(Path.Combine(_dir.Path, "covers"), client),
            Path.Combine(_dir.Path, "state.json"),
            _clock);
        service.UnitChanged += _changed.Add;
        return (service, http);
    }

    private MetadataService SgdbOnly(Func<SeenRequest, HttpResponseMessage> respond, out FakeHttp http)
    {
        http = new FakeHttp(respond);
        var client = http.Client();
        return new MetadataService(
            _catalog, new SteamGridDbClient(client, () => _sgdbKey), null,
            new CoverCache(Path.Combine(_dir.Path, "covers"), client), Path.Combine(_dir.Path, "state.json"), _clock);
    }

    [Fact]
    public async Task NoKeys_NothingHappens_AndNothingIsSent()
    {
        _sgdbKey = null;
        _igdb = null;
        _catalog.Add("u1", "Hades");
        var (service, http) = Create();

        await service.RunPassAsync(default);

        Assert.Empty(http.Requests);
        Assert.Equal(0, _catalog.Saves);
        Assert.False(service.HasCoverSource);
    }

    [Fact]
    public async Task ByName_PicksTheExactGame_NotTheSequel_AndDownloadsTheCover()
    {
        _catalog.Add("u1", "Hades");
        var (service, _) = Create();

        await service.RunPassAsync(default);

        var m = _catalog.Get("u1").Metadata!;
        Assert.Equal(4, m.SteamGridDbGameId);
        Assert.Equal(Thumb, m.CoverUrl);
        Assert.Equal("Ann", m.CoverAuthor);
        Assert.Equal(113112, m.IgdbId);
        Assert.Equal("Hades", m.OfficialName);
        Assert.False(m.Confirmed);
        Assert.NotNull(service.CoverPath(_catalog.Get("u1")));
        Assert.Contains("u1", _changed);
    }

    [Fact]
    public async Task ASteamId_IsUsedInsteadOfTheName()
    {
        _catalog.Add("u1", "Something custom", new UnitSource { SteamId = 1145360 });
        var (service, http) = Create();

        await service.RunPassAsync(default);

        Assert.Equal(4, _catalog.Get("u1").Metadata!.SteamGridDbGameId);
        Assert.DoesNotContain(http.Requests, r => r.Url.Contains("/search/autocomplete/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheManifestName_WinsOverTheDisplayName()
    {
        _catalog.Add("u1", "My Hades save", new UnitSource { ManifestName = "Hades" });
        var (service, _) = Create();
        await service.RunPassAsync(default);
        Assert.Equal("Hades", _catalog.Get("u1").Metadata!.OfficialName);
    }

    [Fact]
    public async Task OnlyNamesAndSteamIdsAreSent_NeverPathsOrIds()
    {
        _catalog.Add("unit-id-123456", "Hades", new UnitSource { SteamId = 1145360, InstallDirs = [@"C:\Users\someone\Games\Hades"] });
        var (service, http) = Create();
        await service.RunPassAsync(default);

        Assert.All(http.Requests, r =>
        {
            Assert.DoesNotContain("unit-id", r.Url + r.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("someone", r.Url + r.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("Users", r.Url + r.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task NoConfidentMatch_IsRetriedOnlyMuchLater()
    {
        _catalog.Add("u1", "Zzzz Unknown Game");
        var (service, http) = Create();

        await service.RunPassAsync(default);
        var after1 = http.Requests.Count;
        Assert.NotEqual(0, after1);
        Assert.Null(_catalog.Get("u1").Metadata);

        await service.RunPassAsync(default);
        Assert.Equal(after1, http.Requests.Count); // not asked again

        _clock.Advance(TimeSpan.FromDays(31));
        await service.RunPassAsync(default);
        Assert.True(http.Requests.Count > after1);
    }

    [Fact]
    public async Task ARejectedKey_StopsTheServiceUntilTheKeyChanges()
    {
        _igdb = null;
        _catalog.Add("u1", "Hades");
        _catalog.Add("u2", "Celeste");
        var service = SgdbOnly(_ => FakeHttp.Json("{}", HttpStatusCode.Unauthorized), out var http);

        await service.RunPassAsync(default);
        Assert.Single(http.Requests); // the second game is not even tried

        await service.RunPassAsync(default);
        Assert.Single(http.Requests);

        service.ResetAttempts();
        await service.RunPassAsync(default);
        Assert.True(http.Requests.Count > 1);
    }

    [Fact]
    public async Task RateLimit_PausesThePass_ForTheRequestedTime()
    {
        _catalog.Add("u1", "Hades");
        _catalog.Add("u2", "Celeste");
        var service = SgdbOnly(_ => FakeHttp.Json("{}", HttpStatusCode.TooManyRequests), out var http);

        await service.RunPassAsync(default);
        Assert.Single(http.Requests);
        await service.RunPassAsync(default);
        Assert.Single(http.Requests); // still paused

        _clock.Advance(TimeSpan.FromMinutes(16));
        await service.RunPassAsync(default);
        Assert.True(http.Requests.Count > 1);
    }

    [Fact]
    public async Task AConfirmedChoice_IsNeverChangedAutomatically()
    {
        _catalog.Add("u1", "Hades", metadata: new UnitMetadata { SteamGridDbGameId = 99, Confirmed = true });
        var (service, http) = Create();

        await service.RunPassAsync(default);

        Assert.Equal(99, _catalog.Get("u1").Metadata!.SteamGridDbGameId);
        Assert.Null(_catalog.Get("u1").Metadata!.CoverUrl);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task SaveChoice_StoresItConfirmed_AndTheCoverIsCached()
    {
        _catalog.Add("u1", "Hades");
        var (service, _) = Create();

        await service.SaveChoiceAsync("u1", new UnitMetadata { SteamGridDbGameId = 4, CoverUrl = Thumb, OfficialName = "Hades" }, default);

        Assert.True(_catalog.Get("u1").Metadata!.Confirmed);
        Assert.NotNull(service.CoverPath(_catalog.Get("u1")));
    }

    [Fact]
    public async Task ResetMatch_ClearsEvenAConfirmedChoice_AndMatchesAgain()
    {
        _catalog.Add("u1", "Hades", metadata: new UnitMetadata { SteamGridDbGameId = 99, Confirmed = true });
        var (service, _) = Create();

        await service.ResetMatchAsync("u1");
        await service.RunPassAsync(default);

        Assert.Equal(4, _catalog.Get("u1").Metadata!.SteamGridDbGameId);
        Assert.False(_catalog.Get("u1").Metadata!.Confirmed);
    }

    [Fact]
    public async Task ACoverChosenOnAnotherPc_IsDownloadedHere_WithoutAnyKey()
    {
        _sgdbKey = null;
        _igdb = null;
        _catalog.Add("u1", "Hades", metadata: new UnitMetadata { CoverUrl = Thumb, Confirmed = true });
        var (service, http) = Create();

        await service.RunPassAsync(default);

        Assert.NotNull(service.CoverPath(_catalog.Get("u1")));
        Assert.Equal([Thumb], http.Requests.Select(r => r.Url));
        Assert.Null(http.Requests[0].Authorization); // images are public: no key is sent to the CDN
    }

    [Fact]
    public async Task ARemovedGame_IsIgnored()
    {
        _catalog.Add("u1", "Hades", removed: true);
        var (service, http) = Create();
        await service.RunPassAsync(default);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Search_ReportsEachSourceSeparately()
    {
        _igdb = null;
        var (service, _) = Create();

        var result = await service.SearchAsync("Hades", default);

        Assert.Equal(["Hades II", "Hades"], result.Games.Select(g => g.Name));
        Assert.Empty(result.OfficialNames);
        Assert.Null(result.GamesError);
    }
}
