using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Globalization;
using System.Xml;
using System.Collections.ObjectModel;

namespace CineLibraryCS.Views;

/// <summary>Visual editor for Kodi/tinyMediaManager movie.nfo. Existing unknown XML nodes are retained.</summary>
public sealed partial class NfoEditorDialog : Window
{
    private readonly string _path;
    private readonly Window _ownerWindow;
    private readonly TaskCompletionSource<ContentDialogResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AppWindow? _appWindow;
    private DispatcherQueueTimer? _sizeSaveTimer;
    private SizeInt32 _pendingSize;
    private nint _ownerHandle;
    private bool _ownerDisabled;
    private bool _shown;
    private bool _savingSize;
    private string SettingsPath => Path.Combine(CineLibraryCS.Services.AppState.Instance.DataDir, "NfoEditorWindow.settings.json");
    private XmlDocument _doc = new() { PreserveWhitespace = true };
    private XmlElement _root = null!;
    private readonly ObservableCollection<RatingRow> _ratings = new();
    private readonly ObservableCollection<IdentifierRow> _identifiers = new();
    private string _originalAudioTracksText = "";
    private string _originalSubtitlesText = "";
    private readonly ObservableCollection<TrackRow> _audioTracks = new();
    private readonly ObservableCollection<TrackRow> _subtitles = new();
    public sealed class TrackRow
    {
        public string[] Values { get; set; } = Array.Empty<string>();
        public string Display => string.Join("   |   ", Values);
    }
    private static readonly string[] AspectRatios = { "1:1", "4:3", "16:9", "16:10", "1.85:1", "2.00:1", "2.20:1", "2.35:1", "2.39:1", "2.40:1", "2.76:1", "Неизвестно" };

    public sealed class RatingRow
    {
        public string Source { get; set; } = "";
        public string Value { get; set; } = "";
        public string Votes { get; set; } = "0";
    }
    public sealed class IdentifierRow
    {
        public string Source { get; set; } = "";
        public string Identifier { get; set; } = "";
    }
    private static readonly string[] RatingSources = { "tmdb", "imdb", "kinopoisk" };
    private static readonly string[] StandardGenres = { "Боевик", "Приключения", "Анимация", "Комедия", "Криминал", "Документальный", "Драма", "Семейный", "Фэнтези", "История", "Ужасы", "Музыка", "Детектив", "Мелодрама", "Фантастика", "Телевизионный фильм", "Триллер", "Военный", "Вестерн" };
    private static readonly string[] RussianCertifications = { "Без ограничений", "0+", "6+", "12+", "16+", "18+", "Неизвестно" };
    public bool Saved { get; private set; }

    public NfoEditorDialog(string nfoPath, Window ownerWindow)
    {
        _path = nfoPath;
        _ownerWindow = ownerWindow;
        InitializeComponent();
        RootGrid.Loaded += (_, _) => CineLibraryCS.Services.LocalizationService.TranslateTree(RootGrid);
        Closed += OnWindowClosed;
        ConfigureWindow();
        CertificationBox.ItemsSource = RussianCertifications;
        AspectBox.ItemsSource = AspectRatios;
        AudioTracksList.ItemsSource = _audioTracks;
        SubtitlesList.ItemsSource = _subtitles;
        RatingsList.ItemsSource = _ratings;
        IdentifiersList.ItemsSource = _identifiers;
        LoadNfo();
    }

    private void ConfigureWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow = appWindow;
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(iconPath)) appWindow.SetIcon(iconPath);
        }
        catch { }

        var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_ownerWindow);
        var owner = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(ownerHwnd));
        _ownerHandle = ownerHwnd;
        var titleBar = appWindow.TitleBar;
        var bg = Windows.UI.Color.FromArgb(255, 18, 18, 20);
        var fg = Windows.UI.Color.FromArgb(255, 235, 235, 238);
        titleBar.BackgroundColor = bg;
        titleBar.InactiveBackgroundColor = bg;
        titleBar.ButtonBackgroundColor = bg;
        titleBar.ButtonInactiveBackgroundColor = bg;
        titleBar.ForegroundColor = fg;
        titleBar.ButtonForegroundColor = fg;
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 55, 55, 60);
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 70, 70, 76);

        var defaultSize = new SizeInt32(Math.Min(1280, Math.Max(980, owner.Size.Width - 100)), Math.Min(900, Math.Max(680, owner.Size.Height - 100)));
        var savedSize = LoadSavedSize();
        appWindow.Resize(savedSize ?? defaultSize);
        _pendingSize = savedSize ?? defaultSize;
        _sizeSaveTimer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (_sizeSaveTimer != null)
        {
            _sizeSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _sizeSaveTimer.IsRepeating = false;
            _sizeSaveTimer.Tick += (_, _) => SaveWindowSize(_pendingSize);
        }
        appWindow.Changed += OnAppWindowChanged;
        appWindow.Move(new PointInt32(owner.Position.X + Math.Max(0, (owner.Size.Width - appWindow.Size.Width) / 2), owner.Position.Y + Math.Max(0, (owner.Size.Height - appWindow.Size.Height) / 2)));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }
    }

    private SizeInt32? LoadSavedSize()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var size = JsonSerializer.Deserialize<SavedWindowSize>(File.ReadAllText(SettingsPath));
            if (size != null && size.Width >= 760 && size.Height >= 600)
                return new SizeInt32(size.Width, size.Height);
        }
        catch { }
        return null;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _savingSize) return;
        _pendingSize = sender.Size;
        if (_sizeSaveTimer != null)
        {
            _sizeSaveTimer.Stop();
            _sizeSaveTimer.Start();
        }
        else SaveWindowSize(_pendingSize);
    }

    private void SaveWindowSize(SizeInt32 size)
    {
        if (_savingSize || size.Width < 760 || size.Height < 600) return;
        _savingSize = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new SavedWindowSize { Width = size.Width, Height = size.Height }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* The app can still be used if settings storage is unavailable. */ }
        finally { _savingSize = false; }
    }

    private sealed class SavedWindowSize
    {
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public Task<ContentDialogResult> ShowAsync()
    {
        if (_shown) return _completion.Task;
        _shown = true;
        if (_ownerHandle != 0) { EnableWindow(_ownerHandle, false); _ownerDisabled = true; }
        Activate();
        return _completion.Task;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _sizeSaveTimer?.Stop();
        SaveWindowSize(_pendingSize.Width > 0 ? _pendingSize : _appWindow?.Size ?? new SizeInt32(0, 0));
        if (_ownerDisabled && _ownerHandle != 0) { EnableWindow(_ownerHandle, true); _ownerDisabled = false; }
        if (!_completion.Task.IsCompleted) _completion.TrySetResult(ContentDialogResult.None);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _completion.TrySetResult(ContentDialogResult.None);
        Close();
    }

    [DllImport("user32.dll")] private static extern bool EnableWindow(nint hWnd, bool bEnable);

    private void LoadNfo()
    {
        _doc.Load(_path);
        _root = _doc.DocumentElement ?? throw new InvalidDataException("NFO не содержит корневого XML-элемента.");
        TitleBox.Text = Get("title"); OriginalTitleBox.Text = Get("originaltitle");
        TaglineBox.Text = Get("tagline"); PlotBox.Text = Get("plot");
        YearBox.Text = Get("year");
        var premiered = Get("premiered");
        if (DateTime.TryParseExact(premiered, new[] { "yyyy-MM-dd", "dd-MM-yyyy", "yyyy-M-d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate)) PremieredBox.Text = releaseDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        else PremieredBox.Text = premiered ?? "";
        RuntimeBox.Text = Get("runtime");
        var certification = Get("mpaa") ?? Get("certification");
        CertificationBox.SelectedItem = RussianCertifications.FirstOrDefault(x => string.Equals(x, certification, StringComparison.OrdinalIgnoreCase));
        if (CertificationBox.SelectedItem == null && !string.IsNullOrWhiteSpace(certification)) CertificationBox.SelectedItem = "Неизвестно";
        StudiosBox.Text = string.Join(Environment.NewLine, Elements("studio").Select(x => x.InnerText));
        CountriesBox.Text = string.Join(Environment.NewLine, Elements("country").Select(x => x.InnerText));
        GenresBox.Text = string.Join(", ", Elements("genre").Select(x => x.InnerText));
        _ratings.Clear(); foreach (var x in Elements("ratings/rating")) _ratings.Add(new RatingRow { Source = x.GetAttribute("name"), Value = Child(x, "value") ?? x.InnerText, Votes = Child(x, "votes") ?? "0" });
        _identifiers.Clear(); foreach (var x in Elements("uniqueid")) _identifiers.Add(new IdentifierRow { Source = x.GetAttribute("type"), Identifier = x.InnerText });
        OnYearChanged(YearBox, null!); OnDateChanged(PremieredBox, null!);

        var video = FindStream("video");
        VideoCodecBox.Text = Child(video, "codec") ?? "";
        VideoBitrateBox.Text = Child(video, "bitrate") ?? "";
        WidthBox.Text = Child(video, "width") ?? ""; HeightBox.Text = Child(video, "height") ?? "";
        FrameRateBox.Text = Child(video, "framerate") ?? ""; HdrBox.Text = Child(video, "hdrtype") ?? "";
        var aspect = Child(video, "aspect") ?? "";
        AspectBox.SelectedItem = AspectRatios.FirstOrDefault(x => string.Equals(x, aspect, StringComparison.OrdinalIgnoreCase));
        if (AspectBox.SelectedItem == null && !string.IsNullOrWhiteSpace(aspect)) AspectBox.SelectedItem = "Неизвестно";
        _audioTracks.Clear(); foreach (var x in StreamElements("audio")) _audioTracks.Add(new TrackRow { Values = new[] { Child(x,"codec"), Child(x,"language"), Child(x,"channels"), Child(x,"bitrate"), Child(x,"samplerate") ?? Child(x,"samplingrate"), Child(x,"title") }.Select(v => v ?? "").ToArray() });
        _subtitles.Clear(); foreach (var x in StreamElements("subtitle")) _subtitles.Add(new TrackRow { Values = new[] { Child(x,"language"), Child(x,"codec"), Child(x,"title"), Child(x,"default"), Child(x,"forced") }.Select(v => v ?? "").ToArray() });
        AudioTracksList.DisplayMemberPath = nameof(TrackRow.Display);
        SubtitlesList.DisplayMemberPath = nameof(TrackRow.Display);
        AudioTracksList.ItemTemplate = null;
        SubtitlesList.ItemTemplate = null;
        _originalAudioTracksText = string.Join(Environment.NewLine, _audioTracks.Select(x => string.Join(" | ", x.Values)));
        _originalSubtitlesText = string.Join(Environment.NewLine, _subtitles.Select(x => string.Join(" | ", x.Values)));
        // Preserve the original representation so unrelated edits do not rebuild track nodes.
    }

    private string? Get(string name) => _root.SelectSingleNode(name)?.InnerText;
    private static string? Child(XmlNode? node, string name) => node?.SelectSingleNode(name)?.InnerText;
    private List<XmlElement> Elements(string xpath) => _root.SelectNodes(xpath)?.OfType<XmlElement>().ToList() ?? new();
    private XmlElement? FindStream(string name) => _root.SelectSingleNode($"fileinfo/streamdetails/{name}") as XmlElement
        ?? _root.SelectSingleNode($"streamdetails/{name}") as XmlElement;
    private List<XmlElement> StreamElements(string name)
    {
        var parent = _root.SelectSingleNode("fileinfo/streamdetails") ?? _root.SelectSingleNode("streamdetails");
        return parent?.SelectNodes(name)?.OfType<XmlElement>().ToList() ?? new();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs args)
    {
        try
        {
            Set("title", TitleBox.Text); Set("originaltitle", OriginalTitleBox.Text); Set("tagline", TaglineBox.Text);
            if (!string.IsNullOrWhiteSpace(YearBox.Text) && !IsValidYear(YearBox.Text)) throw new InvalidDataException("Год должен быть в диапазоне 1700–2100.");
            if (!string.IsNullOrWhiteSpace(PremieredBox.Text) && !DateTime.TryParseExact(PremieredBox.Text, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new InvalidDataException("Дата должна быть корректной и иметь формат ДД-ММ-ГГГГ.");
            Set("plot", PlotBox.Text); Set("year", YearBox.Text);
            Set("premiered", DateTime.TryParseExact(PremieredBox.Text, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate) ? releaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "");
            Set("runtime", RuntimeBox.Text); Set("mpaa", CertificationBox.SelectedItem as string);
            SetList("studio", StudiosBox.Text); SetList("country", CountriesBox.Text); SetList("genre", string.Join(Environment.NewLine, GenresBox.Text.Split(",", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)));
            SetRatings(); SetIdentifiers(); SetTechnical();
            var backup = _path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Copy(_path, backup, overwrite: false);
            var temp = _path + ".tmp";
            using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = true })) _doc.Save(writer);
            File.Move(temp, _path, overwrite: true);
            Saved = true;
            _completion.TrySetResult(ContentDialogResult.Primary);
            Close();
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog { Title = "Не удалось сохранить NFO", Content = ex.Message, CloseButtonText = "ОК", XamlRoot = RootGrid.XamlRoot };
            await dialog.ShowAsync();
        }
    }

    private static bool IsValidYear(string? text) => int.TryParse(text, out var y) && y >= 1700 && y <= 2100;
    private async void OnAddAudioTrack(object sender, RoutedEventArgs e) => await EditTrack(false, null);
    private async void OnEditAudioTrack(object sender, RoutedEventArgs e) { if (AudioTracksList.SelectedItem is TrackRow row) await EditTrack(false, row); }
    private void OnRemoveAudioTrack(object sender, RoutedEventArgs e) { if (AudioTracksList.SelectedItem is TrackRow row) _audioTracks.Remove(row); }
    private async void OnAddSubtitle(object sender, RoutedEventArgs e) => await EditTrack(true, null);
    private async void OnEditSubtitle(object sender, RoutedEventArgs e) { if (SubtitlesList.SelectedItem is TrackRow row) await EditTrack(true, row); }
    private void OnRemoveSubtitle(object sender, RoutedEventArgs e) { if (SubtitlesList.SelectedItem is TrackRow row) _subtitles.Remove(row); }

    private async Task EditTrack(bool subtitle, TrackRow? existing)
    {
        var fields = subtitle ? new[] { "Язык", "Формат", "Название", "По умолчанию (true/false)", "Принудительные (true/false)" } : new[] { "Кодек", "Язык", "Каналы", "Битрейт", "Частота дискретизации", "Название" };
        var panel = new StackPanel { Spacing = 6 };
        var inputs = new List<TextBox>();
        for (int i = 0; i < fields.Length; i++) { var input = new TextBox { Header = fields[i], Text = existing != null && i < existing.Values.Length ? existing.Values[i] : "" }; inputs.Add(input); panel.Children.Add(input); }
        var dialog = new ContentDialog { Title = (existing == null ? "Добавить " : "Изменить ") + (subtitle ? "субтитры" : "звуковую дорожку"), Content = new ScrollViewer { Content = panel, MaxHeight = 520 }, PrimaryButtonText = existing == null ? "Добавить" : "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary, RequestedTheme = ElementTheme.Dark, XamlRoot = RootGrid.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var row = new TrackRow { Values = inputs.Select(x => x.Text.Trim()).ToArray() };
        var collection = subtitle ? _subtitles : _audioTracks;
        if (existing == null) collection.Add(row); else { var index = collection.IndexOf(existing); if (index >= 0) collection[index] = row; }
    }

    private void OnYearChanged(object sender, TextChangedEventArgs e)
    {
        if (YearBox == null) return;
        YearBox.BorderBrush = string.IsNullOrWhiteSpace(YearBox.Text) || IsValidYear(YearBox.Text) ? null : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 235, 65, 65));
    }
    private void OnDateChanged(object sender, TextChangedEventArgs e)
    {
        if (PremieredBox == null) return;
        var text = PremieredBox.Text;
        bool valid = string.IsNullOrWhiteSpace(text) || (text.Length == 10 && text[2] == '-' && text[5] == '-' &&
            int.TryParse(text.Substring(0, 2), out var day) && day >= 1 && day <= 31 &&
            int.TryParse(text.Substring(3, 2), out var month) && month >= 1 && month <= 12 &&
            int.TryParse(text.Substring(6, 4), out var year) && year >= 1700 && year <= 2100 &&
            DateTime.TryParseExact(text, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        PremieredBox.BorderBrush = valid ? null : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 235, 65, 65));
    }
    private async void OnAddRating(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8 };
        var source = new ComboBox { Header = "Источник", ItemsSource = RatingSources, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var value = new TextBox { Header = "Рейтинг", PlaceholderText = "Например, 7.8" };
        var votes = new TextBox { Header = "Количество голосов", PlaceholderText = "Например, 12500" };
        value.BeforeTextChanging += NumericOrDecimalOnly; votes.BeforeTextChanging += DigitsOnly;
        panel.Children.Add(source); panel.Children.Add(value); panel.Children.Add(votes);
        var dialog = new ContentDialog { Title = "Добавить рейтинг", Content = panel, PrimaryButtonText = "Добавить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary, RequestedTheme = ElementTheme.Dark, XamlRoot = RootGrid.XamlRoot };
        dialog.PrimaryButtonClick += (sender, args) => { if (!decimal.TryParse(value.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal parsedRating) || parsedRating < 0 || parsedRating > 10 || !int.TryParse(votes.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedVotes) || parsedVotes < 0) args.Cancel = true; };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) _ratings.Add(new RatingRow { Source = source.SelectedItem?.ToString() ?? "tmdb", Value = value.Text.Replace(',', '.'), Votes = votes.Text });
    }
    private static void DigitsOnly(TextBox sender, TextBoxBeforeTextChangingEventArgs args) => args.Cancel = args.NewText.Any(c => !char.IsDigit(c));
    private static void NumericOrDecimalOnly(TextBox sender, TextBoxBeforeTextChangingEventArgs args) => args.Cancel = args.NewText.Any(c => !char.IsDigit(c) && c != '.' && c != ',') || args.NewText.Count(c => c == '.' || c == ',') > 1;
    private async void OnEditRating(object sender, RoutedEventArgs e)
    {
        if (RatingsList.SelectedItem is not RatingRow selected) return;
        var panel = new StackPanel { Spacing = 8 };
        var source = new ComboBox { Header = "Источник", ItemsSource = RatingSources, SelectedItem = selected.Source, HorizontalAlignment = HorizontalAlignment.Stretch };
        var value = new TextBox { Header = "Рейтинг", Text = selected.Value, PlaceholderText = "Например, 7.8" };
        var votes = new TextBox { Header = "Количество голосов", Text = selected.Votes, PlaceholderText = "Например, 12500" };
        value.BeforeTextChanging += NumericOrDecimalOnly; votes.BeforeTextChanging += DigitsOnly;
        panel.Children.Add(source); panel.Children.Add(value); panel.Children.Add(votes);
        var dialog = new ContentDialog { Title = "Изменить рейтинг", Content = panel, PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary, RequestedTheme = ElementTheme.Dark, XamlRoot = RootGrid.XamlRoot };
        dialog.PrimaryButtonClick += (s, args) => { if (!decimal.TryParse(value.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal parsedRating) || parsedRating < 0 || parsedRating > 10 || !int.TryParse(votes.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedVotes) || parsedVotes < 0) args.Cancel = true; };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var index = _ratings.IndexOf(selected);
            if (index >= 0) _ratings[index] = new RatingRow { Source = source.SelectedItem?.ToString() ?? "tmdb", Value = value.Text.Replace(',', '.'), Votes = votes.Text };
            RatingsList.SelectedIndex = index;
        }
    }
    private void OnRemoveRating(object sender, RoutedEventArgs e) { if (RatingsList.SelectedItem is RatingRow item) _ratings.Remove(item); }
    private async void OnAddIdentifier(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8 };
        var source = new ComboBox { Header = "Источник", ItemsSource = RatingSources, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var id = new TextBox { Header = "Идентификатор", PlaceholderText = "Введите идентификатор" };
        panel.Children.Add(source); panel.Children.Add(id);
        var dialog = new ContentDialog { Title = "Добавить идентификатор", Content = panel, PrimaryButtonText = "Добавить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary, RequestedTheme = ElementTheme.Dark, XamlRoot = RootGrid.XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(id.Text)) _identifiers.Add(new IdentifierRow { Source = source.SelectedItem?.ToString() ?? "tmdb", Identifier = id.Text.Trim() });
    }
    private async void OnEditIdentifier(object sender, RoutedEventArgs e)
    {
        if (IdentifiersList.SelectedItem is not IdentifierRow selected) return;
        var panel = new StackPanel { Spacing = 8 };
        var source = new ComboBox { Header = "Источник", ItemsSource = RatingSources, SelectedItem = selected.Source, HorizontalAlignment = HorizontalAlignment.Stretch };
        var id = new TextBox { Header = "Идентификатор", Text = selected.Identifier, PlaceholderText = "Введите идентификатор" };
        panel.Children.Add(source); panel.Children.Add(id);
        var dialog = new ContentDialog { Title = "Изменить идентификатор", Content = panel, PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary, RequestedTheme = ElementTheme.Dark, XamlRoot = RootGrid.XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(id.Text))
        {
            var index = _identifiers.IndexOf(selected);
            if (index >= 0) _identifiers[index] = new IdentifierRow { Source = source.SelectedItem?.ToString() ?? "tmdb", Identifier = id.Text.Trim() };
            IdentifiersList.SelectedIndex = index;
        }
    }

    private void OnIdentifierSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionVisuals(IdentifiersList, "IdentifierSelectionIndicator", "IdentifierRowBackground");
    private void OnRatingSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionVisuals(RatingsList, "RatingSelectionIndicator", "RatingRowBackground");

    private static void UpdateSelectionVisuals(ListView list, string indicatorName, string backgroundName)
    {
        // ListView containers may be realized asynchronously, so refresh now and after layout.
        void Refresh()
        {
            for (int i = 0; i < list.Items.Count; i++)
            {
                if (list.ContainerFromIndex(i) is not ListViewItem container) continue;
                var indicator = FindNamedElement<Border>(container, indicatorName);
                var background = FindNamedElement<Border>(container, backgroundName);
                bool selected = ReferenceEquals(list.SelectedItem, list.Items[i]);
                if (indicator != null) indicator.Opacity = selected ? 1 : 0;
                if (background != null) background.Background = new SolidColorBrush(selected ? ColorHelper.FromArgb(255, 48, 48, 48) : ColorHelper.FromArgb(0, 0, 0, 0));
            }
        }
        Refresh();
        list.DispatcherQueue.TryEnqueue(Refresh);
    }

    private static T? FindNamedElement<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name) return match;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var result = FindNamedElement<T>(VisualTreeHelper.GetChild(root, i), name);
            if (result != null) return result;
        }
        return null;
    }

    private void OnRemoveIdentifier(object sender, RoutedEventArgs e) { if (IdentifiersList.SelectedItem is IdentifierRow item) _identifiers.Remove(item); }
    private async void OnAddGenre(object sender, RoutedEventArgs e)
    {
        var list = new ListView { ItemsSource = StandardGenres, SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 360 };
        var dialog = new ContentDialog { Title = "Добавить жанры", Content = list, PrimaryButtonText = "Добавить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary, RequestedTheme = ElementTheme.Dark, XamlRoot = RootGrid.XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var genres = GenresBox.Text.Split(",", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var item in list.SelectedItems.Cast<string>())
                if (!genres.Any(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase))) genres.Add(item);
            GenresBox.Text = string.Join(", ", genres);
        }
    }

    private void Set(string name, string? value)
    {
        var node = _root.SelectSingleNode(name);
        var clean = value?.Trim() ?? "";
        if (clean.Length == 0) { if (node?.ParentNode != null) node.ParentNode.RemoveChild(node); return; }
        if (node == null) { node = _doc.CreateElement(name); _root.AppendChild(node); }
        node.InnerText = clean;
    }

    private void SetList(string name, string text)
    {
        foreach (var old in Elements(name)) _root.RemoveChild(old);
        foreach (var line in Lines(text)) { var node = _doc.CreateElement(name); node.InnerText = line; _root.AppendChild(node); }
    }

    private void SetRatings()
    {
        var lines = _ratings.Select(x => new[] { x.Source, x.Value, x.Votes }).Where(x => x[0].Length > 0).ToList();
        var ratings = _root.SelectSingleNode("ratings") as XmlElement;
        if (ratings == null) { ratings = _doc.CreateElement("ratings"); _root.AppendChild(ratings); }
        foreach (XmlNode n in ratings.SelectNodes("rating")!.Cast<XmlNode>().ToList()) ratings.RemoveChild(n);
        foreach (var p in lines)
        {
            var r = _doc.CreateElement("rating"); r.SetAttribute("name", p[0]); r.SetAttribute("max", "10");
            r.SetAttribute("default", ratings.ChildNodes.Count == 0 ? "true" : "false");
            var value = _doc.CreateElement("value"); value.InnerText = p[1]; r.AppendChild(value);
            var votes = _doc.CreateElement("votes"); votes.InnerText = p.Length > 2 ? p[2] : "0"; r.AppendChild(votes); ratings.AppendChild(r);
        }
        if (lines.Count == 0 && ratings.ParentNode != null) ratings.ParentNode.RemoveChild(ratings);
    }

    private void SetIdentifiers()
    {
        var items = _identifiers.Select(x => new[] { x.Source, x.Identifier }).Where(x => x[0].Length > 0 && x[1].Length > 0).ToList();
        foreach (var old in Elements("uniqueid")) _root.RemoveChild(old);
        foreach (var p in items) { var id = _doc.CreateElement("uniqueid"); id.SetAttribute("type", p[0]); id.InnerText = p[1]; _root.AppendChild(id); }
        var tmdb = items.FirstOrDefault(x => x[0].Equals("tmdb", StringComparison.OrdinalIgnoreCase));
        var imdb = items.FirstOrDefault(x => x[0].Equals("imdb", StringComparison.OrdinalIgnoreCase));
        Set("tmdbid", tmdb?[1]);
        Set("imdbid", imdb?[1]);
    }

    private void SetTechnical()
    {
        var parent = _root.SelectSingleNode("fileinfo/streamdetails") as XmlElement
            ?? _root.SelectSingleNode("streamdetails") as XmlElement;
        if (parent == null)
        {
            var fileinfo = _root.SelectSingleNode("fileinfo") as XmlElement;
            if (fileinfo == null) { fileinfo = _doc.CreateElement("fileinfo"); _root.AppendChild(fileinfo); }
            parent = _doc.CreateElement("streamdetails"); fileinfo.AppendChild(parent);
        }
        var video = parent.SelectSingleNode("video") as XmlElement;
        if (video == null) { video = _doc.CreateElement("video"); parent.AppendChild(video); }
        SetChild(video, "codec", VideoCodecBox.Text);
        SetChild(video, "bitrate", VideoBitrateBox.Text); SetChild(video, "width", WidthBox.Text);
        SetChild(video, "height", HeightBox.Text); SetChild(video, "framerate", FrameRateBox.Text);
        SetChild(video, "hdrtype", HdrBox.Text); SetChild(video, "aspect", AspectBox.SelectedItem?.ToString() == "Неизвестно" ? "" : AspectBox.SelectedItem?.ToString());
        var audioText = string.Join(Environment.NewLine, _audioTracks.Select(x => string.Join(" | ", x.Values)));
        var subtitlesText = string.Join(Environment.NewLine, _subtitles.Select(x => string.Join(" | ", x.Values)));
        if (!string.Equals(audioText, _originalAudioTracksText, StringComparison.Ordinal))
            ReplaceTracks(parent, "audio", audioText, new[] { "codec", "language", "channels", "bitrate", "samplerate", "title" });
        if (!string.Equals(subtitlesText, _originalSubtitlesText, StringComparison.Ordinal))
            ReplaceTracks(parent, "subtitle", subtitlesText, new[] { "language", "codec", "title", "default", "forced" });
        if (!parent.HasChildNodes && parent.ParentNode != null) parent.ParentNode.RemoveChild(parent);
    }

    private void SetChild(XmlElement parent, string name, string? value)
    {
        var child = parent.SelectSingleNode(name);
        var clean = value?.Trim() ?? "";
        if (clean.Length == 0) { if (child?.ParentNode != null) child.ParentNode.RemoveChild(child); return; }
        if (child == null) { child = _doc.CreateElement(name); parent.AppendChild(child); }
        child.InnerText = clean;
    }

    private void ReplaceTracks(XmlElement parent, string elementName, string text, string[] fields)
    {
        foreach (XmlNode old in parent.SelectNodes(elementName)!.Cast<XmlNode>().ToList()) parent.RemoveChild(old);
        foreach (var line in Lines(text))
        {
            var values = ParseLine(line); var track = _doc.CreateElement(elementName);
            for (int i = 0; i < fields.Length && i < values.Length; i++) SetChild(track, fields[i], values[i]);
            if (track.HasChildNodes) parent.AppendChild(track);
        }
    }

    private static string[] Lines(string? text) => (text ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static string[] ParseLine(string line) => line.Split('|').Select(x => x.Trim()).ToArray();
}
