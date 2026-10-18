using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using CineLibraryCS.Services.Tmdb;

namespace CineLibraryCS.Views;

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

    public TmdbPickerDialog(TmdbClient client, string? initialTitle, int? year, bool tvShows = false)
    {
        InitializeComponent();
        Loaded += (_, _) => CineLibraryCS.Services.LocalizationService.TranslateTree(this);
        // ContentDialog otherwise measures itself from the search controls (about 500 px),
        // even when MaxWidth is large. Set an explicit width from the host window's
        // available XamlRoot width so the result descriptions have room to wrap.
        Loaded += (_, _) =>
        {
            try
            {
                var availableWidth = XamlRoot?.Size.Width ?? 0;
                if (availableWidth > 0)
                {
                    Width = Math.Max(900, Math.Min(1800, availableWidth - 48));
                    MinWidth = Math.Min(900, Width);
                    MaxWidth = Math.Max(Width, availableWidth - 24);
                }
                else
                {
                    Width = 1400;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TMDb picker sizing failed: {ex.Message}");
                Width = 1400;
            }
        };
        _client = client;
        _tvShows = tvShows;
        if (tvShows)
        {
            Title = CineLibraryCS.Services.LocalizationService.TranslateText("Выбор сериала в TMDb");
            HintText.Text = CineLibraryCS.Services.LocalizationService.TranslateText("Выберите правильный сериал, чтобы сопоставить его с записью TMDb.");
            TitleBox.PlaceholderText = CineLibraryCS.Services.LocalizationService.TranslateText("Название сериала…");
        }
        TitleBox.Text = initialTitle ?? "";
        if (year is int y && y > 0) YearBox.Text = y.ToString();
        Loaded += async (_, _) => { if (!string.IsNullOrWhiteSpace(TitleBox.Text)) await DoSearchAsync(); };
    }

    private void OnTitleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; _ = DoSearchAsync(); }
    }

    private void OnSearch(object sender, RoutedEventArgs e) => _ = DoSearchAsync();

    private async Task DoSearchAsync()
    {
        var title = (TitleBox.Text ?? "").Trim();
        if (title.Length == 0) return;
        int? year = int.TryParse((YearBox.Text ?? "").Trim(), out var y) && y > 1800 ? y : null;

        SearchBtn.IsEnabled = false;
        Busy.IsActive = true;
        StatusText.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = false;
        try
        {
            var results = _tvShows
                ? (await _client.SearchTvAsync(title, year)).Select(t => t.AsSearchHit()).ToList()
                : await _client.SearchMovieAsync(title, year);
            if (results.Count == 0)
            {
                StatusText.Text = $"В TMDb не найдено совпадений для «{title}».";
                StatusText.Visibility = Visibility.Visible;
                ResultsList.ItemsSource = null;
                return;
            }
            var items = new List<TmdbResultItem>();
            foreach (var m in results)
                items.Add(new TmdbResultItem(m, _client.GetImageUrl(m.PosterPath, "w154")));
            ResultsList.ItemsSource = items;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TMDb picker search failed: {ex.Message}");
            StatusText.Text = CineLibraryCS.Services.LocalizationService.TranslateText("Couldn't reach TMDb. Check your connection and try again.");
            StatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            Busy.IsActive = false;
            SearchBtn.IsEnabled = true;
        }
    }

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        Picked = (ResultsList.SelectedItem as TmdbResultItem)?.Source;
        IsPrimaryButtonEnabled = Picked != null;
    }
}
