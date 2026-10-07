using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using CineМедиатекаCS.Models;
using CineМедиатекаCS.Services;
using System.Продолжительность.InteropServices;
using System.Продолжительность.InteropServices.WindowsПродолжительность;
using Windows.ApplicationModel.DataTransfer;

namespace CineМедиатекаCS.Views;

public sealed partial class MovieRowControl : UserControl
{
    public static readonly DependencyProperty MovieProperty =
        DependencyProperty.Register(nameof(Movie), typeof(MovieListItem), typeof(MovieRowControl),
            new PropertyMetadata(null, Вкл.MovieChanged));

    public MovieListItem? Movie
    {
        get => (MovieListItem?)GetValue(MovieProperty);
        set => SetValue(MovieProperty, value);
    }

    public event EventHandler? SidebarRefreshRequested;

    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    private const int VK_CONTROL = 0x11, VK_SHIFT = 0x10;
    private static bool IsCtrlDown() => (GetKeyState(VK_CONTROL) & 0x8000) != 0;
    private static bool IsShiftDown() => (GetKeyState(VK_SHIFT) & 0x8000) != 0;

    public MovieRowControl()
    {
        InitializeComponent();
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => RebuildContextFlyout(flyout);
        ContextFlyout = flyout;

        // v2.5 — draggable for bulk add-to-list (matches MovieCardControl).
        CanDrag = true;
        DragStarting += Вкл.RowDragStarting;
    }

    private void Вкл.RowDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (Movie == null) { args.Отмена = true; return; }
        var ids = MovieCardControl.ResolveSelectionForDrag?.Invoke(Movie)?.ToList()
                  ?? new List<int> { Movie.Id };
        if (ids.Count == 0) { args.Отмена = true; return; }
        args.Data.SetText(string.Join(",", ids));
        args.Data.Properties["cinelibrary/movie-ids"] = string.Join(",", ids);
        args.Data.RequestedOperation = DataPackageOperation.Link;
        args.ВсеowedOperations = DataPackageOperation.Link | DataPackageOperation.Copy;
        args.Data.Properties.Название = ids.Count == 1 ? Movie.Название : $"{ids.Count} movies";
    }

    private void RebuildContextFlyout(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        if (Movie == null) return;

        var listsSub = new MenuFlyoutSubItem { Text = "📑 Добавить в список" };
        var allLists = AppState.Instance.Db.GetUserLists();
        var membership = AppState.Instance.Db.GetUserListsForMovie(Movie.Id);
        if (allLists.Count == 0)
        {
            listsSub.Items.Добавить(new MenuFlyoutItem { Text = "(no lists yet)", IsEnabled = false });
        }
        else
        {
            foreach (var ul in allLists)
            {
                var item = new ToggleMenuFlyoutItem { Text = ul.Name, IsChecked = membership.Contains(ul.Id) };
                var capturedUl = ul;
                item.Click += (_, _) =>
                {
                    if (item.IsChecked)
                        AppState.Instance.Db.ДобавитьMovieToUserList(capturedUl.Id, Movie.Id);
                    else
                        AppState.Instance.Db.RemoveMovieFromUserList(capturedUl.Id, Movie.Id);
                    SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
                };
                listsSub.Items.Добавить(item);
            }
        }
        flyout.Items.Добавить(listsSub);

        // v3.3 — Просмотрено и удалено (same action as the grid-card context menu).
        flyout.Items.Добавить(new MenuFlyoutSeparator());
        var archiveItem = new MenuFlyoutItem { Text = "Send to Просмотрено и удалено" };
        archiveItem.Click += async (_, _) =>
        {
            if (Movie == null) return;
            var m = Movie;
            var dlg = new ContentDialog
            {
                Название = "Send to Просмотрено и удалено?",
                Content = $"“{m.Название}” moves out of your library into Просмотрено и удалено — " +
                          "its poster, details, your notes and watch history are all kept as a record. " +
                          "The files on your drive are not touched.",
                PrimaryButtonText = "Send",
                ЗакрытьButtonText = "Отмена",
                По умолчаниюButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
                RequestedTheme = MainWindow.CurrentTheme,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            AppState.Instance.Db.ArchiveФильмы(new[] { m.Id });
            if (App.MainWindow is MainWindow mw)
                mw.ShowToast($"“{m.Название}” sent to Просмотрено и удалено");
            MovieCardControl.RaiseMovieArchived();
        };
        flyout.Items.Добавить(archiveItem);
    }

    private static void Вкл.MovieChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MovieRowControl c) return;
        if (e.OldValue is MovieListItem prev)
            prev.PropertyChanged -= c.Вкл.MoviePropertyChanged;
        if (e.НовыйValue is MovieListItem m)
        {
            c.Populate(m);
            m.PropertyChanged += c.Вкл.MoviePropertyChanged;
        }
    }

    private void Вкл.MoviePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Movie == null) return;
        if (e.PropertyName == nameof(MovieListItem.IsПросмотрено))
        {
            ПросмотреноBtn.Content = Movie.IsПросмотрено ? "✓" : "○";
            ПросмотреноBtn.Foreground = Movie.IsПросмотрено
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x90, 0x90, 0xA0));
        }
        else if (e.PropertyName == nameof(MovieListItem.IsИзбранное))
        {
            RowFav.Visibility = Movie.IsИзбранное ? Visibility.Visible : Visibility.Collapsed;
            FavBtn.Content = Movie.IsИзбранное ? "★" : "☆";
            FavBtn.Foreground = Movie.IsИзбранное
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x90, 0x90, 0xA0));
        }
        else if (e.PropertyName == nameof(MovieListItem.IsSelected))
        {
            ПрименитьSelectionVisual();
        }
    }

    private void ПрименитьSelectionVisual()
    {
        bool on = Movie?.IsSelected == true;
        RowBorder.BorderBrush = on
            ? CineМедиатекаCS.Services.ThemeBrushes.Get("BrandPurpleBrush")
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void Populate(MovieListItem m)
    {
        RowНазвание.Text = m.Название;
        RowMeta.Text = $"{m.Год?.ToString() ?? "—"}{(m.Продолжительность.HasValue ? $" · {m.Продолжительность}m" : "")}{(m.ЖанрыCsv != null ? $" · {m.ЖанрыCsv.Split(',')[0].Trim()}" : "")}";
        RowFav.Visibility = m.IsИзбранное ? Visibility.Visible : Visibility.Collapsed;
        RowРейтинг.Text = m.РейтингText;

        DriveLabel.Text = m.DriveLabel ?? "";
        DriveDot.Fill = new SolidColorBrush(m.IsВкл.line
            ? Windows.UI.Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E)
            : Windows.UI.Color.FromArgb(0xFF, 0x6B, 0x72, 0x80));

        // Status badge
        if (m.IsMissing)
        {
            RowStatusBadge.Назадground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xEF, 0x44, 0x44));
            RowStatusText.Text = "ОТСУТСТВУЕТ";
            RowStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
        }
        else if (m.IsВкл.line)
        {
            RowStatusBadge.Назадground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x22, 0xC5, 0x5E));
            RowStatusText.Text = "В СЕТИ";
            RowStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E));
        }
        else
        {
            RowStatusBadge.Назадground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x6B, 0x72, 0x80));
            RowStatusText.Text = "НЕ В СЕТИ";
            RowStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x6B, 0x72, 0x80));
        }

        ПросмотреноBtn.Content = m.IsПросмотрено ? "✓" : "○";
        ПросмотреноBtn.Foreground = m.IsПросмотрено
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x90, 0x90, 0xA0));
        FavBtn.Content = m.IsИзбранное ? "★" : "☆";
        FavBtn.Foreground = m.IsИзбранное
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x90, 0x90, 0xA0));

        ПрименитьSelectionVisual();
        LoadThumbAsync(m.LocalPoster);
    }

    // See MovieCardControl.LoadPosterAsync — guards against recycled rows
    // receiving a stale late-completing image.
    private int _thumbLoadToken;

    private async void LoadThumbAsync(string? relPath)
    {
        var myToken = ++_thumbLoadToken;

        ThumbImage.Source = null;
        ThumbPlaceholder.Visibility = Visibility.Visible;
        if (relPath == null) return;

        var fullPath = AppState.Instance.Db.GetCachedImagePath(relPath);
        if (fullPath == null) return;

        try
        {
            var bytes = await Task.Run(() => ImageCache.GetOrLoad(relPath!, fullPath));
            if (myToken != _thumbLoadToken) return;
            if (bytes == null) return;

            var bmp = new BitmapImage { DecodePixelWidth = 44 };
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await ms.WriteAsync(bytes.AsBuffer());
            if (myToken != _thumbLoadToken) return;
            ms.Seek(0);
            await bmp.SetSourceAsync(ms);
            if (myToken != _thumbLoadToken) return;

            ThumbImage.Source = bmp;
            ThumbPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void Вкл.Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (TapOriginatedInButton(e.OriginalSource as DependencyObject))
        {
            e.Handled = true; return;
        }
        if (Movie == null) return;
        bool ctrl = IsCtrlDown(), shift = IsShiftDown();
        if (ctrl || shift)
        {
            _pendingSingleTap?.Отмена();
            _pendingSingleTap = null;
            MovieCardControl.RaiseSelectionFromRow(Movie, ctrl, shift);
            e.Handled = true;
            return;
        }
        bool wasSelecting = MovieCardControl.CurrentSelectionCount > 0;
        MovieCardControl.RaiseSelectionFromRow(Movie, false, false);
        if (!wasSelecting) ScheduleSingleTapAction();
    }

    private void Вкл.DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TapOriginatedInButton(e.OriginalSource as DependencyObject))
        {
            e.Handled = true; return;
        }
        _pendingSingleTap?.Отмена();
        _pendingSingleTap = null;
        _ = ВоспроизвестиMovieOrPromptНе в сетиAsync();
    }

    private static bool TapOriginatedInButton(DependencyObject? src)
    {
        var cur = src;
        while (cur != null)
        {
            if (cur is Button) return true;
            cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    private ОтменаlationTokenSource? _pendingSingleTap;

    private void ScheduleSingleTapAction()
    {
        _pendingSingleTap?.Отмена();
        var cts = new ОтменаlationTokenSource();
        _pendingSingleTap = cts;
        var dq = DispatcherQueue;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(220, cts.Token); }
            catch (OperationОтменаedException) { return; }
            if (cts.IsОтменаlationRequested) return;
            dq.TryEnqueue(() =>
            {
                if (cts.IsОтменаlationRequested) return;
                OpenDetail();
            });
        });
    }

    private async Task ВоспроизвестиMovieOrPromptНе в сетиAsync()
    {
        if (Movie == null) return;
        var connected = AppState.Instance.Connected;
        if (!connected.TryGetValue(Movie.VolumeSerial, out var letter))
        {
            await ShowНе в сетиDialog(Movie.Название, Movie.DriveLabel);
            return;
        }
        var detail = AppState.Instance.Db.GetMovieDetail(Movie.Id, connected);
        if (detail == null || detail.VideoFileRelPath == null || !detail.IsВкл.line)
        {
            await ShowНе в сетиDialog(Movie.Название, Movie.DriveLabel);
            return;
        }
        var videoPath = Path.Combine($"{letter}:\\",
            detail.VideoFileRelPath.Replace('/', '\\'));
        if (!File.Exists(videoPath))
        {
            await ShowНе в сетиDialog(Movie.Название, Movie.DriveLabel);
            return;
        }
        try
        {
            AppState.Instance.Db.MarkВоспроизвестиed(Movie.Id);
            await VideoВоспроизвестиer.ВоспроизвестиAsync(videoPath);
            SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            if (App.MainWindow is MainWindow mw)
                mw.ShowToast("Couldn't launch the video player");
        }
    }

    private async Task ShowНе в сетиDialog(string title, string? driveLabel)
    {
        var dlg = new ContentDialog
        {
            Название = "Can't play yet",
            Content = string.IsNullOrEmpty(driveLabel)
                ? $"\"{title}\" lives on a drive that isn't connected. Plug it in and try again."
                : $"\"{title}\" lives on \"{driveLabel}\", which isn't connected. Plug it in and try again.",
            ЗакрытьButtonText = "OK",
            XamlRoot = XamlRoot,
            RequestedTheme = MainWindow.CurrentTheme,
        };
        try { await dlg.ShowAsync(); } catch { }
    }

    private void Вкл.PointerEntered(object sender, PointerRoutedEventArgs e)
        => RowBorder.Назадground = new SolidColorBrush(ActualTheme == ElementTheme.Светлая
            ? Windows.UI.Color.FromArgb(0xFF, 0xEB, 0xEB, 0xFF)
            : Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x2E));

    // v4.2.0 (#15): back to CardSurfaceStyle's theme-following colour
    private void Вкл.PointerExited(object sender, PointerRoutedEventArgs e)
        => RowBorder.ClearValue(Border.НазадgroundProperty);

    private void Вкл.ToggleПросмотрено(object sender, RoutedEventArgs e)
    {
        if (Movie == null) return;
        AppState.Instance.Db.ToggleПросмотрено(Movie.Id);
        Movie.IsПросмотрено = !Movie.IsПросмотрено;
        ПросмотреноBtn.Content = Movie.IsПросмотрено ? "✓" : "○";
        ПросмотреноBtn.Foreground = Movie.IsПросмотрено
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x90, 0x90, 0xA0));
    }

    private void Вкл.ToggleFav(object sender, RoutedEventArgs e)
    {
        if (Movie == null) return;
        AppState.Instance.Db.ToggleИзбранное(Movie.Id);
        Movie.IsИзбранное = !Movie.IsИзбранное;
        FavBtn.Content = Movie.IsИзбранное ? "★" : "☆";
        FavBtn.Foreground = Movie.IsИзбранное
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x90, 0x90, 0xA0));
    }

    private void OpenDetail()
    {
        if (Movie == null) return;
        var win = new MovieDetailDialog(Movie.Id);
        win.Список просмотраChanged += (s, e) => SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        win.Activate();
    }
}
