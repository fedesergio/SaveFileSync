using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SaveFileSync.Core.Model;
using SaveFileSync.Metadata;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

/// <summary>One cover candidate; its image appears once it has been downloaded.</summary>
public sealed partial class CoverChoice : ObservableObject
{
    public required SgdbGrid Grid { get; init; }

    [ObservableProperty]
    private ImageSource? _image;

    public string Tooltip => $"by {Grid.Author ?? "unknown"}";
}

/// <summary>An IGDB result, or "none".</summary>
public sealed class NameChoice
{
    public IgdbGame? Game { get; init; }

    public override string ToString() => Game is null
        ? "(no official name)"
        : Game.Platforms.Count > 0 ? $"{Game.Display} · {string.Join(", ", Game.Platforms.Take(3))}" : Game.Display;
}

/// <summary>Lets the user correct the automatic match: which game, which cover, which official name (PLAN.md §11, phase 4).</summary>
public partial class CoverPickerWindow : FluentWindow
{
    private readonly AppHost _host;
    private readonly string _unitId;
    private readonly UnitDefinition _definition;
    private CancellationTokenSource _searchCts = new();
    private CancellationTokenSource _coversCts = new();
    private bool _updatingGames;

    public CoverPickerWindow(AppHost host, string unitId)
    {
        _host = host;
        _unitId = unitId;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        _definition = host.Service!.GetDefinition(unitId) ?? throw new InvalidOperationException("Unknown game.");
        Heading.Text = _definition.Name;
        SearchBox.Text = _definition.Source?.ManifestName ?? _definition.Name;

        var any = host.Metadata.HasCoverSource || host.Metadata.HasNameSource;
        NoKeyBar.IsOpen = !any;
        SearchButton.IsEnabled = any;
        Names.ItemsSource = new[] { new NameChoice() };
        Names.SelectedIndex = 0;

        Closed += (_, _) =>
        {
            _searchCts.Cancel();
            _coversCts.Cancel();
        };
        Loaded += async (_, _) =>
        {
            if (any)
            {
                await SearchAsync();
            }
        };
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchButton.IsEnabled)
        {
            e.Handled = true;
            _ = SearchAsync();
        }
    }

    private async void OnSearch(object sender, RoutedEventArgs e) => await SearchAsync();

    private async Task SearchAsync()
    {
        var term = SearchBox.Text.Trim();
        if (term.Length == 0)
        {
            return;
        }

        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        Status.Text = "Searching…";
        try
        {
            var result = await _host.Metadata.SearchAsync(term, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            ShowNames(result.OfficialNames, term);
            ShowGames(result.Games, term);

            var problems = new[] { result.GamesError, result.NamesError }.Where(p => p is not null).ToList();
            Status.Text = problems.Count > 0
                ? string.Join(" ", problems)
                : result.Games.Count == 0 && _host.Metadata.HasCoverSource ? "SteamGridDB has no game with that name. Try other words." : "";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowNames(IReadOnlyList<IgdbGame> games, string term)
    {
        var items = new List<NameChoice> { new() };
        items.AddRange(games.Select(g => new NameChoice { Game = g }));
        Names.ItemsSource = items;
        Names.IsEnabled = _host.Metadata.HasNameSource;

        var current = _definition.Metadata?.IgdbId;
        var selected = items.FirstOrDefault(i => i.Game is { } g && g.Id == current)
            ?? items.FirstOrDefault(i => i.Game is not null
                && NameMatcher.Similarity(term, i.Game.Name) >= MetadataService.AutoMatchThreshold);
        Names.SelectedItem = selected ?? items[0];
    }

    private void ShowGames(IReadOnlyList<SgdbGame> games, string term)
    {
        _updatingGames = true;
        Games.ItemsSource = games;
        _updatingGames = false;

        var wanted = _definition.Metadata?.SteamGridDbGameId;
        var selected = games.FirstOrDefault(g => g.Id == wanted)
            ?? NameMatcher.Best(term, games, g => [g.Name], MetadataService.AutoMatchThreshold)
            ?? games.FirstOrDefault();
        Games.SelectedItem = selected;
        if (selected is null)
        {
            Covers.ItemsSource = null;
            CoversTitle.Text = "Covers";
        }
    }

    private async void OnGameSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingGames || Games.SelectedItem is not SgdbGame game)
        {
            return;
        }

        _coversCts.Cancel();
        _coversCts = new CancellationTokenSource();
        var token = _coversCts.Token;
        CoversTitle.Text = $"Covers for {game.Name}";
        Covers.ItemsSource = null;
        try
        {
            var grids = await _host.Metadata.GetCoversAsync(game.Id, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var choices = grids.Take(30).Select(g => new CoverChoice { Grid = g }).ToList();
            Covers.ItemsSource = choices;
            if (choices.Count == 0)
            {
                CoversTitle.Text = $"No portrait covers for {game.Name}";
                return;
            }

            var currentUrl = _definition.Metadata?.CoverUrl;
            Covers.SelectedItem = choices.FirstOrDefault(c => c.Grid.Thumb == currentUrl) ?? choices[0];

            // A few downloads at a time: the images are small, but a burst of 30 requests is unfriendly.
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(choices.Select(choice => LoadThumbAsync(choice, gate, token)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (MetadataException ex)
        {
            Status.Text = ex.Message;
        }
    }

    private async Task LoadThumbAsync(CoverChoice choice, SemaphoreSlim gate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var path = await _host.Metadata.Covers.EnsureAsync(choice.Grid.Thumb, token);
            var image = await Task.Run(() => CoverImage.Load(path, 150), token);
            if (!token.IsCancellationRequested)
            {
                choice.Image = image;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async void OnUse(object sender, RoutedEventArgs e)
    {
        var current = _definition.Metadata ?? new UnitMetadata();
        var game = Games.SelectedItem as SgdbGame;
        var cover = Covers.SelectedItem as CoverChoice;
        var name = (Names.SelectedItem as NameChoice)?.Game;

        var choice = new UnitMetadata
        {
            // A new cover needs its game; without a SteamGridDB selection the existing cover stays.
            SteamGridDbGameId = game is not null && cover is not null ? game.Id : current.SteamGridDbGameId,
            CoverUrl = cover is not null && game is not null ? cover.Grid.Thumb : current.CoverUrl,
            CoverAuthor = cover is not null && game is not null ? cover.Grid.Author : current.CoverAuthor,
            // Without IGDB credentials on this PC the box is disabled: keep what another PC found.
            IgdbId = _host.Metadata.HasNameSource ? name?.Id : current.IgdbId,
            OfficialName = _host.Metadata.HasNameSource ? name?.Name : current.OfficialName,
        };

        UseButton.IsEnabled = false;
        try
        {
            await _host.Metadata.SaveChoiceAsync(_unitId, choice, default);
            Close();
        }
        catch (Exception ex)
        {
            UseButton.IsEnabled = true;
            MessageBox.Show(this, ex.Message, "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnReset(object sender, RoutedEventArgs e)
    {
        await _host.Metadata.ResetMatchAsync(_unitId);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
