using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CineLibraryCS.Views;

public sealed partial class MediaPreparationPage : Page
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".flv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts", ".iso" };

    private string? _rootPath;
    private CancellationTokenSource? _scanCts;
    private readonly ObservableCollection<MediaFolderCandidate> _results = new();

    public MediaPreparationPage()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _results;
        Loaded += (_, _) => ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        var ru = CineLibraryCS.Services.LocalizationService.CurrentLanguage.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        PageTitle.Text = ru ? "Подготовка медиатеки" : "Media library preparation";
        PageDescription.Text = ru
            ? "Первый этап: найдите папки с видео, для которых ещё не создан NFO. Файлы фильмов не изменяются."
            : "Stage one: find video folders that do not have an NFO yet. Movie files are not modified.";
        ChooseRootButton.Content = ru ? "Выбрать папку" : "Choose folder";
        ScanButton.Content = ru ? "Найти папки без NFO" : "Find folders without NFO";
        EmptyTitle.Text = ru ? "Пока ничего не найдено" : "Nothing found yet";
        EmptyDescription.Text = ru ? "Выберите корневую папку и запустите поиск. Будут показаны папки с видео без NFO." : "Choose a root folder and start scanning. Video folders without NFO files will be listed.";
        FooterNote.Text = ru
            ? "Ничего не записывается и не переименовывается. Сопоставление с TMDb и создание NFO будут добавлены на следующих этапах."
            : "Nothing is written or renamed. TMDb matching and NFO creation will be added in later stages.";
        RootLabel.Text = _rootPath ?? (ru ? "Корневая папка не выбрана" : "No root folder selected");
        if (_rootPath is null) StatusText.Text = ru ? "Выберите папку с фильмами, чтобы начать поиск." : "Choose a folder containing movies to begin.";
    }

    private async void ChooseRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Videos };
        picker.FileTypeFilter.Add("*");
        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _rootPath = folder.Path;
        RootLabel.Text = _rootPath;
        ScanButton.IsEnabled = true;
        _results.Clear();
        EmptyState.Visibility = Visibility.Visible;
        var ru = CineLibraryCS.Services.LocalizationService.CurrentLanguage.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        StatusText.Text = ru ? "Папка выбрана. Запустите поиск." : "Folder selected. Start scanning.";
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_rootPath)) return;
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        BusyRing.IsActive = true;
        ScanButton.IsEnabled = false;
        ChooseRootButton.IsEnabled = false;
        _results.Clear();
        EmptyState.Visibility = Visibility.Collapsed;
        var ru = CineLibraryCS.Services.LocalizationService.CurrentLanguage.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        StatusText.Text = ru ? "Поиск папок…" : "Scanning folders…";
        try
        {
            var candidates = await Task.Run(() => FindCandidates(_rootPath, token), token);
            foreach (var candidate in candidates.OrderBy(x => x.FolderPath, StringComparer.OrdinalIgnoreCase))
                _results.Add(candidate);
            StatusText.Text = ru
                ? $"Готово. Найдено папок без NFO: {_results.Count}."
                : $"Done. Folders without NFO found: {_results.Count}.";
            EmptyState.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_results.Count == 0) EmptyDescription.Text = ru
                ? "В выбранной папке не найдено видео без NFO. Попробуйте выбрать другую папку."
                : "No video folders without NFO were found. Try another folder.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = (ru ? "Ошибка поиска: " : "Scan error: ") + ex.Message;
            EmptyState.Visibility = Visibility.Visible;
        }
        finally
        {
            BusyRing.IsActive = false;
            ScanButton.IsEnabled = !string.IsNullOrWhiteSpace(_rootPath);
            ChooseRootButton.IsEnabled = true;
        }
    }

    private static List<MediaFolderCandidate> FindCandidates(string root, CancellationToken token)
    {
        var results = new List<MediaFolderCandidate>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Pop();
            try
            {
                var videos = Directory.EnumerateFiles(current)
                    .Where(p => VideoExtensions.Contains(Path.GetExtension(p)))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var hasMovieNfo = Directory.EnumerateFiles(current, "*.nfo", SearchOption.TopDirectoryOnly)
                    .Any(p => !Path.GetFileNameWithoutExtension(p).Equals("tvshow", StringComparison.OrdinalIgnoreCase)
                           && !Path.GetFileNameWithoutExtension(p).Equals("season", StringComparison.OrdinalIgnoreCase));
                if (videos.Count > 0 && !hasMovieNfo)
                {
                    results.Add(new MediaFolderCandidate
                    {
                        FolderPath = current,
                        FolderName = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : current,
                        VideoSummary = videos.Count == 1 ? Path.GetFileName(videos[0]) : $"{videos.Count} видеофайла",
                        VideoCount = videos.Count
                    });
                }

                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(directory);
                        if ((attributes & FileAttributes.ReparsePoint) == 0 && (attributes & FileAttributes.System) == 0)
                            pending.Push(directory);
                    }
                    catch { /* Skip inaccessible or transient directories. */ }
                }
            }
            catch { /* Continue past folders without read permission. */ }
        }
        return results;
    }

}

public sealed class MediaFolderCandidate
{
    public string FolderPath { get; init; } = "";
    public string FolderName { get; init; } = "";
    public string VideoSummary { get; init; } = "";
    public int VideoCount { get; init; }
}
