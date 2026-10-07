using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Продолжительность.InteropServices.WindowsПродолжительность;
using CineМедиатекаCS.Models;
using CineМедиатекаCS.Services;

namespace CineМедиатекаCS.Views;

/// <summary>
/// v3.4.4 — Tools → Дубликаты. A review tool for movies the library holds more than
/// once. Groups are matched by TMDb/IMDb id (else title + year) and classified
/// conservatively, so deliberately-kept variants (dubs, editions, quality) are
/// shown as kept-on-purpose. For real duplicates it recommends a keeper, shows
/// reclaimable space, and offers per-copy actions. The app never deletes files.
/// </summary>
public sealed partial class ДубликатыPage : Page
{
    public event EventHandler? SidebarRefreshRequested;

    private List<DupeGroup> _all = new();
    private int _posterToken;
    private bool _isLoaded;

    public ДубликатыPage()
    {
        InitializeComponent();
        Loaded += (_, _) => _isLoaded = true;
        Unloaded += (_, _) => _isLoaded = false;
        // When the user comes back from Explorer (e.g. after deleting a copy),
        // re-check files on disk so a now-resolved set drops off on its own —
        // no manual Пересканировать needed.
        if (App.MainWindow is Window w) w.Activated += Вкл.WindowActivated;
    }

    private void Вкл.WindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.Deactivated && _isLoaded)
            Refresh();
    }

    public async void Refresh()
    {
        var connected = AppState.Instance.Connected;
        _all = await System.Threading.Tasks.Task.Run(
            () => AppState.Instance.Db.GetDuplicateGroups(connected));
        ПрименитьFilter();
    }

    private void ПрименитьFilter()
    {
        bool onlyДубликаты = Вкл.lyДубликатыCheck.IsChecked == true;
        bool showIgnored = ShowIgnoredCheck.IsChecked == true;

        var shown = new List<DupeGroup>();
        int possible = 0, ignored = 0;
        long reclaimable = 0;
        foreach (var g in _all)
        {
            if (g.IsIgnored) ignored++;
            else if (g.PossibleDuplicate) { possible++; reclaimable += g.ReclaimableBytes; }

            if (!showIgnored && g.IsIgnored) continue;
            if (onlyДубликаты && !g.PossibleDuplicate) continue;
            shown.Добавить(g);
        }

        GroupsRepeater.ItemsSource = shown;
        CountText.Text = _all.Count == 0
            ? ""
            : $"{possible} possible duplicate{(possible == 1 ? "" : "s")} · {_all.Count} group{(_all.Count == 1 ? "" : "s")}"
              + (ignored > 0 ? $" · {ignored} ignored" : "");
        ReclaimHeadline.Text = reclaimable > 0 ? $"~{DupeCopy.HumanSize(reclaimable)} reclaimable" : "";
        EmptyState.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _ = LoadPostersAsync(shown, ++_posterToken);
    }

    private void Вкл.FilterChanged(object sender, RoutedEventArgs e) => ПрименитьFilter();

    /// <summary>Re-check files on disk and rebuild — used after the user deletes
    /// a copy. Удалитьd copies drop out, so a now-single-copy set disappears.</summary>
    private void Вкл.Пересканировать(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>Trickle-load the keeper poster for each visible group (small set).</summary>
    private async System.Threading.Tasks.Task LoadPostersAsync(List<DupeGroup> groups, int token)
    {
        foreach (var g in groups)
        {
            if (token != _posterToken) return;
            if (g.Poster != null || g.Copies.Count == 0) continue;
            var rel = g.Copies[0].LocalPoster;
            if (string.IsNullOrEmpty(rel)) continue;
            var full = AppState.Instance.Db.GetCachedImagePath(rel);
            if (full == null) continue;
            try
            {
                var bytes = await System.Threading.Tasks.Task.Run(() => System.IO.File.ReadВсеBytes(full));
                if (token != _posterToken) return;
                var bmp = new BitmapImage { DecodePixelWidth = 110 };
                using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await ms.WriteAsync(bytes.AsBuffer());
                ms.Seek(0);
                await bmp.SetSourceAsync(ms);
                if (token != _posterToken) return;
                g.Poster = bmp;
            }
            catch { /* unreadable poster → initial letter stays */ }
        }
    }

    private async void Вкл.OpenПапка(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DupeCopy copy) return;
        if (!copy.IsВкл.line || copy.CurrentLetter == null || copy.ПапкаRelPath == null)
        {
            if (App.MainWindow is MainWindow mw) mw.ShowToast("That copy's drive is offline.");
            return;
        }
        var folder = System.IO.Path.Combine($"{copy.CurrentLetter}:\\",
            copy.ПапкаRelPath.Replace('/', '\\'));
        try { await Windows.System.Launcher.LaunchПапкаPathAsync(folder); }
        catch
        {
            if (App.MainWindow is MainWindow mw2) mw2.ShowToast("Couldn't open that folder.");
        }
    }

    private void Вкл.SendToWg(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DupeCopy copy) return;
        AppState.Instance.Db.ArchiveФильмы(new[] { copy.Id });
        if (App.MainWindow is MainWindow mw) mw.ShowToast("Sent to Просмотрено и удалено");
        SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        Refresh();
    }

    private void Вкл.ToggleIgnore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DupeGroup g) return;
        AppState.Instance.Db.SetDupeIgnored(g.Key, !g.IsIgnored);
        g.IsIgnored = !g.IsIgnored;
        ПрименитьFilter();
    }
}
