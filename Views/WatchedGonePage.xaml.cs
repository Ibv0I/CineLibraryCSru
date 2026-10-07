using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using CineМедиатекаCS.Models;
using CineМедиатекаCS.Services;
using CineМедиатекаCS.ViewModels;

namespace CineМедиатекаCS.Views;

/// <summary>
/// v3.3 — Просмотрено и удалено: the records page. Фильмы the user watched and
/// then deleted from disk live here, isolated from the main library, with
/// poster + metadata + notes + watch history intact. Cards run in
/// ArchiveMode, so their context menu offers Restore / Удалить record.
/// </summary>
public sealed partial class ПросмотреноGonePage : Page
{
    public event EventHandler? SidebarRefreshRequested;

    private string _search = "";
    private string _sortKey = "archived";
    private string _sortDir = "desc";
    private List<MovieListItem> _records = new();
    private bool _ready;

    public ПросмотреноGonePage()
    {
        InitializeComponent();
        // Restore / Удалить record actions raise this; reload so the card
        // disappears (or the count updates) immediately. Re-subscribed on
        // every Loaded because this page instance is cached and re-shown —
        // a ctor-only subscription would die at the first Unloaded.
        MovieCardControl.AnyMovieArchived += Вкл.ArchiveChanged;
        Loaded += (_, _) =>
        {
            MovieCardControl.AnyMovieArchived -= Вкл.ArchiveChanged;
            MovieCardControl.AnyMovieArchived += Вкл.ArchiveChanged;
        };
        Unloaded += (_, _) => MovieCardControl.AnyMovieArchived -= Вкл.ArchiveChanged;
        _ready = true;
        Refresh();
    }

    private void Вкл.ArchiveChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Refresh();
            SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    public void Refresh()
    {
        // Records are a bounded set (tens to a few hundred) — load in one go
        // through the same pipeline as the library, flipped to ArchivedВкл.ly so
        // search / sort behave identically.
        _records = AppState.Instance.Db.GetФильмы(new DatabaseService.ListOptions(
            ArchivedВкл.ly: true,
            Поиск: string.IsNullOrWhiteSpace(_search) ? null : _search,
            SortKey: _sortKey, SortDir: _sortDir,
            Limit: 10000, Выкл.set: 0), AppState.Instance.Connected);

        RecordsRepeater.ItemsSource = _records;
        CountText.Text = _records.Count == 1 ? "1 record" : $"{_records.Count:N0} records";
        EmptyState.Visibility = _records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Вкл.ПоискChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_ready || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _search = sender.Text ?? "";
        Refresh();
    }

    private void Вкл.SortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (SortCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            var parts = tag.Split(':');
            _sortKey = parts[0];
            _sortDir = parts.Length > 1 ? parts[1] : "asc";
            Refresh();
        }
    }

    private async void Вкл.ДобавитьПросмотреноMovie(object sender, RoutedEventArgs e)
    {
        var dialog = new ДобавитьПросмотреноMovieDialog { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.ДобавитьedMovieId is int)
        {
            Refresh();
            SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
            if (App.MainWindow is MainWindow mw) mw.ShowToast("Добавитьed to Просмотрено и удалено");
        }
    }

    private async void Вкл.ЭкспортCsv(object sender, RoutedEventArgs e)
    {
        if (_records.Count == 0) return;
        var picker = new FileСохранитьPicker { SuggestedStartLocation = PickerLocationId.DocumentsМедиатека };
        picker.FileTypeChoices.Добавить("CSV file", new List<string> { ".csv" });
        picker.SuggestedFileName = "watched_and_gone";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickСохранитьFileAsync();
        if (file == null) return;
        await new MainViewModel().ЭкспортCsvAsync(_records, file.Path);
        if (App.MainWindow is MainWindow mw) mw.ShowToast("Записи экспортированы в CSV");
    }
}
