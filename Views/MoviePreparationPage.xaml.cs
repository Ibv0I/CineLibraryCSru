using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CineLibraryCS.Services.Tmdb;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml;
using MediaInfo;

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
        var picker = new TmdbPickerDialog(client, candidate.SearchTitle, year);
        var result = await picker.ShowAsync();
        if (result != ContentDialogResult.Primary || picker.Picked == null) return;
        var preview = new TmdbMoviePreviewDialog(client, picker.Picked);
        var previewResult = await preview.ShowAsync();
        if (previewResult != ContentDialogResult.Primary || preview.Movie == null) return;

        candidate.SelectedMovie = preview.Movie;
        candidate.SelectedPosterPath = preview.SelectedPosterPath ?? preview.Movie.PosterPath;
        candidate.SelectedBackdropPath = preview.SelectedBackdropPath ?? preview.Movie.BackdropPath;
        candidate.MatchSummary = $"Готово к сохранению: {preview.Movie.Title}{(preview.Movie.Year > 0 ? $" ({preview.Movie.Year})" : string.Empty)} · TMDb ID: {preview.Movie.TmdbId}";
        RefreshCandidates();
        StatusText.Text = $"Для папки «{candidate.FolderName}» выбраны метаданные и изображения. Нажмите «Сохранить файлы», чтобы создать movie.nfo, poster.jpg и fanart.jpg.";
    }

    private void RefreshCandidates()
    {
        FoldersList.ItemsSource = null;
        FoldersList.ItemsSource = _candidates;
    }

    private async void OnSavePreparedMovieClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MovieFolderCandidate candidate || candidate.SelectedMovie is not TmdbMovie movie)
        {
            StatusText.Text = "Сначала выберите совпадение фильма в TMDb.";
            return;
        }

        var nfoPath = Path.Combine(candidate.FolderPath, "movie.nfo");
        var posterPath = Path.Combine(candidate.FolderPath, "poster.jpg");
        var fanartPath = Path.Combine(candidate.FolderPath, "fanart.jpg");
        var existing = new[] { nfoPath, posterPath, fanartPath }.Where(File.Exists).Select(Path.GetFileName).ToArray();
        if (existing.Length > 0)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Файлы уже существуют",
                Content = "Будут заменены: " + string.Join(", ", existing) + ". Продолжить?",
                PrimaryButtonText = "Заменить",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        var tempSuffix = ".tmp-" + Guid.NewGuid().ToString("N");
        var tempNfo = nfoPath + tempSuffix;
        var tempPoster = posterPath + tempSuffix;
        var tempFanart = fanartPath + tempSuffix;
        try
        {
            StatusText.Text = $"Сохранение метаданных и изображений: {candidate.FolderName}…";
            using var client = new TmdbClient(language: "ru-RU");
            var posterFile = candidate.SelectedPosterPath ?? movie.PosterPath;
            var backdropFile = candidate.SelectedBackdropPath ?? movie.BackdropPath;
            if (string.IsNullOrWhiteSpace(posterFile))
                throw new InvalidOperationException("Не выбран постер, а TMDb не предоставил постер по умолчанию.");
            if (string.IsNullOrWhiteSpace(backdropFile))
                throw new InvalidOperationException("Не выбран фон, а TMDb не предоставил фон по умолчанию.");

            if (!await client.DownloadImageAsync(client.GetImageUrl(posterFile, "original"), tempPoster))
                throw new IOException("Не удалось загрузить постер из TMDb.");
            if (!await client.DownloadImageAsync(client.GetImageUrl(backdropFile, "original"), tempFanart))
                throw new IOException("Не удалось загрузить фоновое изображение из TMDb.");

            var technicalInfo = AnalyzeMediaFile(candidate.Videos.OrderByDescending(GetFileSizeSafe).FirstOrDefault() ?? string.Empty);
            WriteMovieNfo(tempNfo, movie, technicalInfo);
            File.Move(tempPoster, posterPath, true);
            File.Move(tempFanart, fanartPath, true);
            File.Move(tempNfo, nfoPath, true); // Write NFO last, so scanner only sees completed preparation.

            candidate.MatchSummary = $"Сохранено: {movie.Title}{(movie.Year > 0 ? $" ({movie.Year})" : string.Empty)} · movie.nfo, poster.jpg, fanart.jpg";
            RefreshCandidates();
            StatusText.Text = technicalInfo == null
                ? $"Файлы сохранены в папке «{candidate.FolderName}», но технические параметры видео не удалось прочитать. Проверьте формат файла."
                : $"Файлы успешно сохранены в папке «{candidate.FolderName}» вместе с длительностью, видеопараметрами, аудиодорожками и языками субтитров.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось сохранить подготовку фильма: " + ex.Message;
        }
        finally
        {
            TryDelete(tempNfo); TryDelete(tempPoster); TryDelete(tempFanart);
        }
    }

    private static void WriteMovieNfo(string path, TmdbMovie movie, MediaTechnicalInfo? technicalInfo)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false), CloseOutput = true };
        using var writer = XmlWriter.Create(path, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("movie");
        WriteElement(writer, "title", movie.Title);
        WriteElement(writer, "originaltitle", movie.OriginalTitle);
        WriteElement(writer, "sorttitle", movie.Title);
        if (movie.Year > 0) WriteElement(writer, "year", movie.Year.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(movie.Tagline)) WriteElement(writer, "tagline", movie.Tagline);
        if (!string.IsNullOrWhiteSpace(movie.Overview))
        {
            WriteElement(writer, "plot", movie.Overview);
            WriteElement(writer, "outline", movie.Overview);
        }
        if (!string.IsNullOrWhiteSpace(movie.ReleaseDate))
        {
            WriteElement(writer, "premiered", movie.ReleaseDate);
            WriteElement(writer, "releasedate", movie.ReleaseDate);
        }
        var durationSeconds = technicalInfo?.DurationSeconds;
        var runtimeMinutes = durationSeconds.HasValue && durationSeconds.Value > 0
            ? (int)Math.Round(durationSeconds.Value / 60.0)
            : movie.Runtime;
        if (runtimeMinutes > 0) WriteElement(writer, "runtime", runtimeMinutes.ToString(CultureInfo.InvariantCulture));
        // Human-readable duration in hours and minutes; runtime above remains in Kodi's standard minutes format.
        if (durationSeconds.HasValue && durationSeconds.Value > 0)
        {
            var totalMinutes = (long)Math.Round(durationSeconds.Value / 60.0);
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;
            WriteElement(writer, "duration", hours > 0
                ? $"{hours} ч {minutes:D2} мин"
                : $"{minutes} мин");
        }
        WriteElement(writer, "rating", movie.Rating.ToString("0.0", CultureInfo.InvariantCulture));
        WriteElement(writer, "votes", movie.VoteCount.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(movie.Certification)) WriteElement(writer, "mpaa", movie.Certification);
        foreach (var genre in movie.Genres) WriteElement(writer, "genre", genre.Name);
        foreach (var director in movie.Directors) WriteElement(writer, "director", director);
        foreach (var writerName in movie.Writers) WriteElement(writer, "credits", writerName);
        foreach (var company in movie.ProductionCompanies) WriteElement(writer, "studio", company.Name);
        foreach (var country in movie.ProductionCountries) WriteElement(writer, "country", GetRussianCountryName(country.IsoCode, country.Name));
        if (!string.IsNullOrWhiteSpace(movie.ImdbId))
        {
            WriteElement(writer, "imdbid", movie.ImdbId);
            writer.WriteStartElement("uniqueid"); writer.WriteAttributeString("type", "imdb"); writer.WriteAttributeString("default", "false"); writer.WriteString(movie.ImdbId); writer.WriteEndElement();
        }
        writer.WriteStartElement("uniqueid"); writer.WriteAttributeString("type", "tmdb"); writer.WriteAttributeString("default", "true"); writer.WriteString(movie.TmdbId.ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement();
        writer.WriteStartElement("ratings");
        writer.WriteStartElement("rating"); writer.WriteAttributeString("name", "themoviedb"); writer.WriteAttributeString("max", "10"); writer.WriteAttributeString("default", "true");
        WriteElement(writer, "value", movie.Rating.ToString("0.0", CultureInfo.InvariantCulture));
        WriteElement(writer, "votes", movie.VoteCount.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement(); writer.WriteEndElement();
        if (technicalInfo != null)
        {
            writer.WriteStartElement("fileinfo");
            writer.WriteStartElement("streamdetails");
            if (technicalInfo.Video != null)
            {
                var video = technicalInfo.Video;
                writer.WriteStartElement("video");
                WriteElement(writer, "codec", NormalizeVideoCodec(video.Codec));
                if (video.Width > 0) WriteElement(writer, "width", video.Width.ToString(CultureInfo.InvariantCulture));
                if (video.Height > 0) WriteElement(writer, "height", video.Height.ToString(CultureInfo.InvariantCulture));
                if (video.Width > 0 && video.Height > 0)
                {
                    // MediaInfo's AspectRatio enum may stringify as "Opaque"; calculate the ratio from dimensions.
                    var aspect = (video.Width / (double)video.Height).ToString("0.000", CultureInfo.InvariantCulture);
                    WriteElement(writer, "aspect", aspect);
                    WriteElement(writer, "format", $"{video.Height}p");
                }
                if (technicalInfo.DurationSeconds > 0)
                    WriteElement(writer, "durationinseconds", technicalInfo.DurationSeconds.Value.ToString(CultureInfo.InvariantCulture));
                WriteElement(writer, "bitrate", ConvertValue(video.BitRate));
                WriteElement(writer, "framerate", ConvertValue(video.FrameRate));
                WriteElement(writer, "hdrtype", video.Hdr);
                WriteElement(writer, "profile", video.Profile);
                WriteElement(writer, "scantype", video.ScanType);
                writer.WriteEndElement();
            }
            foreach (var audio in technicalInfo.AudioTracks)
            {
                writer.WriteStartElement("audio");
                WriteElement(writer, "codec", NormalizeAudioCodec(audio.Codec));
                WriteElement(writer, "language", NormalizeLanguageCode(audio.Language));
                WriteElement(writer, "title", audio.Title);
                WriteElement(writer, "channels", NormalizeChannels(audio.Channels));
                WriteElement(writer, "channel", audio.ChannelLayout);
                WriteElement(writer, "bitrate", audio.BitRate);
                WriteElement(writer, "samplingrate", audio.SamplingRate);
                WriteElement(writer, "bitdepth", audio.BitDepth);
                writer.WriteEndElement();
            }
            foreach (var subtitle in technicalInfo.SubtitleTracks)
            {
                writer.WriteStartElement("subtitle");
                WriteElement(writer, "language", NormalizeLanguageCode(subtitle.Language));
                WriteElement(writer, "codec", subtitle.Codec);
                WriteElement(writer, "title", subtitle.Title);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        foreach (var actor in movie.Cast)
        {
            writer.WriteStartElement("actor");
            WriteElement(writer, "name", actor.Name);
            if (!string.IsNullOrWhiteSpace(actor.Character)) WriteElement(writer, "role", actor.Character);
            if (!string.IsNullOrWhiteSpace(actor.ProfilePath)) WriteElement(writer, "thumb", "https://image.tmdb.org/t/p/original" + actor.ProfilePath);
            WriteElement(writer, "order", actor.Order.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }
        writer.WriteStartElement("thumb"); writer.WriteAttributeString("aspect", "poster"); writer.WriteString("poster.jpg"); writer.WriteEndElement();
        writer.WriteStartElement("fanart"); writer.WriteStartElement("thumb"); writer.WriteString("fanart.jpg"); writer.WriteEndElement(); writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static long GetFileSizeSafe(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static MediaTechnicalInfo? AnalyzeMediaFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            // MediaInfoWrapper 26.1.0 is not IDisposable; do not use a using statement.
            var media = new MediaInfoWrapper(path);
            if (!media.Success) return null;

            var videoStream = media.VideoStreams.FirstOrDefault();
            var durationMs = media.Duration;
            var durationSeconds = durationMs > 0 ? (long?)Math.Round(durationMs / 1000.0) : null;
            var video = videoStream == null ? null : new MediaVideoInfo(
                Width: videoStream.Width,
                Height: videoStream.Height,
                Codec: ConvertValue(videoStream.Codec),
                AspectRatio: ConvertValue(videoStream.AspectRatio),
                BitRate: ReadStringProperty(videoStream, "BitRate") ?? ReadStringProperty(videoStream, "Bitrate"),
                FrameRate: videoStream.FrameRate,
                Hdr: ConvertValue(videoStream.Hdr),
                Profile: ReadStringProperty(videoStream, "Profile"),
                ScanType: ReadStringProperty(videoStream, "ScanType"));

            var audio = media.AudioStreams.Select(a => new MediaAudioInfo(
                Codec: ConvertValue(a.Codec),
                Language: a.Language,
                Title: ReadStringProperty(a, "Name") ?? ReadStringProperty(a, "Title"),
                Channels: ReadStringProperty(a, "AudioChannelsFriendly") ?? ConvertValue(ReadProperty(a, "Channel")) ?? ConvertValue(ReadProperty(a, "Channels")),
                ChannelLayout: ReadStringProperty(a, "ChannelLayout"),
                BitRate: ConvertValue(ReadProperty(a, "Bitrate")) ?? ConvertValue(ReadProperty(a, "BitRate")),
                SamplingRate: ConvertValue(a.SamplingRate),
                BitDepth: ConvertValue(a.BitDepth))).ToList();
            var subtitles = media.Subtitles.Select(sub => new MediaSubtitleInfo(
                Language: sub.Language,
                Codec: ConvertValue(sub.Codec),
                Title: ReadStringProperty(sub, "Name") ?? ReadStringProperty(sub, "Title"))).ToList();

            if (video == null && audio.Count == 0 && subtitles.Count == 0 && durationSeconds == null) return null;
            return new MediaTechnicalInfo(durationSeconds, video, audio, subtitles);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MediaInfo analysis failed for '{path}': {ex.Message}");
            return null;
        }
    }

    private static object? ReadProperty(object source, string propertyName)
    {
        try { return source.GetType().GetProperty(propertyName)?.GetValue(source); }
        catch { return null; }
    }

    private static string? ConvertValue(object? value)
    {
        if (value == null) return null;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) || text == "0" ? null : text;
    }

    private static string? ReadStringProperty(object source, string propertyName)
    {
        return ConvertValue(ReadProperty(source, propertyName));
    }

    private sealed record MediaTechnicalInfo(long? DurationSeconds, MediaVideoInfo? Video,
        List<MediaAudioInfo> AudioTracks, List<MediaSubtitleInfo> SubtitleTracks);
    private sealed record MediaVideoInfo(int Width, int Height, string? Codec, string? AspectRatio,
        object? BitRate, object? FrameRate, string? Hdr, string? Profile, string? ScanType);
    private sealed record MediaAudioInfo(string? Codec, string? Language, string? Title,
        string? Channels, string? ChannelLayout, string? BitRate, string? SamplingRate, string? BitDepth);
    private sealed record MediaSubtitleInfo(string? Language, string? Codec, string? Title);

    private static string? NormalizeVideoCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return codec;
        var value = codec.Trim();
        var upper = value.ToUpperInvariant();
        if (upper.Contains("HEVC") || upper.Contains("MPEGHISO")) return "hevc";
        if (upper.Contains("AVC") || upper.Contains("H264")) return "h264";
        if (upper.Contains("AV1")) return "av1";
        if (upper.Contains("MPEG4")) return "mpeg4";
        if (upper.Contains("MPEG2")) return "mpeg2video";
        if (upper.Contains("VC1") || upper.Contains("VC-1")) return "vc1";
        if (upper.Contains("VP9")) return "vp9";
        if (upper.Contains("VP8")) return "vp8";
        return value.ToLowerInvariant();
    }

    private static string? NormalizeAudioCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return codec;
        var upper = codec.Trim().ToUpperInvariant();
        if (upper.Contains("AC-3") || upper == "AC3" || upper.Contains("A_AC3")) return "ac3";
        if (upper.Contains("E-AC-3") || upper.Contains("EAC3")) return "eac3";
        if (upper.Contains("DTS-HD MA") || upper.Contains("DTSHDMA")) return "dts-hd ma";
        if (upper.Contains("DTS")) return "dts";
        if (upper.Contains("TRUEHD") || upper.Contains("TRUE HD")) return "truehd";
        if (upper.Contains("AAC")) return "aac";
        if (upper.Contains("FLAC")) return "flac";
        if (upper.Contains("OPUS")) return "opus";
        if (upper.Contains("VORBIS")) return "vorbis";
        if (upper.Contains("PCM")) return "pcm";
        return codec.Trim().ToLowerInvariant();
    }

    private static string? NormalizeChannels(string? channels)
    {
        if (string.IsNullOrWhiteSpace(channels)) return channels;
        var value = channels.Trim();
        value = System.Text.RegularExpressions.Regex.Replace(value, @"\s*(кан\.?|каналов?|channels?)\s*$", string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return value.Trim();
    }

    private static string? NormalizeLanguageCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return language;
        var value = language.Trim().ToLowerInvariant();
        var code = value switch
        {
            "ru" or "rus" or "russian" or "русский" or "рус" => "rus",
            "en" or "eng" or "english" or "английский" or "англ" => "eng",
            "uk" or "ukr" or "ukrainian" or "украинский" => "ukr",
            "de" or "deu" or "ger" or "german" or "немецкий" => "deu",
            "fr" or "fra" or "fre" or "french" or "французский" => "fra",
            "es" or "spa" or "spanish" or "испанский" => "spa",
            "it" or "ita" or "italian" or "итальянский" => "ita",
            "ja" or "jpn" or "japanese" or "японский" => "jpn",
            "zh" or "zho" or "chi" or "chinese" or "китайский" => "zho",
            "ko" or "kor" or "korean" or "корейский" => "kor",
            "pt" or "por" or "portuguese" or "португальский" => "por",
            "pl" or "pol" or "polish" or "польский" => "pol",
            "und" or "undefined" or "unknown" => "und",
            _ => value.Length >= 3 ? value[..3] : value
        };
        return code;
    }

    private static string GetRussianCountryName(string? isoCode, string fallback)
    {
        var code = (isoCode ?? string.Empty).ToUpperInvariant();
        return code switch
        {
            "RU" => "Россия", "US" => "США", "GB" => "Великобритания", "UK" => "Великобритания",
            "FR" => "Франция", "DE" => "Германия", "IT" => "Италия", "ES" => "Испания",
            "JP" => "Япония", "CN" => "Китай", "KR" => "Южная Корея", "IN" => "Индия",
            "CA" => "Канада", "AU" => "Австралия", "NZ" => "Новая Зеландия", "BR" => "Бразилия",
            "MX" => "Мексика", "AR" => "Аргентина", "PL" => "Польша", "UA" => "Украина",
            "TR" => "Турция", "NL" => "Нидерланды", "BE" => "Бельгия", "CH" => "Швейцария",
            "AT" => "Австрия", "SE" => "Швеция", "NO" => "Норвегия", "DK" => "Дания",
            "FI" => "Финляндия", "IE" => "Ирландия", "CZ" => "Чехия", "HU" => "Венгрия",
            "RO" => "Румыния", "GR" => "Греция", "PT" => "Португалия", "ZA" => "ЮАР",
            "HK" => "Гонконг", "TW" => "Тайвань", "TH" => "Таиланд", "ID" => "Индонезия",
            "IL" => "Израиль", "IR" => "Иран", "AE" => "ОАЭ", "SA" => "Саудовская Аравия",
            _ => string.IsNullOrWhiteSpace(fallback) ? code : fallback
        };
    }

    private static void WriteElement(XmlWriter writer, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        writer.WriteElementString(name, value);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
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
    public string? SelectedPosterPath { get; set; }
    public string? SelectedBackdropPath { get; set; }
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
