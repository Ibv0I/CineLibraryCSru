using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using CineLibraryCS.Services.Tmdb;
using System.Runtime.InteropServices;

namespace CineLibraryCS.Views;

public sealed partial class TmdbMoviePreviewDialog : Window
{
    private readonly TmdbClient _client;
    private readonly TmdbMovie _searchHit;
    private readonly TaskCompletionSource<ContentDialogResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _ownerHandle;
    private bool _ownerDisabled;
    private bool _shown;
    private bool _loading = true;

    public TmdbMovie? Movie { get; private set; }
    public string? SelectedPosterPath { get; private set; }
    public string? SelectedBackdropPath { get; private set; }

    public TmdbMoviePreviewDialog(TmdbClient client, TmdbMovie searchHit)
    {
        _client = client;
        _searchHit = searchHit;
        InitializeComponent();
        RootGrid.Loaded += async (_, _) => await LoadMovieAsync();
        Closed += OnClosed;
        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        var window = AppWindow.GetFromWindowId(id);
        var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var owner = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(ownerHwnd));
        _ownerHandle = ownerHwnd;
        var titleBar = window.TitleBar;
        var bg = Windows.UI.Color.FromArgb(255, 18, 18, 20);
        var fg = Windows.UI.Color.FromArgb(255, 235, 235, 238);
        titleBar.BackgroundColor = bg; titleBar.InactiveBackgroundColor = bg;
        titleBar.ButtonBackgroundColor = bg; titleBar.ButtonInactiveBackgroundColor = bg;
        titleBar.ForegroundColor = fg; titleBar.ButtonForegroundColor = fg;
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 55, 55, 60);
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 70, 70, 76);
        window.Resize(new SizeInt32(Math.Min(1450, Math.Max(1000, owner.Size.Width - 80)), Math.Min(920, Math.Max(720, owner.Size.Height - 80))));
        window.Move(new PointInt32(owner.Position.X + Math.Max(0, (owner.Size.Width - window.Size.Width) / 2), owner.Position.Y + Math.Max(0, (owner.Size.Height - window.Size.Height) / 2)));
        if (window.Presenter is OverlappedPresenter presenter) { presenter.IsResizable = true; presenter.IsMaximizable = true; }
    }

    public Task<ContentDialogResult> ShowAsync()
    {
        if (_shown) return _completion.Task;
        _shown = true;
        if (_ownerHandle != 0) { EnableWindow(_ownerHandle, false); _ownerDisabled = true; }
        Activate();
        return _completion.Task;
    }

    private async Task LoadMovieAsync()
    {
        Busy.IsActive = true;
        try
        {
            var details = await _client.GetMovieDetailsAsync(_searchHit.TmdbId);
            if (details == null) throw new InvalidOperationException("TMDb не вернул подробности фильма.");
            Movie = details;
            MovieTitleText.Text = $"{details.Title}{(details.Year > 0 ? $" ({details.Year})" : string.Empty)}";
            var genres = string.Join(", ", details.Genres.Select(g => g.Name));
            var countries = string.Join(", ", details.ProductionCountries.Select(c => c.Name));
            MovieFactsText.Text = $"TMDb ID: {details.TmdbId} · Рейтинг: {details.Rating:0.0}/10 ({details.VoteCount} голосов) · Длительность: {(details.Runtime > 0 ? $"{details.Runtime} мин." : "не указана")}
Дата выхода: {(string.IsNullOrWhiteSpace(details.ReleaseDate) ? "не указана" : details.ReleaseDate)} · Жанры: {(string.IsNullOrWhiteSpace(genres) ? "не указаны" : genres)} · Страны: {(string.IsNullOrWhiteSpace(countries) ? "не указаны" : countries)}";
            MovieOverviewText.Text = string.IsNullOrWhiteSpace(details.Overview) ? "Описание отсутствует в TMDb." : details.Overview;
            var directors = string.Join(", ", details.Directors);
            var cast = string.Join(", ", details.Cast.Take(8).Select(c => c.Name));
            MovieCreditsText.Text = $"Режиссёр: {(string.IsNullOrWhiteSpace(directors) ? "не указан" : directors)}
В ролях: {(string.IsNullOrWhiteSpace(cast) ? "не указаны" : cast)}";

            var images = await _client.GetMovieImagesAsync(details.TmdbId);
            if (images.Posters.Count == 0 && !string.IsNullOrWhiteSpace(details.PosterPath)) images.Posters.Add(details.PosterPath!);
            if (images.Backdrops.Count == 0 && !string.IsNullOrWhiteSpace(details.BackdropPath)) images.Backdrops.Add(details.BackdropPath!);
            PosterGrid.ItemsSource = images.Posters.Select(p => new TmdbImageChoice(p, _client.GetImageUrl(p, "w185"))).ToList();
            BackdropGrid.ItemsSource = images.Backdrops.Select(p => new TmdbImageChoice(p, _client.GetImageUrl(p, "w300"))).ToList();
            if (PosterGrid.Items.Count > 0) PosterGrid.SelectedIndex = 0;
            if (BackdropGrid.Items.Count > 0) BackdropGrid.SelectedIndex = 0;
            if (PosterGrid.Items.Count == 0) ImageStatusText.Text = "TMDb не предоставил варианты постеров. Выбор постера можно будет повторить позже.";
            if (BackdropGrid.Items.Count == 0) ImageStatusText.Text = "TMDb не предоставил варианты фоновых изображений. Выбор фона можно будет повторить позже.";
            UseSelectionButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ImageStatusText.Text = "Не удалось загрузить подробности или изображения TMDb: " + ex.Message;
        }
        finally { _loading = false; Busy.IsActive = false; }
    }

    private void OnPosterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PosterGrid.SelectedItem is TmdbImageChoice choice)
        { SelectedPosterPath = choice.FilePath; PosterPreview.Source = new BitmapImage(new Uri(_client.GetImageUrl(choice.FilePath, "w500"))); }
    }

    private void OnBackdropSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BackdropGrid.SelectedItem is TmdbImageChoice choice)
        { SelectedBackdropPath = choice.FilePath; BackdropPreview.Source = new BitmapImage(new Uri(_client.GetImageUrl(choice.FilePath, "w780"))); }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (Movie != null) CloseWithResult(ContentDialogResult.Primary);
    }
    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseWithResult(ContentDialogResult.None);
    private void CloseWithResult(ContentDialogResult result) { _completion.TrySetResult(result); Close(); }
    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_ownerDisabled && _ownerHandle != 0) { EnableWindow(_ownerHandle, true); _ownerDisabled = false; try { App.MainWindow.Activate(); } catch { } }
        if (!_completion.Task.IsCompleted) _completion.TrySetResult(ContentDialogResult.None);
    }
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint hWnd, bool bEnable);
}

public sealed class TmdbImageChoice
{
    public string FilePath { get; }
    public BitmapImage Thumbnail { get; }
    public TmdbImageChoice(string filePath, string url) { FilePath = filePath; Thumbnail = new BitmapImage(new Uri(url)); }
}
