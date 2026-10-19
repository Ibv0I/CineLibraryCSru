using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CineLibraryCS.Services.Tmdb;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml;

namespace CineLibraryCS.Views;

public sealed partial class MoviePreparationPage : Page
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".webm", ".vob" };
    private readonly ObservableCollection<MovieFolderCandidate> _candidates = new();

    public MoviePreparationPage()
    {
        InitializeComponent();
        FoldersList.ItemsSource = _candidates;
    }

    private async void OnChooseFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                RootPathBox.Text = folder.Path;
                // Scan immediately after the user chooses a folder.
                OnScanClick(this, new RoutedEventArgs());
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось открыть выбор папки: " + ex.Message;
        }
    }

    private void OnScanClick(object sender, RoutedEventArgs e)
    {
        var root = (RootPathBox.Text ?? string.Empty).Trim().Trim('"');
        if (!Directory.Exists(root))
        {
            StatusText.Text = "Указанная папка не существует или недоступна.";
            return;
        }

        _candidates.Clear();
        StatusText.Text = "Поиск папок с видео без movie.nfo…";
        try
        {
            var dirs = new List<string> { root };
            try { dirs.AddRange(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string[] videos;
                try { videos = Directory.EnumerateFiles(dir).Where(f => VideoExtensions.Contains(Path.GetExtension(f))).ToArray(); }
                catch { continue; }
                if (videos.Length == 0) continue;
                // A folder is already prepared if Kodi's standard NFO exists.
                if (File.Exists(Path.Combine(dir, "movie.nfo"))) continue;
                _candidates.Add(new MovieFolderCandidate(dir, videos));
            }
            StatusText.Text = _candidates.Count == 0
                ? "Папки с видео без movie.nfo не найдены."
                : $"Найдено папок: {_candidates.Count}. Выберите правильное совпадение TMDb для каждого фильма.";
        }
        catch (Exception ex) { StatusText.Text = "Не удалось просканировать папку: " + ex.Message; }
    }

    private async void OnPickMovieClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MovieFolderCandidate candidate) return;
        var year = candidate.Year;
        using var client = new TmdbClient(language: "ru-RU");
        var picker = new TmdbPickerDialog(client, candidate.SearchTitle, year);
        var result = await picker.ShowAsync();
        if (result != ContentDialogResult.Primary || picker.Picked == null) return;
        var preview = new TmdbMoviePreviewDialog(client, picker.Picked);
        var previewResult = await preview.ShowAsync();
        if (previewResult != ContentDialogResult.Primary || preview.Movie == null) return;

        candidate.SelectedMovie = preview.Movie;
        candidate.SelectedPosterPath = preview.SelectedPosterPath ?? preview.Movie.PosterPath;
        candidate.SelectedBackdropPath = preview.SelectedBackdropPath ?? preview.Movie.BackdropPath;
        candidate.MatchSummary = $"Готово к сохранению: {preview.Movie.Title}{(preview.Movie.Year > 0 ? $" ({preview.Movie.Year})" : string.Empty)} · TMDb ID: {preview.Movie.TmdbId}";
        RefreshCandidates();
        StatusText.Text = $"Для папки «{candidate.FolderName}» выбраны метаданные и изображения. Нажмите «Сохранить файлы», чтобы создать movie.nfo, poster.jpg и fanart.jpg.";
    }

    private void RefreshCandidates()
    {
        FoldersList.ItemsSource = null;
        FoldersList.ItemsSource = _candidates;
    }

    private async void OnSavePreparedMovieClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MovieFolderCandidate candidate || candidate.SelectedMovie is not TmdbMovie movie)
        {
            StatusText.Text = "Сначала выберите совпадение фильма в TMDb.";
            return;
        }

        var nfoPath = Path.Combine(candidate.FolderPath, "movie.nfo");
        var posterPath = Path.Combine(candidate.FolderPath, "poster.jpg");
        var fanartPath = Path.Combine(candidate.FolderPath, "fanart.jpg");
        var existing = new[] { nfoPath, posterPath, fanartPath }.Where(File.Exists).Select(Path.GetFileName).ToArray();
        if (existing.Length > 0)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Файлы уже существуют",
                Content = "Будут заменены: " + string.Join(", ", existing) + ". Продолжить?",
                PrimaryButtonText = "Заменить",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        var tempSuffix = ".tmp-" + Guid.NewGuid().ToString("N");
        var tempNfo = nfoPath + tempSuffix;
        var tempPoster = posterPath + tempSuffix;
        var tempFanart = fanartPath + tempSuffix;
        try
        {
            StatusText.Text = $"Сохранение метаданных и изображений: {candidate.FolderName}…";
            using var client = new TmdbClient(language: "ru-RU");
            var posterFile = candidate.SelectedPosterPath ?? movie.PosterPath;
            var backdropFile = candidate.SelectedBackdropPath ?? movie.BackdropPath;
            if (string.IsNullOrWhiteSpace(posterFile))
                throw new InvalidOperationException("Не выбран постер, а TMDb не предоставил постер по умолчанию.");
            if (string.IsNullOrWhiteSpace(backdropFile))
                throw new InvalidOperationException("Не выбран фон, а TMDb не предоставил фон по умолчанию.");

            if (!await client.DownloadImageAsync(client.GetImageUrl(posterFile, "original"), tempPoster))
                throw new IOException("Не удалось загрузить постер из TMDb.");
            if (!await client.DownloadImageAsync(client.GetImageUrl(backdropFile, "original"), tempFanart))
                throw new IOException("Не удалось загрузить фоновое изображение из TMDb.");

            WriteMovieNfo(tempNfo, movie);
            File.Move(tempPoster, posterPath, true);
            File.Move(tempFanart, fanartPath, true);
            File.Move(tempNfo, nfoPath, true); // Write NFO last, so scanner only sees completed preparation.

            candidate.MatchSummary = $"Сохранено: {movie.Title}{(movie.Year > 0 ? $" ({movie.Year})" : string.Empty)} · movie.nfo, poster.jpg, fanart.jpg";
            RefreshCandidates();
            StatusText.Text = $"Файлы успешно сохранены в папке «{candidate.FolderName}».";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось сохранить подготовку фильма: " + ex.Message;
        }
        finally
        {
            TryDelete(tempNfo); TryDelete(tempPoster); TryDelete(tempFanart);
        }
    }

    private static void WriteMovieNfo(string path, TmdbMovie movie)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false), CloseOutput = true };
        using var writer = XmlWriter.Create(path, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("movie");
        WriteElement(writer, "title", movie.Title);
        WriteElement(writer, "originaltitle", movie.OriginalTitle);
        WriteElement(writer, "sorttitle", movie.Title);
        if (movie.Year > 0) WriteElement(writer, "year", movie.Year.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(movie.Tagline)) WriteElement(writer, "tagline", movie.Tagline);
        if (!string.IsNullOrWhiteSpace(movie.Overview))
        {
            WriteElement(writer, "plot", movie.Overview);
            WriteElement(writer, "outline", movie.Overview);
        }
        if (!string.IsNullOrWhiteSpace(movie.ReleaseDate))
        {
            WriteElement(writer, "premiered", movie.ReleaseDate);
            WriteElement(writer, "releasedate", movie.ReleaseDate);
        }
        if (movie.Runtime > 0) WriteElement(writer, "runtime", movie.Runtime.ToString(CultureInfo.InvariantCulture));
        WriteElement(writer, "rating", movie.Rating.ToString("0.0", CultureInfo.InvariantCulture));
        WriteElement(writer, "votes", movie.VoteCount.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(movie.Certification)) WriteElement(writer, "mpaa", movie.Certification);
        foreach (var genre in movie.Genres) WriteElement(writer, "genre", genre.Name);
        foreach (var director in movie.Directors) WriteElement(writer, "director", director);
        foreach (var writerName in movie.Writers) WriteElement(writer, "credits", writerName);
        foreach (var company in movie.ProductionCompanies) WriteElement(writer, "studio", company.Name);
        foreach (var country in movie.ProductionCountries) WriteElement(writer, "country", country.Name);
        if (!string.IsNullOrWhiteSpace(movie.ImdbId))
        {
            WriteElement(writer, "imdbid", movie.ImdbId);
            writer.WriteStartElement("uniqueid"); writer.WriteAttributeString("type", "imdb"); writer.WriteAttributeString("default", "false"); writer.WriteString(movie.ImdbId); writer.WriteEndElement();
        }
        writer.WriteStartElement("uniqueid"); writer.WriteAttributeString("type", "tmdb"); writer.WriteAttributeString("default", "true"); writer.WriteString(movie.TmdbId.ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement();
        writer.WriteStartElement("ratings");
        writer.WriteStartElement("rating"); writer.WriteAttributeString("name", "themoviedb"); writer.WriteAttributeString("max", "10"); writer.WriteAttributeString("default", "true");
        WriteElement(writer, "value", movie.Rating.ToString("0.0", CultureInfo.InvariantCulture));
        WriteElement(writer, "votes", movie.VoteCount.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement(); writer.WriteEndElement();
        foreach (var actor in movie.Cast)
        {
            writer.WriteStartElement("actor");
            WriteElement(writer, "name", actor.Name);
            if (!string.IsNullOrWhiteSpace(actor.Character)) WriteElement(writer, "role", actor.Character);
            if (!string.IsNullOrWhiteSpace(actor.ProfilePath)) WriteElement(writer, "thumb", "https://image.tmdb.org/t/p/original" + actor.ProfilePath);
            WriteElement(writer, "order", actor.Order.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }
        writer.WriteStartElement("thumb"); writer.WriteAttributeString("aspect", "poster"); writer.WriteString("poster.jpg"); writer.WriteEndElement();
        writer.WriteStartElement("fanart"); writer.WriteStartElement("thumb"); writer.WriteString("fanart.jpg"); writer.WriteEndElement(); writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteElement(XmlWriter writer, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        writer.WriteElementString(name, value);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

public sealed class MovieFolderCandidate
{
    public string FolderPath { get; }
    public string FolderName => Path.GetFileName(FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    public string[] Videos { get; }
    public string VideoSummary => Videos.Length == 1 ? Path.GetFileName(Videos[0]) : $"Видео: {Videos.Length} файла(ов) · {string.Join(", ", Videos.Take(3).Select(f => Path.GetFileName(f)))}{(Videos.Length > 3 ? "…" : string.Empty)}";
    public string SearchTitle { get; }
    public int? Year { get; }
    public TmdbMovie? SelectedMovie { get; set; }
    public string? SelectedPosterPath { get; set; }
    public string? SelectedBackdropPath { get; set; }
    public string MatchSummary { get; set; } = "Совпадение TMDb не выбрано";

    public MovieFolderCandidate(string path, string[] videos)
    {
        FolderPath = path;
        Videos = videos;
        var name = FolderName;
        var match = System.Text.RegularExpressions.Regex.Match(name, @"\((19|20)\d{2}\)\s*$");
        if (match.Success && int.TryParse(match.Value.Trim('(', ')'), out var y))
        { Year = y; SearchTitle = name[..match.Index].Trim(); }
        else SearchTitle = System.Text.RegularExpressions.Regex.Replace(name, @"[._]+", " ").Trim();
    }
}
