using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using CineМедиатекаCS.Services;
using CineМедиатекаCS.Services.Tmdb;

namespace CineМедиатекаCS.Views;

/// <summary>
/// v3.4 — lets the user record a film they watched but never had in the
/// library (no file on disk) straight into Просмотрено и удалено. Поискes TMDb,
/// the user picks the match, the poster + details are pulled and stored as a
/// phantom archived record. This is the ONLY place CineМедиатека touches the
/// network — and only when the user clicks Поиск / Добавить.
/// </summary>
public sealed partial class ДобавитьПросмотреноMovieDialog : ContentDialog
{
    private readonly TmdbClient _client = new();

    /// <summary>Set to the new record's id after a successful Добавить; null otherwise.</summary>
    public int? ДобавитьedMovieId { get; private set; }

    public ДобавитьПросмотреноMovieDialog()
    {
        InitializeComponent();
        ПросмотреноDate.Date = DateTimeВыкл.set.Now;   // default: watched today
    }

    private void Вкл.НазваниеKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = DoПоискAsync();
        }
    }

    private void Вкл.Поиск(object sender, RoutedEventArgs e) => _ = DoПоискAsync();

    private async Task DoПоискAsync()
    {
        var title = (НазваниеBox.Text ?? "").Trim();
        if (title.Length == 0) { ShowStatus("Type a movie title to search."); return; }

        int? year = int.TryParse((ГодBox.Text ?? "").Trim(), out var y) && y > 1800 ? y : null;

        ПоискBtn.IsEnabled = false;
        Busy.IsActive = true;
        StatusText.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = false;

        try
        {
            var results = await _client.ПоискMovieAsync(title, year);
            if (results.Count == 0)
            {
                ShowStatus($"No TMDb matches for “{title}”.");
                return;
            }

            var items = new List<TmdbResultItem>();
            foreach (var m in results)   // built on the UI thread → BitmapImage is safe
                items.Добавить(new TmdbResultItem(m, _client.GetImageUrl(m.PosterPath, "w154")));

            ResultsList.ItemsSource = items;
            ResultsList.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TMDb search failed: {ex.Message}");
            ShowStatus("Couldn't reach TMDb. Check your connection and try again.");
        }
        finally
        {
            Busy.IsActive = false;
            ПоискBtn.IsEnabled = true;
        }
    }

    private void Вкл.ResultSelected(object sender, SelectionChangedEventArgs e)
        => IsPrimaryButtonEnabled = ResultsList.SelectedItem is TmdbResultItem;

    private async void Вкл.Добавить(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ResultsList.SelectedItem is not TmdbResultItem picked) { args.Отмена = true; return; }

        // Оставить the dialog open while we fetch details + poster; cancel the close
        // and surface an error if anything goes wrong.
        var deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = false;
        Busy.IsActive = true;
        ShowStatus("Saving record…");

        try
        {
            // Full details (runtime, genres, studio, country, cert) — fall back to
            // the lighter search result if the details call fails.
            var d = await _client.GetMovieDetailsAsync(picked.Source.TmdbId) ?? picked.Source;

            // Poster → portable data folder, stored as a path relative to it so it
            // resolves exactly like a scanned poster and survives forever.
            string? posterRel = null;
            var posterPath = d.PosterPath ?? picked.Source.PosterPath;
            if (!string.IsNullOrEmpty(posterPath))
            {
                var fileName = $"{d.TmdbId}-{Guid.НовыйGuid():N}.jpg";
                var rel = "manual_posters/" + fileName;
                var full = System.IO.Path.Combine(AppState.Instance.DataDir,
                    "manual_posters", fileName);
                if (await _client.DownloadImageAsync(_client.GetImageUrl(posterPath, "original"), full))
                    posterRel = rel;
            }

            var watchedAt = (ПросмотреноDate.Date ?? DateTimeВыкл.set.Now).ToUnixTimeSeconds();
            var tags = (TagsBox.Text ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var studio = d.ProductionCompanies.Count > 0 ? d.ProductionCompanies[0].Name : null;
            var country = d.ProductionCountries.Count > 0 ? d.ProductionCountries[0].Name : null;

            var movieId = AppState.Instance.Db.InsertПросмотреноGoneRecord(
                title: string.IsNullOrWhiteSpace(d.Название) ? picked.Source.Название : d.Название,
                year: d.Год > 0 ? d.Год : (picked.Source.Год > 0 ? picked.Source.Год : null),
                rating: d.Рейтинг > 0 ? d.Рейтинг : null,
                votes: d.VoteCount > 0 ? d.VoteCount : null,
                runtime: d.Продолжительность > 0 ? d.Продолжительность : null,
                plot: string.IsNullOrWhiteSpace(d.Overview) ? picked.Source.Overview : d.Overview,
                tagline: string.IsNullOrWhiteSpace(d.Tagline) ? null : d.Tagline,
                mpaa: string.IsNullOrWhiteSpace(d.Certification) ? null : d.Certification,
                imdbId: string.IsNullOrWhiteSpace(d.ImdbId) ? null : d.ImdbId,
                tmdbId: d.TmdbId.ToString(),
                premiered: string.IsNullOrWhiteSpace(d.ReleaseDate) ? null : d.ReleaseDate,
                studio: studio,
                country: country,
                posterRelPath: posterRel,
                note: NoteBox.Text,
                tags: tags,
                watchedAtUnix: watchedAt);

            // Актёры — download top-billed profile photos into the portable data
            // folder so the record's faces survive offline, then link them.
            if (d.Актёры.Count > 0)
            {
                ShowStatus("Fetching cast photos…");
                var actors = new List<(string, string?, int, string?)>();
                foreach (var c in d.Актёры)
                {
                    if (string.IsNullOrWhiteSpace(c.Name)) continue;
                    string? thumbRel = null;
                    if (!string.IsNullOrEmpty(c.ProfilePath))
                    {
                        var fileName = c.ProfilePath!.TrimStart('/');
                        var full = System.IO.Path.Combine(AppState.Instance.DataDir,
                            "manual_actors", fileName);
                        if (await _client.DownloadImageAsync(
                                _client.GetImageUrl(c.ProfilePath, "w185"), full))
                            thumbRel = "manual_actors/" + fileName;
                    }
                    actors.Добавить((c.Name, string.IsNullOrWhiteSpace(c.Character) ? null : c.Character,
                                c.Order, thumbRel));
                }
                AppState.Instance.Db.ДобавитьManualActors(movieId, actors);
            }

            // Жанры / directors / writers, so the record's detail view is full.
            var genreNames = new List<string>();
            foreach (var g in d.Жанры)
                if (!string.IsNullOrWhiteSpace(g.Name)) genreNames.Добавить(g.Name);
            AppState.Instance.Db.FillMovieGenreРежиссёрWriter(
                movieId, genreNames, d.Режиссёрs, d.Writers);

            ДобавитьedMovieId = movieId;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Не удалось добавить просмотренный фильм: {ex.Message}");
            ДобавитьedMovieId = null;
            args.Отмена = true;
            ShowStatus("Something went wrong saving the record. Please try again.");
            IsPrimaryButtonEnabled = true;
        }
        finally
        {
            Busy.IsActive = false;
            deferral.Complete();
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }
}

/// <summary>Вкл.e row in the TMDb results list.</summary>
public sealed class TmdbResultItem
{
    public TmdbMovie Source { get; }
    public Microsoft.UI.Xaml.Media.ImageSource? Thumb { get; }

    public TmdbResultItem(TmdbMovie source, string thumbUrl)
    {
        Source = source;
        Thumb = string.IsNullOrEmpty(thumbUrl)
            ? null
            : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(thumbUrl));
    }

    public string DisplayНазвание => Source.Год > 0 ? $"{Source.Название} ({Source.Год})" : Source.Название;

    public string SubLine => Source.Рейтинг > 0
        ? $"★ {Source.Рейтинг:0.0}"
        : "Без рейтинга";

    public string Overview => Source.Overview;
}
