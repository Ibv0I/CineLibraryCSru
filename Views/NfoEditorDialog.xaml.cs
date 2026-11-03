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
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using CineLibraryCS.Services;
using CineLibraryCS.Services.Tmdb;

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
    private static readonly string[] RussianCertifications =
    {
        "Без ограничений", "0+", "6+", "12+", "16+", "18+", "21+",
        "G", "PG", "PG-13", "R", "NC-17", "NR", "Not Rated", "Unrated",
        "TV-Y", "TV-Y7", "TV-G", "TV-PG", "TV-14", "TV-MA",
        "U", "U/A", "A", "12A", "15", "18", "M", "MA15+", "R18+"
    };
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
        BudgetBox.Text = FirstNonEmpty(Get("budget"), "—");
        RevenueBox.Text = FirstNonEmpty(Get("revenue"), "—");
        AwardsBox.Text = string.Join(Environment.NewLine, Elements("awards/award").Select(x => x.InnerText));
        if (string.IsNullOrWhiteSpace(AwardsBox.Text)) AwardsBox.Text = Get("awards") ?? "";
        var certification = Get("mpaa") ?? Get("certification");
        CertificationBox.SelectedItem = RussianCertifications.FirstOrDefault(x =>
            string.Equals(x, certification, StringComparison.OrdinalIgnoreCase));
        if (CertificationBox.SelectedItem == null &&
            !string.IsNullOrWhiteSpace(certification) &&
            !certification.Equals("Неизвестно", StringComparison.OrdinalIgnoreCase))
        {
            var certificationOptions = RussianCertifications.ToList();
            certificationOptions.Add(certification);
            CertificationBox.ItemsSource = certificationOptions;
            CertificationBox.SelectedItem = certification;
        }
        StudiosBox.Text = string.Join(Environment.NewLine, Elements("studio").Select(x => x.InnerText));
        CountriesBox.Text = string.Join(Environment.NewLine, Elements("country").Select(x => x.InnerText));
        GenresBox.Text = string.Join(", ", Elements("genre").Select(x => x.InnerText));
        _ratings.Clear();
        foreach (var x in Elements("ratings/rating"))
        {
            var source = NormalizeRatingSource(x.GetAttribute("name"));
            if (_ratings.Any(row => NormalizeRatingSource(row.Source).Equals(source, StringComparison.OrdinalIgnoreCase)))
                continue;
            _ratings.Add(new RatingRow { Source = source, Value = Child(x, "value") ?? x.InnerText, Votes = Child(x, "votes") ?? "0" });
        }
        _identifiers.Clear();
        foreach (var x in Elements("uniqueid"))
        {
            var source = NormalizeIdentifierSource(x.GetAttribute("type"));
            if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(x.InnerText) &&
                !_identifiers.Any(row => NormalizeIdentifierSource(row.Source) == source))
                _identifiers.Add(new IdentifierRow { Source = source, Identifier = x.InnerText.Trim() });
        }
        AddLegacyIdentifierIfMissing("tmdb", Get("tmdbid"));
        AddLegacyIdentifierIfMissing("imdb", Get("imdbid"));
        AddLegacyIdentifierIfMissing("kinopoisk", Get("kinopoiskid"));
        OnYearChanged(YearBox, null!); OnDateChanged(PremieredBox, null!);

        var video = FindStream("video");
        VideoCodecBox.Text = Child(video, "codec") ?? "";
        VideoBitrateBox.Text = Child(video, "bitrate") ?? "";
        WidthBox.Text = Child(video, "width") ?? ""; HeightBox.Text = Child(video, "height") ?? "";
        FrameRateBox.Text = Child(video, "framerate") ?? ""; HdrBox.Text = Child(video, "hdrtype") ?? "";
        _audioTracks.Clear(); foreach (var x in StreamElements("audio")) _audioTracks.Add(new TrackRow { Values = new[] { Child(x,"codec"), Child(x,"language"), Child(x,"channels"), Child(x,"bitrate"), Child(x,"samplerate") ?? Child(x,"samplingrate"), Child(x,"title") }.Select(v => v ?? "").ToArray() });
        _subtitles.Clear(); foreach (var x in StreamElements("subtitle")) _subtitles.Add(new TrackRow { Values = new[] { Child(x,"language"), Child(x,"codec"), Child(x,"title"), Child(x,"default"), Child(x,"forced") }.Select(v => v ?? "").ToArray() });
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
            Set("budget", BudgetBox.Text == "—" ? "" : BudgetBox.Text);
            Set("revenue", RevenueBox.Text == "—" ? "" : RevenueBox.Text);
            SetAwards(AwardsBox.Text);
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

    private void SetAwards(string text)
    {
        var old = _root.SelectSingleNode("awards");
        if (old?.ParentNode != null) old.ParentNode.RemoveChild(old);
        var lines = Lines(text);
        if (lines.Length == 0) return;
        var awards = _doc.CreateElement("awards");
        foreach (var line in lines) { var item = _doc.CreateElement("award"); item.InnerText = line; awards.AppendChild(item); }
        _root.AppendChild(awards);
    }

    private async void OnCollectMissingInfo(object sender, RoutedEventArgs e)
    {
        CollectMissingInfoButton.IsEnabled = false;
        CollectMissingInfoButton.Content = "Сбор информации…";
        var added = new List<string>();
        var unavailable = new List<string>();
        var errors = new List<string>();

        try
        {
            var title = FirstNonEmpty(TitleBox.Text, OriginalTitleBox.Text,
                Path.GetFileName(Path.GetDirectoryName(_path)) ?? "");
            var originalTitle = FirstNonEmpty(OriginalTitleBox.Text, TitleBox.Text);
            int? year = int.TryParse(YearBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear)
                && parsedYear >= 1700 && parsedYear <= 2100 ? parsedYear : null;

            var tmdbId = GetIdentifierValue("tmdb");
            var imdbId = GetIdentifierValue("imdb");
            var kpIdText = GetIdentifierValue("kinopoisk");
            int? kpId = int.TryParse(kpIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedKp) && parsedKp > 0
                ? parsedKp : null;

            TmdbMovie? tmdbMovie = null;
            TmdbMovie? kpMovie = null;
            using var tmdbClient = new TmdbClient(language: "ru-RU");

            // 1. Use an existing TMDb ID first.
            if (int.TryParse(tmdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var existingTmdbId) && existingTmdbId > 0)
            {
                try { tmdbMovie = await tmdbClient.GetMovieDetailsAsync(existingTmdbId); }
                catch (Exception ex) { errors.Add("TMDb: " + ex.Message); }
            }

            // 2. If TMDb ID is missing, resolve it from IMDb.
            if (tmdbMovie == null && !string.IsNullOrWhiteSpace(imdbId))
            {
                try
                {
                    var foundId = await tmdbClient.FindMovieIdByImdbAsync(imdbId);
                    if (foundId.HasValue)
                    {
                        AddIdentifierIfMissing("tmdb", foundId.Value.ToString(CultureInfo.InvariantCulture), added);
                        tmdbMovie = await tmdbClient.GetMovieDetailsAsync(foundId.Value);
                    }
                }
                catch (Exception ex) { errors.Add("Поиск TMDb по IMDb: " + ex.Message); }
            }

            // 3. If both TMDb and IMDb are absent, search by title/year.
            if (tmdbMovie == null && string.IsNullOrWhiteSpace(tmdbId) &&
                !string.IsNullOrWhiteSpace(title))
            {
                try
                {
                    var candidate = await FindExactTmdbMatchAsync(tmdbClient, title, originalTitle, year);
                    if (candidate != null)
                    {
                        AddIdentifierIfMissing("tmdb", candidate.TmdbId.ToString(CultureInfo.InvariantCulture), added);
                        tmdbMovie = await tmdbClient.GetMovieDetailsAsync(candidate.TmdbId);
                    }
                }
                catch (Exception ex) { errors.Add("Поиск фильма в TMDb: " + ex.Message); }
            }

            // 4. Resolve Kinopoisk ID/details. Try Kinopoisk API Unofficial first,
            // then PoiskKino API when the first provider is unavailable or has no match.
            var hasUnofficial = !string.IsNullOrWhiteSpace(KinopoiskSettings.ApiToken);
            var hasPoiskKino = !string.IsNullOrWhiteSpace(PoiskKinoSettings.ApiToken);
            if (!kpId.HasValue && !string.IsNullOrWhiteSpace(title))
            {
                if (hasUnofficial)
                {
                    try
                    {
                        using var kpClient = new KinopoiskClient();
                        var candidate = await FindExactKinopoiskMatchAsync(kpClient, title, originalTitle, year);
                        if (candidate != null)
                        {
                            var id = candidate.KinopoiskId > 0 ? candidate.KinopoiskId : candidate.TmdbId;
                            if (id > 0) { kpId = id; AddIdentifierIfMissing("kinopoisk", id.ToString(CultureInfo.InvariantCulture), added); }
                        }
                    }
                    catch (Exception ex) { errors.Add("КиноПоиск API Unofficial: " + FormatKinopoiskApiError(ex)); }
                }
                if (!kpId.HasValue && hasPoiskKino)
                {
                    try
                    {
                        using var poisk = new PoiskKinoClient();
                        var candidate = await FindExactPoiskKinoMatchAsync(poisk, title, originalTitle, year);
                        if (candidate != null && candidate.KinopoiskId > 0)
                        {
                            kpId = candidate.KinopoiskId;
                            AddIdentifierIfMissing("kinopoisk", kpId.Value.ToString(CultureInfo.InvariantCulture), added);
                        }
                    }
                    catch (Exception ex) { errors.Add("ПоискКино API: " + ex.Message); }
                }
            }

            if (kpId.HasValue)
            {
                if (hasUnofficial)
                {
                    try { using var kp = new KinopoiskClient(); kpMovie = await kp.GetMovieDetailsAsync(kpId.Value); }
                    catch (Exception ex) { errors.Add("Данные КиноПоиск API Unofficial: " + FormatKinopoiskApiError(ex)); }
                }
                if ((kpMovie == null || kpMovie.Rating <= 0) && hasPoiskKino)
                {
                    try
                    {
                        using var poisk = new PoiskKinoClient();
                        var alternative = await poisk.GetMovieDetailsAsync(kpId.Value);
                        if (alternative != null && (kpMovie == null || alternative.Rating > kpMovie.Rating ||
                            ((kpMovie.ImdbRating ?? 0) <= 0 && (alternative.ImdbRating ?? 0) > 0)))
                            kpMovie = alternative;
                    }
                    catch (Exception ex) { errors.Add("Данные ПоискКино API: " + ex.Message); }
                }
                if (kpMovie == null) unavailable.Add("Данные КиноПоиска (оба API не вернули фильм)");
            }
            else if (!hasUnofficial && !hasPoiskKino)
                unavailable.Add("КиноПоиск ID (не настроены оба API)");
            else
                unavailable.Add("КиноПоиск ID (совпадение не найдено через оба API)");

            // Kinopoisk often provides IMDb ID. Use it to resolve TMDb if still absent.
            if (string.IsNullOrWhiteSpace(imdbId) && !string.IsNullOrWhiteSpace(kpMovie?.ImdbId))
            {
                imdbId = kpMovie!.ImdbId!.Trim();
                AddIdentifierIfMissing("imdb", imdbId, added);
            }
            if (tmdbMovie == null && string.IsNullOrWhiteSpace(tmdbId) && !string.IsNullOrWhiteSpace(imdbId))
            {
                try
                {
                    var foundId = await tmdbClient.FindMovieIdByImdbAsync(imdbId);
                    if (foundId.HasValue)
                    {
                        AddIdentifierIfMissing("tmdb", foundId.Value.ToString(CultureInfo.InvariantCulture), added);
                        tmdbMovie = await tmdbClient.GetMovieDetailsAsync(foundId.Value);
                    }
                }
                catch (Exception ex) { errors.Add("Поиск TMDb по IMDb: " + ex.Message); }
            }

            // Fill only empty fields. Existing user-edited NFO values are never overwritten.
            if (string.IsNullOrWhiteSpace(imdbId) && !string.IsNullOrWhiteSpace(tmdbMovie?.ImdbId))
            {
                imdbId = tmdbMovie!.ImdbId!.Trim();
                AddIdentifierIfMissing("imdb", imdbId, added);
            }

            var resolvedYear = tmdbMovie != null && tmdbMovie.Year > 0 ? tmdbMovie.Year : (kpMovie?.Year ?? 0);
            // Store a tagline only when it contains Russian (Cyrillic) text.
            // Neither API is guaranteed to return a localized slogan; never fall back to English.
            var resolvedTagline = FirstRussianTagline(kpMovie?.Tagline, tmdbMovie?.Tagline);
            // Prefer Russian-localized TMDb genres; translate common English genre names
            // returned by either Kinopoisk provider as a fallback.
            var tmdbGenres = tmdbMovie?.Genres ?? new List<TmdbNamed>();
            var kpGenres = kpMovie?.Genres ?? new List<TmdbNamed>();
            // Keep Russian genre names in preference to English ones. English names are
            // translated through the local map only when neither provider has Russian names.
            var russianGenres = tmdbGenres.Concat(kpGenres)
                .Where(g => ContainsCyrillic(g.Name))
                .Select(g => NormalizeRussianGenre(g.Name));
            var englishGenres = tmdbGenres.Concat(kpGenres)
                .Where(g => !ContainsCyrillic(g.Name))
                .Select(g => NormalizeRussianGenre(g.Name));
            var resolvedGenres = (russianGenres.Any() ? russianGenres : englishGenres)
                .Where(g => ContainsCyrillic(g)) // never write an untranslated English genre to NFO
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(g => new TmdbNamed { Name = CapitalizeGenre(g) }).ToList();
            if (string.IsNullOrWhiteSpace(YearBox.Text) && resolvedYear > 0)
            {
                YearBox.Text = resolvedYear.ToString(CultureInfo.InvariantCulture);
                added.Add("Год");
            }
            if (string.IsNullOrWhiteSpace(TaglineBox.Text) && !string.IsNullOrWhiteSpace(resolvedTagline))
            {
                TaglineBox.Text = resolvedTagline;
                added.Add("Слоган");
            }
            // On every information-collection run, inspect existing NFO genres.
            // If English genres are present, remove them and replace with Russian names.
            var existingGenres = GenresBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var hasEnglishGenres = existingGenres.Any(g => !ContainsCyrillic(g));
            var normalizedExistingRussian = existingGenres.Where(ContainsCyrillic)
                .Select(g => CapitalizeGenre(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (hasEnglishGenres)
            {
                var replacement = resolvedGenres.Select(g => g.Name).ToList();
                GenresBox.Text = string.Join(", ", replacement);
                if (replacement.Count > 0) added.Add("Жанры (заменены на русские)");
                else if (normalizedExistingRussian.Count > 0) GenresBox.Text = string.Join(", ", normalizedExistingRussian);
                else GenresBox.Text = string.Empty;
            }
            else if (normalizedExistingRussian.Count > 0)
            {
                GenresBox.Text = string.Join(", ", normalizedExistingRussian);
            }
            else if (resolvedGenres.Count > 0)
            {
                GenresBox.Text = string.Join(", ", resolvedGenres.Select(x => x.Name));
                added.Add("Жанры");
            }
            if (CertificationBox.SelectedItem == null)
            {
                var certification = MapRussianCertification(
                    FirstNonEmpty(tmdbMovie?.RussianCertification, kpMovie?.RussianCertification,
                        tmdbMovie?.Certification, kpMovie?.Certification));
                if (certification != null)
                {
                    CertificationBox.SelectedItem = certification;
                    added.Add("Возрастной рейтинг");
                }
            }
            if (string.IsNullOrWhiteSpace(PremieredBox.Text))
            {
                var releaseDate = FirstNonEmpty(tmdbMovie?.RussianReleaseDate,
                    tmdbMovie?.ReleaseDate, kpMovie?.ReleaseDate);
                if (DateTime.TryParse(releaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    PremieredBox.Text = date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
                    added.Add("Дата выхода");
                }
            }

            // Refresh ratings for every known provider ID after discovery, not only when
            // an identifier is manually added in the dialog.
            await TryLoadRatingForIdentifierAsync("tmdb", GetIdentifierValue("tmdb"));
            await TryLoadRatingForIdentifierAsync("kinopoisk", GetIdentifierValue("kinopoisk"));
            await TryLoadRatingForIdentifierAsync("imdb", GetIdentifierValue("imdb"));
            // Награды и номинации загружаются отдельно кнопкой «Загрузить бюджет, сборы и награды».
            if (string.IsNullOrWhiteSpace(GetIdentifierValue("tmdb"))) unavailable.Add("TMDb ID");
            if (string.IsNullOrWhiteSpace(GetIdentifierValue("imdb"))) unavailable.Add("IMDb ID");
            if (string.IsNullOrWhiteSpace(GetIdentifierValue("kinopoisk"))) unavailable.Add("Кинопоиск ID");

            var report = new List<string>();
            if (added.Count > 0) report.Add("Добавлено: " + string.Join(", ", added.Distinct(StringComparer.OrdinalIgnoreCase)) + ".");
            if (unavailable.Count > 0) report.Add("Не удалось получить: " + string.Join(", ", unavailable.Distinct(StringComparer.OrdinalIgnoreCase)) + ".");
            if (errors.Count > 0)
            {
                // The same 402 can be encountered during search and details lookup.
                // Show the daily-limit explanation only once, while preserving other diagnostics.
                const string limitMessage = "Исчерпан дневной лимит запросов (HTTP 402). Для продолжения используйте API ПоискКино или дождитесь сброса лимита.";
                var has402 = errors.Any(x => x.Contains("HTTP 402", StringComparison.OrdinalIgnoreCase));
                var otherErrors = errors.Where(x => !x.Contains("HTTP 402", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (has402) otherErrors.Insert(0, limitMessage);
                if (otherErrors.Count > 0) report.Add("Ошибки источников:\n" + string.Join("\n", otherErrors));
            }
            if (report.Count == 0) report.Add("Все основные поля уже заполнены. Изменений не требуется.");

            var result = new ContentDialog
            {
                Title = "Сбор информации завершён",
                Content = new ScrollViewer { Content = new TextBlock { Text = string.Join("\n\n", report), TextWrapping = TextWrapping.Wrap }, MaxHeight = 420 },
                CloseButtonText = "ОК",
                RequestedTheme = ElementTheme.Dark,
                XamlRoot = RootGrid.XamlRoot
            };
            await result.ShowAsync();
        }
        catch (Exception ex)
        {
            var error = new ContentDialog { Title = "Не удалось собрать информацию", Content = ex.Message, CloseButtonText = "ОК", RequestedTheme = ElementTheme.Dark, XamlRoot = RootGrid.XamlRoot };
            await error.ShowAsync();
        }
        finally
        {
            CollectMissingInfoButton.IsEnabled = true;
            CollectMissingInfoButton.Content = "✦ Собрать информацию";
        }
    }

    private void AddLegacyIdentifierIfMissing(string source, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        source = NormalizeIdentifierSource(source);
        if (_identifiers.Any(x => NormalizeIdentifierSource(x.Source) == source)) return;
        _identifiers.Add(new IdentifierRow { Source = source, Identifier = value.Trim() });
    }

    private string GetIdentifierValue(string canonicalSource)
    {
        var row = _identifiers.FirstOrDefault(x => NormalizeIdentifierSource(x.Source) == canonicalSource);
        if (!string.IsNullOrWhiteSpace(row?.Identifier)) return row.Identifier.Trim();

        var legacyNode = canonicalSource switch
        {
            "tmdb" => _root.SelectSingleNode("tmdbid")?.InnerText,
            "imdb" => _root.SelectSingleNode("imdbid")?.InnerText,
            "kinopoisk" => _root.SelectSingleNode("kinopoiskid")?.InnerText,
            _ => null
        };
        return legacyNode?.Trim() ?? "";
    }

    private static string NormalizeIdentifierSource(string source)
    {
        var value = (source ?? "").Trim().ToLowerInvariant();
        if (value is "kp" or "kinopoisk" or "kinopoiskdev" or "poiskkino" or "kinopoisk_id") return "kinopoisk";
        if (value is "imdb" or "imdbid" or "imdb_id") return "imdb";
        if (value is "tmdb" or "tmdbid" or "tmdb_id") return "tmdb";
        return value;
    }

    private void AddIdentifierIfMissing(string source, string identifier, List<string> added)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return;
        source = NormalizeIdentifierSource(source);
        var existing = _identifiers.FirstOrDefault(x => NormalizeIdentifierSource(x.Source) == source);
        if (existing != null)
        {
            if (string.IsNullOrWhiteSpace(existing.Identifier))
            {
                var index = _identifiers.IndexOf(existing);
                if (index >= 0) _identifiers[index] = new IdentifierRow { Source = source, Identifier = identifier.Trim() };
                added.Add(source.ToUpperInvariant() + " ID");
            }
            return;
        }
        _identifiers.Add(new IdentifierRow { Source = source, Identifier = identifier.Trim() });
        added.Add(source.ToUpperInvariant() + " ID");
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";

    private static string FirstRussianTagline(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var text = value.Trim();
            if (text.Any(c => c >= '\u0400' && c <= '\u04FF'))
                return text;
        }
        return string.Empty;
    }

    private static bool ContainsCyrillic(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Any(ch => ch >= '\u0400' && ch <= '\u04FF');

    private static string CapitalizeGenre(string? genre)
    {
        var value = (genre ?? string.Empty).Trim();
        if (value.Length == 0) return value;
        return char.ToUpper(value[0], CultureInfo.GetCultureInfo("ru-RU")) + value.Substring(1);
    }

    private static string NormalizeRussianGenre(string? genre)
    {
        var value = (genre ?? string.Empty).Trim();
        if (value.Any(ch => ch >= '\u0400' && ch <= '\u04FF')) return value;
        return CapitalizeGenre(value.ToLowerInvariant() switch
        {
            "action" => "Боевик", "adventure" => "Приключения", "animation" => "Анимация",
            "anime" => "Аниме", "comedy" => "Комедия", "crime" => "Криминал",
            "documentary" => "Документальный", "drama" => "Драма", "family" => "Семейный",
            "fantasy" => "Фэнтези", "history" => "История", "horror" => "Ужасы",
            "music" => "Музыка", "mystery" => "Детектив", "romance" => "Мелодрама",
            "sci-fi" or "science fiction" => "Фантастика", "tv movie" or "tv-movie" => "Телевизионный фильм",
            "thriller" => "Триллер", "war" => "Военный", "western" => "Вестерн",
            "biography" => "Биография", "sport" => "Спорт", "musical" => "Мюзикл",
            "short" => "Короткометражка",
            _ => value
        });
    }

    private static string NormalizeMatchTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return new string(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private static async Task<TmdbMovie?> FindExactTmdbMatchAsync(TmdbClient client,
        string title, string originalTitle, int? year)
    {
        foreach (var query in new[] { title, originalTitle }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var results = await client.SearchMovieAsync(query, year);
            var queryNames = new[] { title, originalTitle }.Select(NormalizeMatchTitle).Where(x => x.Length > 0).ToHashSet();
            var exact = results.FirstOrDefault(x =>
                (queryNames.Contains(NormalizeMatchTitle(x.Title)) || queryNames.Contains(NormalizeMatchTitle(x.OriginalTitle))) &&
                (!year.HasValue || x.Year == 0 || x.Year == year.Value));
            if (exact != null) return exact;
        }
        return null;
    }

    private static async Task<TmdbMovie?> FindExactKinopoiskMatchAsync(KinopoiskClient client,
        string title, string originalTitle, int? year)
    {
        foreach (var query in new[] { title, originalTitle }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var results = await client.SearchMovieAsync(query, year);
            var queryNames = new[] { title, originalTitle }.Select(NormalizeMatchTitle).Where(x => x.Length > 0).ToHashSet();
            var exact = results.FirstOrDefault(x =>
                (queryNames.Contains(NormalizeMatchTitle(x.Title)) || queryNames.Contains(NormalizeMatchTitle(x.OriginalTitle))) &&
                (!year.HasValue || x.Year == 0 || x.Year == year.Value));
            if (exact != null) return exact;
        }
        return null;
    }


    private static string FormatKinopoiskApiError(Exception exception)
    {
        if (exception is HttpRequestException http && http.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
            return "исчерпан дневной лимит запросов (HTTP 402). Для продолжения используйте API ПоискКино или дождитесь сброса лимита.";
        return exception.Message;
    }

    private static async Task<TmdbMovie?> FindExactPoiskKinoMatchAsync(PoiskKinoClient client,
        string title, string originalTitle, int? year)
    {
        var queries = new[] { title, originalTitle }.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            var results = await client.SearchMovieAsync(query, year);
            var match = results.FirstOrDefault(x =>
                (year == null || x.Year == 0 || x.Year == year.Value) &&
                (NormalizeMatchTitle(x.Title) == NormalizeMatchTitle(title) ||
                 NormalizeMatchTitle(x.Title) == NormalizeMatchTitle(originalTitle) ||
                 NormalizeMatchTitle(x.OriginalTitle) == NormalizeMatchTitle(title) ||
                 NormalizeMatchTitle(x.OriginalTitle) == NormalizeMatchTitle(originalTitle)));
            if (match != null) return match;
        }
        return null;
    }

    private static string? MapRussianCertification(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant().Replace(" ", "");
        if (normalized.Contains("age0") || normalized is "0" or "0+") return "Без ограничений";
        if (normalized.Contains("age6") || normalized is "6" or "6+") return "6+";
        if (normalized.Contains("age12") || normalized is "12" or "12+") return "12+";
        if (normalized.Contains("age16") || normalized is "16" or "16+") return "16+";
        if (normalized.Contains("age18") || normalized is "18" or "18+") return "18+";
        if (normalized.Contains("age21") || normalized is "21" or "21+") return "21+";
        // Keep foreign classifications (R, PG-13, TV-MA, etc.) in their original form.
        if (RussianCertifications.Any(x => string.Equals(x, value.Trim(), StringComparison.OrdinalIgnoreCase)))
            return RussianCertifications.First(x => string.Equals(x, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (Regex.IsMatch(value.Trim(), @"^[A-Za-z0-9+/-]{1,12}$"))
            return value.Trim().ToUpperInvariant();
        return null;
    }

    private async void OnLoadExternalData(object sender, RoutedEventArgs e)
    {
        try
        {
            var tmdbText = _identifiers.FirstOrDefault(x => x.Source.Equals("tmdb", StringComparison.OrdinalIgnoreCase))?.Identifier;
            if (!int.TryParse(tmdbText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId) || tmdbId <= 0)
                throw new InvalidOperationException("Сначала добавь корректный TMDb ID.");
            LoadExternalDataButton.IsEnabled = false; LoadExternalDataButton.Content = "Загрузка…";
            using (var client = new CineLibraryCS.Services.Tmdb.TmdbClient(language: "ru-RU"))
            {
                var movie = await client.GetMovieDetailsAsync(tmdbId);
                if (movie == null) throw new InvalidOperationException("TMDb не вернул данные фильма.");
                if (movie.Budget > 0) BudgetBox.Text = movie.Budget.ToString(CultureInfo.InvariantCulture);
                if (movie.Revenue > 0) RevenueBox.Text = movie.Revenue.ToString(CultureInfo.InvariantCulture);
            }
            var kpText = _identifiers.FirstOrDefault(x => new[] { "kinopoisk", "kinopoiskdev", "poiskkino" }.Contains(x.Source, StringComparer.OrdinalIgnoreCase))?.Identifier;
            if (int.TryParse(kpText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kpId) && kpId > 0)
            {
                List<string> awards = new();
                if (!string.IsNullOrWhiteSpace(KinopoiskSettings.ApiToken))
                {
                    try { using var kp = new KinopoiskClient(); awards = await kp.GetAwardsAsync(kpId); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Awards via Unofficial failed: {ex.Message}"); }
                }
                if (awards.Count == 0 && !string.IsNullOrWhiteSpace(PoiskKinoSettings.ApiToken))
                {
                    try { using var poisk = new PoiskKinoClient(); awards = await poisk.GetAwardsAsync(kpId); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Awards via PoiskKino failed: {ex.Message}"); }
                }
                if (awards.Count > 0) AwardsBox.Text = string.Join(Environment.NewLine, awards);
            }
            var info = new ContentDialog { Title = "Загрузка завершена", Content = "Финансовые данные получены из TMDb. Награды загружены, если API КиноПоиска предоставил их.", CloseButtonText = "ОК", RequestedTheme = ElementTheme.Dark, XamlRoot = RootGrid.XamlRoot };
            await info.ShowAsync();
        }
        catch (Exception ex) { var error = new ContentDialog { Title = "Не удалось загрузить данные", Content = ex.Message, CloseButtonText = "ОК", RequestedTheme = ElementTheme.Dark, XamlRoot = RootGrid.XamlRoot }; await error.ShowAsync(); }
        finally { LoadExternalDataButton.IsEnabled = true; LoadExternalDataButton.Content = "↓ Загрузить бюджет, сборы и награды"; }
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(id.Text))
        {
            var provider = source.SelectedItem?.ToString() ?? "tmdb";
            var identifier = id.Text.Trim();
            _identifiers.Add(new IdentifierRow { Source = provider, Identifier = identifier });
            await TryLoadRatingForIdentifierAsync(provider, identifier);
        }
    }

    private static string NormalizeRatingSource(string? source)
    {
        var value = (source ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
        return value switch
        {
            "themoviedb" or "tmdb" or "themoviedborg" => "tmdb",
            "imdb" or "internetmoviedatabase" => "imdb",
            "kinopoisk" or "kinopoiskdev" or "kp" => "kinopoisk",
            _ => (source ?? "").Trim()
        };
    }

    private void NormalizeExistingRatingSources()
    {
        var normalized = _ratings
            .Where(x => !string.IsNullOrWhiteSpace(x.Source))
            .GroupBy(x => NormalizeRatingSource(x.Source), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        _ratings.Clear();
        foreach (var row in normalized)
            _ratings.Add(new RatingRow { Source = NormalizeRatingSource(row.Source), Value = row.Value, Votes = row.Votes });
    }

    private void UpsertRating(string provider, double rating, int votes)
    {
        if (rating <= 0) return;
        provider = NormalizeRatingSource(provider);
        var existing = _ratings.FirstOrDefault(x => NormalizeRatingSource(x.Source).Equals(provider, StringComparison.OrdinalIgnoreCase));
        var row = new RatingRow
        {
            Source = provider,
            Value = rating.ToString("0.0#", CultureInfo.InvariantCulture),
            Votes = Math.Max(0, votes).ToString(CultureInfo.InvariantCulture)
        };
        if (existing != null)
        {
            var index = _ratings.IndexOf(existing);
            if (index >= 0) _ratings[index] = row;
        }
        else _ratings.Add(row);
    }

    private async Task TryLoadRatingForIdentifierAsync(string provider, string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return;
        provider = NormalizeRatingSource(provider);
        if (_ratings.Any(x => NormalizeRatingSource(x.Source).Equals(provider, StringComparison.OrdinalIgnoreCase) &&
                              double.TryParse(x.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var saved) && saved > 0))
            return;
        if (provider.Equals("imdb", StringComparison.OrdinalIgnoreCase))
        {
            var kpIdentifier = GetIdentifierValue("kinopoisk");
            if (!int.TryParse(kpIdentifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kpId) || kpId <= 0 ||
                string.IsNullOrWhiteSpace(KinopoiskSettings.ApiToken)) return;
            TmdbMovie? movie = null;
            if (!string.IsNullOrWhiteSpace(KinopoiskSettings.ApiToken))
            {
                try { using var client = new KinopoiskClient(); movie = await client.GetMovieDetailsAsync(kpId); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"IMDb rating via Unofficial failed: {ex.Message}"); }
            }
            if ((movie?.ImdbRating ?? 0) <= 0 && !string.IsNullOrWhiteSpace(PoiskKinoSettings.ApiToken))
            {
                try { using var client = new PoiskKinoClient(); movie = await client.GetMovieDetailsAsync(kpId); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"IMDb rating via PoiskKino failed: {ex.Message}"); }
            }
            if (movie?.ImdbRating is double imdbRating && imdbRating > 0)
                UpsertRating("imdb", imdbRating, Math.Max(0, movie.ImdbVoteCount ?? 0));
            return;
        }

        if (!int.TryParse(identifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0) return;
        if ((provider.Equals("kinopoisk", StringComparison.OrdinalIgnoreCase) ||
             provider.Equals("kinopoiskdev", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(KinopoiskSettings.ApiToken)) return;
        try
        {
            double rating;
            int votes;
            if (provider.Equals("tmdb", StringComparison.OrdinalIgnoreCase))
            {
                using var client = new CineLibraryCS.Services.Tmdb.TmdbClient(language: "ru-RU");
                var movie = await client.GetMovieDetailsAsync(id);
                if (movie == null || movie.Rating <= 0) return;
                rating = movie.Rating;
                votes = Math.Max(0, movie.VoteCount);
            }
            else if (provider.Equals("kinopoisk", StringComparison.OrdinalIgnoreCase) || provider.Equals("kinopoiskdev", StringComparison.OrdinalIgnoreCase))
            {
                TmdbMovie? movie = null;
                if (!string.IsNullOrWhiteSpace(KinopoiskSettings.ApiToken))
                {
                    try { using var client = new CineLibraryCS.Services.KinopoiskClient(); movie = await client.GetMovieDetailsAsync(id); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Kinopoisk rating via Unofficial failed: {ex.Message}"); }
                }
                if ((movie?.Rating ?? 0) <= 0 && !string.IsNullOrWhiteSpace(PoiskKinoSettings.ApiToken))
                {
                    try { using var client = new PoiskKinoClient(); movie = await client.GetMovieDetailsAsync(id); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Kinopoisk rating via PoiskKino failed: {ex.Message}"); }
                }
                if (movie?.Rating is not double kpRating || kpRating <= 0) return;
                rating = kpRating;
                votes = Math.Max(0, movie.KinopoiskVoteCount ?? movie.VoteCount);
                provider = "kinopoisk";
            }
            else return;

            UpsertRating(provider, rating, votes);
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog { Title = "Не удалось загрузить рейтинг", Content = ex.Message, CloseButtonText = "ОК", RequestedTheme = ElementTheme.Dark, XamlRoot = RootGrid.XamlRoot };
            await dialog.ShowAsync();
        }
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
    private void OnAudioTrackSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionVisuals(AudioTracksList, "AudioSelectionIndicator", "AudioRowBackground");
    private void OnSubtitleSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionVisuals(SubtitlesList, "SubtitleSelectionIndicator", "SubtitleRowBackground");

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
        NormalizeExistingRatingSources();
        var lines = _ratings.Select(x => new[] { NormalizeRatingSource(x.Source), x.Value, x.Votes }).Where(x => x[0].Length > 0).ToList();
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
        var tmdb = items.FirstOrDefault(x => NormalizeIdentifierSource(x[0]) == "tmdb");
        var imdb = items.FirstOrDefault(x => NormalizeIdentifierSource(x[0]) == "imdb");
        var kinopoisk = items.FirstOrDefault(x => NormalizeIdentifierSource(x[0]) == "kinopoisk");
        Set("tmdbid", tmdb?[1]);
        Set("imdbid", imdb?[1]);
        Set("kinopoiskid", kinopoisk?[1]);
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
        SetChild(video, "hdrtype", HdrBox.Text);
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
