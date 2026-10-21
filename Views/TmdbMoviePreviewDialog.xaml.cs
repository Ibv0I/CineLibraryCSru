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
    private List<TmdbImageChoice> _posterChoices = new();
    private List<TmdbImageChoice> _backdropChoices = new();
    private nint _ownerHandle;
    private readonly Window _ownerWindow;
    private bool _ownerDisabled;
    private bool _shown;
    private readonly string? _initialPosterLocalPath;
    private readonly string? _initialBackdropLocalPath;
    public bool PosterWasChanged { get; private set; }
    public bool BackdropWasChanged { get; private set; }
    public bool DownloadActorPhotos => DownloadActorPhotosCheckBox.IsChecked == true;

    public TmdbMovie? Movie { get; private set; }
    public string? SelectedPosterPath { get; private set; }
    public string? SelectedBackdropPath { get; private set; }

    public TmdbMoviePreviewDialog(TmdbClient client, TmdbMovie searchHit, string? initialPosterLocalPath = null, string? initialBackdropLocalPath = null, Window? ownerWindow = null)
    {
        _client = client;
        _ownerWindow = ownerWindow ?? App.MainWindow ?? throw new InvalidOperationException("Главное окно приложения ещё не создано.");
        _searchHit = searchHit;
        _initialPosterLocalPath = initialPosterLocalPath;
        _initialBackdropLocalPath = initialBackdropLocalPath;
        InitializeComponent();
        RootGrid.Loaded += async (_, _) => await LoadMovieAsync();
        Closed += OnClosed;
        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var window = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        SetApplicationIcon(window);
        var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_ownerWindow);
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
        window.Resize(new SizeInt32(Math.Min(1450, Math.Max(1050, owner.Size.Width - 80)), Math.Min(1050, Math.Max(800, owner.Size.Height - 60))));
        window.Move(new PointInt32(owner.Position.X + Math.Max(0, (owner.Size.Width - window.Size.Width) / 2), owner.Position.Y + Math.Max(0, (owner.Size.Height - window.Size.Height) / 2)));
        if (window.Presenter is OverlappedPresenter presenter) { presenter.IsResizable = true; presenter.IsMaximizable = true; }
    }

    private static void SetApplicationIcon(AppWindow appWindow)
    {
        try { var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"); if (File.Exists(iconPath)) appWindow.SetIcon(iconPath); } catch { }
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
            MovieTitleEditor.Text = details.Title;

            var images = await _client.GetMovieImagesAsync(details.TmdbId);
            if (images.Posters.Count == 0 && !string.IsNullOrWhiteSpace(details.PosterPath)) images.Posters.Add(details.PosterPath!);
            if (images.Backdrops.Count == 0 && !string.IsNullOrWhiteSpace(details.BackdropPath)) images.Backdrops.Add(details.BackdropPath!);
            _posterChoices = images.Posters.Select(p => new TmdbImageChoice(p, _client.GetImageUrl(p, "w185"), true)).ToList();
            _backdropChoices = images.Backdrops.Select(p => new TmdbImageChoice(p, _client.GetImageUrl(p, "w300"), false)).ToList();
            SelectedPosterPath = images.Posters.FirstOrDefault() ?? details.PosterPath;
            SelectedBackdropPath = images.Backdrops.FirstOrDefault() ?? details.BackdropPath;
            RefreshPosterPreview(); RefreshBackdropPreview();
            if (SelectedPosterPath == null || SelectedBackdropPath == null)
                ImageStatusText.Text = "Некоторые изображения отсутствуют в TMDb. Метаданные можно редактировать независимо от изображений.";
            UseSelectionButton.IsEnabled = true;
        }
        catch (Exception ex) { ImageStatusText.Text = "Не удалось загрузить подробности или изображения TMDb: " + ex.Message; }
        finally { Busy.IsActive = false; }
    }

    private void OnPosterPreviewTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => OnPosterPreviewClick(sender, new RoutedEventArgs());
    private void OnBackdropPreviewTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => OnBackdropPreviewClick(sender, new RoutedEventArgs());

    private async void OnPosterPreviewClick(object sender, RoutedEventArgs e)
    {
        if (_posterChoices.Count == 0) { ImageStatusText.Text = "TMDb не предоставил варианты постеров для этого фильма."; return; }
        var picker = new TmdbImagePickerWindow(_posterChoices, true, this);
        var path = await picker.ShowAsync();
        if (!string.IsNullOrWhiteSpace(path)) { SelectedPosterPath = path; PosterWasChanged = true; RefreshPosterPreview(); }
    }

    private async void OnBackdropPreviewClick(object sender, RoutedEventArgs e)
    {
        if (_backdropChoices.Count == 0) { ImageStatusText.Text = "TMDb не предоставил варианты фоновых изображений для этого фильма."; return; }
        var picker = new TmdbImagePickerWindow(_backdropChoices, false, this);
        var path = await picker.ShowAsync();
        if (!string.IsNullOrWhiteSpace(path)) { SelectedBackdropPath = path; BackdropWasChanged = true; RefreshBackdropPreview(); }
    }

    private void RefreshPosterPreview()
    {
        try
        {
            if (!PosterWasChanged && !string.IsNullOrWhiteSpace(_initialPosterLocalPath) && File.Exists(_initialPosterLocalPath))
                PosterPreview.Source = new BitmapImage(new Uri(_initialPosterLocalPath));
            else PosterPreview.Source = !string.IsNullOrWhiteSpace(SelectedPosterPath) ? new BitmapImage(new Uri(_client.GetImageUrl(SelectedPosterPath, "w500"))) : null;
        }
        catch { PosterPreview.Source = null; }
    }
    private void RefreshBackdropPreview()
    {
        try
        {
            if (!BackdropWasChanged && !string.IsNullOrWhiteSpace(_initialBackdropLocalPath) && File.Exists(_initialBackdropLocalPath))
                BackdropPreview.Source = new BitmapImage(new Uri(_initialBackdropLocalPath));
            else BackdropPreview.Source = !string.IsNullOrWhiteSpace(SelectedBackdropPath) ? new BitmapImage(new Uri(_client.GetImageUrl(SelectedBackdropPath, "w1280"))) : null;
        }
        catch { BackdropPreview.Source = null; }
    }

    private void ApplyEditedMetadata()
    {
        if (Movie == null) return;
        Movie.Title = MovieTitleEditor.Text.Trim();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        ApplyEditedMetadata();
        if (Movie != null && !string.IsNullOrWhiteSpace(Movie.Title)) CloseWithResult(ContentDialogResult.Primary);
        else ImageStatusText.Text = "Укажите название фильма перед подтверждением.";
    }
    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseWithResult(ContentDialogResult.None);
    private void CloseWithResult(ContentDialogResult result) { _completion.TrySetResult(result); Close(); }
    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_ownerDisabled && _ownerHandle != 0) { EnableWindow(_ownerHandle, true); _ownerDisabled = false; try { _ownerWindow.Activate(); } catch { } }
        if (!_completion.Task.IsCompleted) _completion.TrySetResult(ContentDialogResult.None);
    }
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint hWnd, bool bEnable);
}

public sealed class TmdbImageChoice
{
    public string FilePath { get; }
    public BitmapImage Thumbnail { get; }
    public double DisplayWidth { get; }
    public double DisplayHeight { get; }
    public TmdbImageChoice(string filePath, string url, bool isPoster = true)
    {
        FilePath = filePath;
        Thumbnail = new BitmapImage(new Uri(url));
        DisplayWidth = isPoster ? 150 : 240;
        DisplayHeight = isPoster ? 225 : 135;
    }
}
