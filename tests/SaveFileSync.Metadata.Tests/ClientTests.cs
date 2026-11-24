using System.Net;

namespace SaveFileSync.Metadata.Tests;

public class NameMatcherTests
{
    [Theory]
    [InlineData("The Witcher 3: Wild Hunt", "Witcher 3 - Wild Hunt")]
    [InlineData("Hollow Knight™", "hollow knight")]
    [InlineData("Dead Cells", "DEAD CELLS")]
    [InlineData("Pokémon Rouge", "Pokemon Rouge")]
    public void SameGameWrittenDifferently_ScoresOne(string a, string b) =>
        Assert.Equal(1.0, NameMatcher.Similarity(a, b));

    [Fact]
    public void SequelsAreNotAutoMatched()
    {
        Assert.True(NameMatcher.Similarity("Hades", "Hades II") < MetadataService.AutoMatchThreshold);
        Assert.True(NameMatcher.Similarity("Resident Evil 2", "Resident Evil 3") < MetadataService.AutoMatchThreshold);
        Assert.True(NameMatcher.Similarity("Celeste", "Dark Souls") < 0.3);
    }

    [Fact]
    public void Best_PicksTheClosestAboveTheThreshold_AndNullOtherwise()
    {
        string[] names = ["Hades II", "Hades", "Hades' Star"];
        Assert.Equal("Hades", NameMatcher.Best("Hades", names, n => [n], 0.85));
        Assert.Null(NameMatcher.Best("Something else entirely", names, n => [n], 0.85));
        Assert.Null(NameMatcher.Best("", names, n => [n], 0.85));
    }
}

public class SteamGridDbClientTests
{
    private const string Games = """{"success":true,"data":[{"id":2254,"name":"Half-Life 2","types":["steam"],"verified":true},{"id":9,"name":"Other"},{"nope":1}]}""";

    private static SteamGridDbClient Client(FakeHttp http, string? key = "k3y") => new(http.Client(), () => key);

    [Fact]
    public async Task Search_SendsTheBearerKey_AndParsesGames()
    {
        var http = new FakeHttp(_ => FakeHttp.Json(Games));
        var games = await Client(http).SearchAsync("Half-Life 2 & more", default);

        Assert.Equal(2, games.Count);
        Assert.Equal(new SgdbGame(2254, "Half-Life 2", true), games[0]);
        var request = Assert.Single(http.Requests);
        Assert.Equal("Bearer k3y", request.Authorization);
        Assert.Equal("https://www.steamgriddb.com/api/v2/search/autocomplete/Half-Life%202%20%26%20more", request.Url);
    }

    [Fact]
    public async Task Covers_AreFilteredToPortraitStatic_AndSortedByScore()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""
            {"success":true,"data":[
              {"id":1,"score":1,"url":"https://cdn2.steamgriddb.com/grid/a.png","thumb":"https://cdn2.steamgriddb.com/thumb/a.jpg","width":600,"height":900,"author":{"name":"Ann"}},
              {"id":2,"score":7,"url":"https://cdn2.steamgriddb.com/grid/b.png","thumb":"https://cdn2.steamgriddb.com/thumb/b.jpg","width":600,"height":900},
              {"id":3,"url":"x"}]}
            """));

        var covers = await Client(http).GetCoversAsync(42, default);

        Assert.Equal([2L, 1L], covers.Select(c => c.Id));
        Assert.Equal("Ann", covers[1].Author);
        var url = http.Requests.Single().Url;
        Assert.Contains("/grids/game/42?", url);
        Assert.Contains("dimensions=600x900,342x482,660x930", url);
        Assert.Contains("nsfw=false", url);
        Assert.Contains("humor=false", url);
        Assert.Contains("types=static", url);
    }

    [Fact]
    public async Task UnknownGame_IsNull_NotAnError()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""{"success":false,"errors":["Game not found."]}""", HttpStatusCode.NotFound));
        Assert.Null(await Client(http).GetGameBySteamIdAsync(1, default));
        Assert.Empty(await Client(http).GetCoversAsync(1, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, MetadataFailure.InvalidKey)]
    [InlineData(HttpStatusCode.Forbidden, MetadataFailure.InvalidKey)]
    [InlineData(HttpStatusCode.TooManyRequests, MetadataFailure.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, MetadataFailure.Unavailable)]
    public async Task HttpErrors_AreClassified(HttpStatusCode status, MetadataFailure expected)
    {
        var http = new FakeHttp(_ => FakeHttp.Json("{}", status));
        var ex = await Assert.ThrowsAsync<MetadataException>(() => Client(http).SearchAsync("x", default));
        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public async Task NoKey_FailsWithoutAnyRequest()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("{}"));
        var ex = await Assert.ThrowsAsync<MetadataException>(() => Client(http, key: " ").SearchAsync("x", default));
        Assert.Equal(MetadataFailure.InvalidKey, ex.Failure);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task GarbageAnswer_IsABadResponse()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("<html>hello</html>"));
        var ex = await Assert.ThrowsAsync<MetadataException>(() => Client(http).SearchAsync("x", default));
        Assert.Equal(MetadataFailure.BadResponse, ex.Failure);
    }
}

public class IgdbClientTests
{
    private const string Token = """{"access_token":"tok1","expires_in":5587808,"token_type":"bearer"}""";

    private static (IgdbClient Client, FakeHttp Http, FakeClock Clock) Create(Func<SeenRequest, HttpResponseMessage>? games = null, bool noCreds = false)
    {
        (string, string)? creds = noCreds ? null : ("cid", "sec");
        var clock = new FakeClock(new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc));
        var http = new FakeHttp(r => r.Url.StartsWith(IgdbClient.TokenUrl, StringComparison.Ordinal)
            ? FakeHttp.Json(Token)
            : (games ?? (_ => FakeHttp.Json("[]")))(r));
        var client = new IgdbClient(http.Client(), () => creds, () => clock.UtcNow, new RateLimiter(TimeSpan.Zero));
        return (client, http, clock);
    }

    [Fact]
    public async Task Search_GetsATwitchToken_ThenQueriesWithTheDocumentedHeaders()
    {
        var (client, http, _) = Create(_ => FakeHttp.Json("""
            [{"id":1942,"name":"The Witcher 3: Wild Hunt","first_release_date":1431993600,
              "platforms":[{"id":6,"name":"PC (Microsoft Windows)"}],"alternative_names":[{"id":1,"name":"Wiedźmin 3"}]},
             {"id":5,"name":"No expansion","platforms":[48]}]
            """));

        var games = await client.SearchAsync("Witcher 3", default);

        Assert.Equal(2, games.Count);
        Assert.Equal(2015, games[0].Year);
        Assert.Equal("The Witcher 3: Wild Hunt (2015)", games[0].Display);
        Assert.Equal(["Wiedźmin 3"], games[0].AlternativeNames);
        Assert.Empty(games[1].Platforms);

        var token = http.Requests[0];
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Equal("https://id.twitch.tv/oauth2/token?client_id=cid&client_secret=sec&grant_type=client_credentials", token.Url);
        var query = http.Requests[1];
        Assert.Equal("cid", query.ClientId);
        Assert.Equal("Bearer tok1", query.Authorization);
        Assert.StartsWith("search \"Witcher 3\";", query.Body);
        Assert.Contains("limit 10;", query.Body);
    }

    [Fact]
    public async Task TheTokenIsReused_UntilItExpires_ThenRenewed()
    {
        var (client, http, clock) = Create();
        await client.SearchAsync("a", default);
        await client.SearchAsync("b", default);
        Assert.Equal(1, http.Requests.Count(r => r.Url.StartsWith(IgdbClient.TokenUrl, StringComparison.Ordinal)));

        clock.Advance(TimeSpan.FromSeconds(5587808));
        await client.SearchAsync("c", default);
        Assert.Equal(2, http.Requests.Count(r => r.Url.StartsWith(IgdbClient.TokenUrl, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ARejectedToken_IsRefreshedOnce()
    {
        var calls = 0;
        var (client, http, _) = Create(_ => ++calls == 1 ? FakeHttp.Json("{}", HttpStatusCode.Unauthorized) : FakeHttp.Json("[]"));

        await client.SearchAsync("a", default);

        Assert.Equal(2, http.Requests.Count(r => r.Url == IgdbClient.GamesUrl));
        Assert.Equal(2, http.Requests.Count(r => r.Url.StartsWith(IgdbClient.TokenUrl, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task QuotesInTheSearchTerm_CannotBreakOutOfTheQuery()
    {
        var (client, http, _) = Create();
        await client.SearchAsync("Evil\"; fields *; delete \"x\\", default);

        var body = http.Requests.Last().Body!;
        Assert.StartsWith("search \"Evil\\\"; fields *; delete \\\"x\\\\\"; fields name,", body);
    }

    [Fact]
    public async Task WrongSecret_IsReportedAsAnInvalidKey()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""{"status":403,"message":"invalid client secret"}""", HttpStatusCode.Forbidden));
        var client = new IgdbClient(http.Client(), () => ("cid", "bad"));
        var ex = await Assert.ThrowsAsync<MetadataException>(() => client.TestCredentialsAsync(default));
        Assert.Equal(MetadataFailure.InvalidKey, ex.Failure);
    }

    [Fact]
    public async Task MissingCredentials_MakeNoRequest()
    {
        var (client, http, _) = Create(noCreds: true);
        Assert.False(client.HasCredentials);
        await Assert.ThrowsAsync<MetadataException>(() => client.SearchAsync("x", default));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task RateLimiter_SpacesRequests()
    {
        var delays = new List<TimeSpan>();
        var limiter = new RateLimiter(TimeSpan.FromMilliseconds(250), (d, _) =>
        {
            delays.Add(d);
            return Task.CompletedTask;
        });

        for (var i = 0; i < 4; i++)
        {
            await limiter.WaitAsync(default);
        }

        // The first call is free; each later one is scheduled one gap after the previous (the fake delay does not
        // really wait, so the waits add up to roughly 250, 500 and 750 ms).
        Assert.Equal(3, delays.Count);
        Assert.InRange(delays[0].TotalMilliseconds, 1, 250);
        Assert.InRange(delays[1].TotalMilliseconds, 251, 500);
        Assert.InRange(delays[2].TotalMilliseconds, 501, 750);
    }
}
