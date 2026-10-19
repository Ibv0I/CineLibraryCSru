using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CineLibraryCS.Services.Tmdb;
using System.Collections.ObjectModel;

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
        var picker = new TmdbPickerDialog(client, candidate.SearchTitle, year) { XamlRoot = XamlRoot };
        picker.PrepareForDisplay();
        var result = await picker.ShowAsync();
        if (result != ContentDialogResult.Primary || picker.Picked == null) return;
        candidate.SelectedMovie = picker.Picked;
        candidate.MatchSummary = $"Выбрано: {picker.Picked.Title}{(picker.Picked.Year > 0 ? $" ({picker.Picked.Year})" : string.Empty)} · TMDb ID: {picker.Picked.TmdbId}";
        FoldersList.ItemsSource = null;
        FoldersList.ItemsSource = _candidates;
        StatusText.Text = $"Сопоставление сохранено в текущем сеансе для папки «{candidate.FolderName}». Файлы пока не записываются.";
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