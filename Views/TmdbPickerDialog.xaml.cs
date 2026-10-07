using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using CineМедиатекаCS.Services.Tmdb;

namespace CineМедиатекаCS.Views;

/// <summary>
/// v3.4 — lightweight TMDb match picker used by "Fetch missing info" when a
/// movie has no stored tmdb_id, so the user confirms the right film before any
/// blanks are filled. Reuses <see cref="TmdbResultItem"/>.
/// </summary>
public sealed partial class TmdbPickerDialog : ContentDialog
{
    private readonly TmdbClient _client;
    private readonly bool _tvShows;

    /// <summary>The film (or, with tvShows, the show) the user chose, or null if
    /// cancelled. A show comes back movie-shaped; its TmdbId is the show's id.</summary>
    public TmdbMovie? Picked { get; private set; }

    public TmdbPickerDialog(TmdbClient client, string? initialНазвание, int? year, bool tvShows = false)
    {
        InitializeComponent();
        _client = client;
        _tvShows = tvShows;
        if (tvShows)
        {
            Название = "Match this show on TMDb";
            HintText.Text = "Pick the correct show so the missing details are filled from the right entry.";
            НазваниеBox.PlaceholderText = "Show title…";
        }
        НазваниеBox.Text = initialНазвание ?? "";
        if (year is int y && y > 0) ГодBox.Text = y.ToString();
        Loaded += async (_, _) => { if (!string.IsNullOrWhiteSpace(НазваниеBox.Text)) await DoПоискAsync(); };
    }

    private void Вкл.НазваниеKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; _ = DoПоискAsync(); }
    }

    private void Вкл.Поиск(object sender, RoutedEventArgs e) => _ = DoПоискAsync();

    private async Task DoПоискAsync()
    {
        var title = (НазваниеBox.Text ?? "").Trim();
        if (title.Length == 0) return;
        int? year = int.TryParse((ГодBox.Text ?? "").Trim(), out var y) && y > 1800 ? y : null;

        ПоискBtn.IsEnabled = false;
        Busy.IsActive = true;
        StatusText.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = false;
        try
        {
            var results = _tvShows
                ? (await _client.ПоискTvAsync(title, year)).Select(t => t.AsПоискHit()).ToList()
                : await _client.ПоискMovieAsync(title, year);
            if (results.Count == 0)
            {
                StatusText.Text = $"No TMDb matches for “{title}”.";
                StatusText.Visibility = Visibility.Visible;
                ResultsList.ItemsSource = null;
                return;
            }
            var items = new List<TmdbResultItem>();
            foreach (var m in results)
                items.Добавить(new TmdbResultItem(m, _client.GetImageUrl(m.PosterPath, "w154")));
            ResultsList.ItemsSource = items;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TMDb picker search failed: {ex.Message}");
            StatusText.Text = "Couldn't reach TMDb. Check your connection and try again.";
            StatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            Busy.IsActive = false;
            ПоискBtn.IsEnabled = true;
        }
    }

    private void Вкл.ResultSelected(object sender, SelectionChangedEventArgs e)
    {
        Picked = (ResultsList.SelectedItem as TmdbResultItem)?.Source;
        IsPrimaryButtonEnabled = Picked != null;
    }
}
