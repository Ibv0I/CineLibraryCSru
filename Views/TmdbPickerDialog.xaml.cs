using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using CineLibraryCS.Services.Tmdb;
using System.Runtime.InteropServices;

namespace CineLibraryCS.Views;

/// <summary>
/// Resizable, centered native window for manually matching a movie to TMDb.
/// Unlike ContentDialog, Window allows an explicit client size before first paint.
/// </summary>
public sealed partial class TmdbPickerDialog : Window
{
    private readonly TmdbClient _client;
    private readonly bool _tvShows;
    private readonly TaskCompletionSource<ContentDialogResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _shown;
    private bool _ownerDisabled;
    private nint _ownerHandle;

    public TmdbMovie? Picked { get; private set; }
    public bool IsPrimaryButtonEnabled
    {
        get => SelectBtn?.IsEnabled ?? false;
        set { if (SelectBtn != null) SelectBtn.IsEnabled = value; }
    }

    public TmdbPickerDialog(TmdbClient client, string? initialTitle, int? year, bool tvShows = false)
    {
        _client = client;
        _tvShows = tvShows;
        InitializeComponent();
        RootGrid.RequestedTheme = ElementTheme.Dark;
        RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(255, 18, 18, 20));
        RootGrid.Loaded += (_, _) => CineLibraryCS.Services.LocalizationService.TranslateTree(RootGrid);
        Closed += OnWindowClosed;

        if (tvShows)
        {
            Title = CineLibraryCS.Services.LocalizationService.TranslateText("Выбор сериала в TMDb");
            HintText.Text = CineLibraryCS.Services.LocalizationService.TranslateText("Выберите правильный сериал, чтобы сопоставить его с записью TMDb.");
            TitleBox.PlaceholderText = CineLibraryCS.Services.LocalizationService.TranslateText("Название сериала…");
            SelectBtn.Content = CineLibraryCS.Services.LocalizationService.TranslateText("Выбрать сериал");
        }
        TitleBox.Text = initialTitle ?? "";
        if (year is int y && y > 0) YearBox.Text = y.ToString();

        // Configure the final dimensions and position before the first Activate().
        ConfigureForAvailableWidth(0);
        RootGrid.Loaded += async (_, _) => { if (!string.IsNullOrWhiteSpace(TitleBox.Text)) await DoSearchAsync(); };
        // Keep Esc as a keyboard shortcut without showing a floating key tip.
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, e) => { e.Handled = true; CloseWithResult(ContentDialogResult.None); };
        RootGrid.KeyboardAccelerators.Add(esc);
    }

    public void ConfigureForAvailableWidth(double availableWidth)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        SetApplicationIcon(appWindow);

        // Native title bar follows the app's dark theme, including the caption buttons.
        var titleBar = appWindow.TitleBar;
        var titleBarColor = Windows.UI.Color.FromArgb(255, 18, 18, 20);
        var titleTextColor = Windows.UI.Color.FromArgb(255, 235, 235, 238);
        var titleHoverColor = Windows.UI.Color.FromArgb(255, 55, 55, 60);
        var titlePressedColor = Windows.UI.Color.FromArgb(255, 70, 70, 76);
        titleBar.BackgroundColor = titleBarColor;
        titleBar.ForegroundColor = titleTextColor;
        titleBar.InactiveBackgroundColor = titleBarColor;
        titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 165, 165, 170);
        titleBar.ButtonBackgroundColor = titleBarColor;
        titleBar.ButtonForegroundColor = titleTextColor;
        titleBar.ButtonInactiveBackgroundColor = titleBarColor;
        titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 165, 165, 170);
        titleBar.ButtonHoverBackgroundColor = titleHoverColor;
        titleBar.ButtonHoverForegroundColor = titleTextColor;
        titleBar.ButtonPressedBackgroundColor = titlePressedColor;
        titleBar.ButtonPressedForegroundColor = titleTextColor;

        var owner = App.MainWindow;
        var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        var ownerId = Win32Interop.GetWindowIdFromWindow(ownerHwnd);
        var ownerWindow = AppWindow.GetFromWindowId(ownerId);
        _ownerHandle = ownerHwnd;

        // Slightly smaller than the previous build, but wide enough for readable overviews.
        int width = Math.Min(1500, Math.Max(900, ownerWindow.Size.Width - 100));
        int height = Math.Min(820, Math.Max(620, ownerWindow.Size.Height - 100));
        if (availableWidth > 0) width = Math.Min(width, Math.Max(900, (int)availableWidth - 48));
        appWindow.Resize(new SizeInt32(width, height));
        appWindow.Title = Title;
        appWindow.Move(new PointInt32(
            ownerWindow.Position.X + Math.Max(0, (ownerWindow.Size.Width - width) / 2),
            ownerWindow.Position.Y + Math.Max(0, (ownerWindow.Size.Height - height) / 2)));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }
    }

    private static void SetApplicationIcon(AppWindow appWindow)
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(iconPath)) appWindow.SetIcon(iconPath);
        }
        catch { }
    }

    public Task<ContentDialogResult> ShowAsync()
    {
        if (_shown) return _completion.Task;
        _shown = true;
        if (_ownerHandle != 0)
        {
            EnableWindow(_ownerHandle, false);
            _ownerDisabled = true;
        }
        Activate();
        return _completion.Task;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_ownerDisabled && _ownerHandle != 0)
        {
            EnableWindow(_ownerHandle, true);
            _ownerDisabled = false;
            try { App.MainWindow?.Activate(); } catch { }
        }
        if (!_completion.Task.IsCompleted) _completion.TrySetResult(ContentDialogResult.None);
    }

    private void CloseWithResult(ContentDialogResult result)
    {
        _completion.TrySetResult(result);
        Close();
    }

    private void OnChooseClick(object sender, RoutedEventArgs e)
    {
        if (Picked != null) CloseWithResult(ContentDialogResult.Primary);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseWithResult(ContentDialogResult.None);

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
            var items = results.Select(m => new TmdbResultItem(m, _client.GetImageUrl(m.PosterPath, "w154"))).ToList();
            using (var gate = new SemaphoreSlim(4))
            {
                await Task.WhenAll(items.Select(async item =>
                {
                    await gate.WaitAsync();
                    try { await item.LoadThumbnailAsync(_client); }
                    finally { gate.Release(); }
                }));
            }
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

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(nint hWnd, bool bEnable);
}
