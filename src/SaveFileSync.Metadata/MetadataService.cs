using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SaveFileSync.Core;
using SaveFileSync.Core.IO;
using SaveFileSync.Core.Model;

namespace SaveFileSync.Metadata;

/// <summary>The games the app syncs, and the place where a match is saved (implemented by the app on top of <c>SyncService</c>).</summary>
public interface IUnitCatalog
{
    IReadOnlyList<UnitDefinition> GetDefinitions();

    /// <summary>Stores the match in the shared definition; false when nothing changed.</summary>
    Task<bool> SaveMetadataAsync(string unitId, UnitMetadata metadata);
}

public sealed record MetadataSearchResult(
    IReadOnlyList<SgdbGame> Games,
    IReadOnlyList<IgdbGame> OfficialNames,
    string? GamesError,
    string? NamesError);

/// <summary>
/// Phase 4: finds a cover (SteamGridDB) and an official name (IGDB) for each game and keeps the covers cached
/// on this PC. Everything here is optional and best effort: without keys it does nothing, and a failing
/// service never affects syncing. Only game names and Steam ids leave the PC (PLAN.md §11, phase 4).
/// </summary>
public sealed class MetadataService : IDisposable
{
    /// <summary>The same name must score at least this much to be accepted without asking (see <see cref="NameMatcher"/>).</summary>
    public const double AutoMatchThreshold = 0.85;

    private static readonly TimeSpan NoMatchRetry = TimeSpan.FromDays(30);
    private static readonly TimeSpan ErrorRetry = TimeSpan.FromHours(1);
    private static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(30);

    private readonly IUnitCatalog _catalog;
    private readonly SteamGridDbClient? _sgdb;
    private readonly IgdbClient? _igdb;
    private readonly CoverCache _covers;
    private readonly string _stateFile;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _stateLock = new();
    private Dictionary<string, Attempt> _attempts;
    private DateTime _pausedUntil;
    private bool _sgdbKeyRejected;
    private bool _igdbKeyRejected;
    private Task? _loop;

    public MetadataService(
        IUnitCatalog catalog,
        SteamGridDbClient? sgdb,
        IgdbClient? igdb,
        CoverCache covers,
        string stateFile,
        IClock clock,
        ILogger? log = null)
    {
        _catalog = catalog;
        _sgdb = sgdb;
        _igdb = igdb;
        _covers = covers;
        _stateFile = stateFile;
        _clock = clock;
        _log = log ?? NullLogger.Instance;
        _attempts = LoadAttempts(stateFile);
    }

    /// <summary>Raised (from a background thread) when a cover or a match changed. The argument is the unit id.</summary>
    public event Action<string>? UnitChanged;

    public bool HasCoverSource => _sgdb?.HasKey == true;

    public bool HasNameSource => _igdb?.HasCredentials == true;

    public CoverCache Covers => _covers;

    public string? CoverPath(UnitDefinition definition) => _covers.Find(definition.Metadata?.CoverUrl);

    /// <summary>Starts the background loop: a first pass shortly after start, then every 30 minutes or when asked.</summary>
    public void Start(TimeSpan? firstDelay = null)
    {
        _loop ??= Task.Run(() => LoopAsync(firstDelay ?? TimeSpan.FromSeconds(20), _stop.Token));
    }

    /// <summary>Asks for a pass soon (a game was added, a key was changed...).</summary>
    public void Trigger()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already pending
        }
    }

    /// <summary>Forget earlier failures and rejected keys, e.g. after the user entered a new key.</summary>
    public void ResetAttempts()
    {
        lock (_stateLock)
        {
            _attempts = [];
            _sgdbKeyRejected = false;
            _igdbKeyRejected = false;
            _pausedUntil = default;
            SaveAttempts();
        }

        Trigger();
    }

    // ---------------------------------------------------------------- the pass

    /// <summary>One pass over all games: match what is unmatched, then download missing covers.</summary>
    public async Task RunPassAsync(CancellationToken token)
    {
        await _passGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var definitions = _catalog.GetDefinitions().Where(d => !d.Removed).ToList();
            foreach (var definition in definitions)
            {
                token.ThrowIfCancellationRequested();
                if (_clock.UtcNow < _pausedUntil)
                {
                    break;
                }

                await MatchAsync(definition, token).ConfigureAwait(false);
            }

            // Covers other PCs chose (or earlier passes matched) but this PC has not downloaded yet.
            foreach (var definition in _catalog.GetDefinitions().Where(d => !d.Removed))
            {
                token.ThrowIfCancellationRequested();
                var url = definition.Metadata?.CoverUrl;
                if (url is null || _covers.Find(url) is not null || !Allowed(definition.UnitId, "cover"))
                {
                    continue;
                }

                if (await _covers.EnsureAsync(url, token).ConfigureAwait(false) is not null)
                {
                    UnitChanged?.Invoke(definition.UnitId);
                }
                else
                {
                    Record(definition.UnitId, "cover", Outcome.Error);
                }
            }

            if (definitions.Count > 0)
            {
                var used = _catalog.GetDefinitions().Select(d => d.Metadata?.CoverUrl).OfType<string>().ToHashSet(StringComparer.Ordinal);
                _covers.Prune(used, _clock.UtcNow);
            }
        }
        finally
        {
            _passGate.Release();
        }
    }

    private async Task MatchAsync(UnitDefinition definition, CancellationToken token)
    {
        var metadata = definition.Metadata ?? new UnitMetadata();
        if (metadata.Confirmed)
        {
            return;
        }

        var term = definition.Source?.ManifestName ?? definition.Name;
        var updated = metadata;

        if (_sgdb is { HasKey: true } && !_sgdbKeyRejected && metadata.CoverUrl is null && Allowed(definition.UnitId, "sgdb"))
        {
            updated = await MatchCoverAsync(definition, term, updated, token).ConfigureAwait(false);
        }

        if (_igdb is { HasCredentials: true } && !_igdbKeyRejected && metadata.IgdbId is null && Allowed(definition.UnitId, "igdb"))
        {
            updated = await MatchNameAsync(definition, term, updated, token).ConfigureAwait(false);
        }

        if (!ReferenceEquals(updated, metadata) && await _catalog.SaveMetadataAsync(definition.UnitId, updated).ConfigureAwait(false))
        {
            _log.LogInformation("Metadata for {Unit} updated", definition.UnitId);
            if (updated.CoverUrl is { } url)
            {
                await _covers.EnsureAsync(url, token).ConfigureAwait(false);
            }

            UnitChanged?.Invoke(definition.UnitId);
        }
    }

    private async Task<UnitMetadata> MatchCoverAsync(UnitDefinition definition, string term, UnitMetadata metadata, CancellationToken token)
    {
        try
        {
            var gameId = metadata.SteamGridDbGameId;
            if (gameId is null)
            {
                SgdbGame? game = null;
                if (definition.Source?.SteamId is { } steamId)
                {
                    game = await _sgdb!.GetGameBySteamIdAsync(steamId, token).ConfigureAwait(false);
                }

                if (game is null)
                {
                    var found = await _sgdb!.SearchAsync(term, token).ConfigureAwait(false);
                    game = NameMatcher.Best(term, found.OrderByDescending(g => g.Verified), g => [g.Name], AutoMatchThreshold);
                }

                if (game is null)
                {
                    Record(definition.UnitId, "sgdb", Outcome.NoMatch);
                    return metadata;
                }

                gameId = game.Id;
            }

            var covers = await _sgdb!.GetCoversAsync(gameId.Value, token).ConfigureAwait(false);
            var best = covers.FirstOrDefault(c => CoverCache.IsAllowedUrl(c.Thumb));
            if (best is null)
            {
                Record(definition.UnitId, "sgdb", Outcome.NoMatch);
                return Copy(metadata, steamGridDbGameId: gameId);
            }

            Forget(definition.UnitId, "sgdb");
            return Copy(metadata, steamGridDbGameId: gameId, coverUrl: best.Thumb, coverAuthor: best.Author);
        }
        catch (MetadataException ex)
        {
            HandleFailure(definition.UnitId, "sgdb", ex);
            return metadata;
        }
    }

    private async Task<UnitMetadata> MatchNameAsync(UnitDefinition definition, string term, UnitMetadata metadata, CancellationToken token)
    {
        try
        {
            var found = await _igdb!.SearchAsync(term, token).ConfigureAwait(false);
            var game = NameMatcher.Best(term, found, g => g.AlternativeNames.Prepend(g.Name), AutoMatchThreshold);
            if (game is null)
            {
                Record(definition.UnitId, "igdb", Outcome.NoMatch);
                return metadata;
            }

            Forget(definition.UnitId, "igdb");
            return Copy(metadata, igdbId: game.Id, officialName: game.Name);
        }
        catch (MetadataException ex)
        {
            HandleFailure(definition.UnitId, "igdb", ex);
            return metadata;
        }
    }

    private void HandleFailure(string unitId, string source, MetadataException ex)
    {
        _log.LogWarning("{Source} lookup failed: {Failure} ({Message})", source, ex.Failure, ex.Message);
        switch (ex.Failure)
        {
            case MetadataFailure.InvalidKey:
                // Do not hammer a service with a key it refused; the next key change resets this.
                if (source == "sgdb")
                {
                    _sgdbKeyRejected = true;
                }
                else
                {
                    _igdbKeyRejected = true;
                }

                break;
            case MetadataFailure.RateLimited:
                _pausedUntil = _clock.UtcNow + (ex.RetryAfter ?? TimeSpan.FromMinutes(15));
                break;
            default:
                Record(unitId, source, Outcome.Error);
                break;
        }
    }

    private static UnitMetadata Copy(
        UnitMetadata m,
        long? steamGridDbGameId = null,
        string? coverUrl = null,
        string? coverAuthor = null,
        long? igdbId = null,
        string? officialName = null) => new()
    {
        SteamGridDbGameId = steamGridDbGameId ?? m.SteamGridDbGameId,
        CoverUrl = coverUrl ?? m.CoverUrl,
        CoverAuthor = coverAuthor ?? m.CoverAuthor,
        IgdbId = igdbId ?? m.IgdbId,
        OfficialName = officialName ?? m.OfficialName,
        Confirmed = m.Confirmed,
    };

    // ---------------------------------------------------------------- what the user can do

    /// <summary>Searches both sources for the picker window. A source without a key, or failing, is reported in its error field.</summary>
    public async Task<MetadataSearchResult> SearchAsync(string term, CancellationToken token)
    {
        IReadOnlyList<SgdbGame> games = [];
        IReadOnlyList<IgdbGame> names = [];
        string? gamesError = null;
        string? namesError = null;

        if (_sgdb is { HasKey: true })
        {
            try
            {
                games = await _sgdb.SearchAsync(term, token).ConfigureAwait(false);
            }
            catch (MetadataException ex)
            {
                gamesError = ex.Message;
            }
        }

        if (_igdb is { HasCredentials: true })
        {
            try
            {
                names = await _igdb.SearchAsync(term, token).ConfigureAwait(false);
            }
            catch (MetadataException ex)
            {
                namesError = ex.Message;
            }
        }

        return new MetadataSearchResult(games, names, gamesError, namesError);
    }

    public Task<IReadOnlyList<SgdbGrid>> GetCoversAsync(long gameId, CancellationToken token) =>
        _sgdb is null ? Task.FromResult<IReadOnlyList<SgdbGrid>>([]) : _sgdb.GetCoversAsync(gameId, token);

    /// <summary>Stores a choice made by the user; automatic matching leaves it alone from now on.</summary>
    public async Task SaveChoiceAsync(string unitId, UnitMetadata choice, CancellationToken token)
    {
        if (choice.CoverUrl is { } url)
        {
            await _covers.EnsureAsync(url, token).ConfigureAwait(false);
        }

        var confirmed = new UnitMetadata
        {
            SteamGridDbGameId = choice.SteamGridDbGameId,
            CoverUrl = choice.CoverUrl,
            CoverAuthor = choice.CoverAuthor,
            IgdbId = choice.IgdbId,
            OfficialName = choice.OfficialName,
            Confirmed = true,
        };
        await _catalog.SaveMetadataAsync(unitId, confirmed).ConfigureAwait(false);
        UnitChanged?.Invoke(unitId);
    }

    /// <summary>Drops the match (including a confirmed one) and lets automatic matching try again.</summary>
    public async Task ResetMatchAsync(string unitId)
    {
        await _catalog.SaveMetadataAsync(unitId, new UnitMetadata()).ConfigureAwait(false);
        Forget(unitId, "sgdb");
        Forget(unitId, "igdb");
        Forget(unitId, "cover");
        UnitChanged?.Invoke(unitId);
        Trigger();
    }

    // ---------------------------------------------------------------- local retry bookkeeping

    private enum Outcome
    {
        NoMatch,
        Error,
    }

    private sealed class Attempt
    {
        public DateTime Utc { get; set; }

        public Outcome Outcome { get; set; }
    }

    private bool Allowed(string unitId, string source)
    {
        lock (_stateLock)
        {
            if (!_attempts.TryGetValue(unitId + "|" + source, out var attempt))
            {
                return true;
            }

            var wait = attempt.Outcome == Outcome.Error ? ErrorRetry : NoMatchRetry;
            return _clock.UtcNow - attempt.Utc >= wait;
        }
    }

    private void Record(string unitId, string source, Outcome outcome)
    {
        lock (_stateLock)
        {
            _attempts[unitId + "|" + source] = new Attempt { Utc = _clock.UtcNow, Outcome = outcome };
            SaveAttempts();
        }
    }

    private void Forget(string unitId, string source)
    {
        lock (_stateLock)
        {
            if (_attempts.Remove(unitId + "|" + source))
            {
                SaveAttempts();
            }
        }
    }

    private void SaveAttempts()
    {
        try
        {
            AtomicFile.WriteAllBytes(_stateFile, Json.Serialize(_attempts), "meta");
        }
        catch (IOException ex)
        {
            _log.LogDebug(ex, "Could not save the metadata state");
        }
    }

    private static Dictionary<string, Attempt> LoadAttempts(string path)
    {
        try
        {
            return File.Exists(path)
                ? Json.Deserialize<Dictionary<string, Attempt>>(File.ReadAllBytes(path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    // ---------------------------------------------------------------- loop

    private async Task LoopAsync(TimeSpan firstDelay, CancellationToken token)
    {
        try
        {
            await Task.Delay(firstDelay, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RunPassAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Metadata pass failed");
                }

                await _wake.WaitAsync(PassInterval, token).ConfigureAwait(false);
                // Several triggers in a row (a batch of games being added) become one pass.
                await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}
