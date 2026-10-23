using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Globalization;
using System.Xml;

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
    public bool Saved { get; private set; }

    public NfoEditorDialog(string nfoPath, Window ownerWindow)
    {
        _path = nfoPath;
        _ownerWindow = ownerWindow;
        InitializeComponent();
        RootGrid.Loaded += (_, _) => CineLibraryCS.Services.LocalizationService.TranslateTree(this);
        Closed += OnWindowClosed;
        ConfigureWindow();
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
        YearBox.Text = Get("year"); PremieredBox.Text = Get("premiered");
        RuntimeBox.Text = Get("runtime"); CertificationBox.Text = Get("mpaa") ?? Get("certification");
        StudiosBox.Text = string.Join(Environment.NewLine, Elements("studio").Select(x => x.InnerText));
        CountriesBox.Text = string.Join(Environment.NewLine, Elements("country").Select(x => x.InnerText));
        GenresBox.Text = string.Join(Environment.NewLine, Elements("genre").Select(x => x.InnerText));
        RatingsBox.Text = string.Join(Environment.NewLine, Elements("ratings/rating").Select(x =>
            $"{x.GetAttribute("name")} | {Child(x, "value") ?? x.InnerText} | {Child(x, "votes")}"));
        IdentifiersBox.Text = string.Join(Environment.NewLine, Elements("uniqueid").Select(x => $"{x.GetAttribute("type")} | {x.InnerText}"));

        var video = FindStream("video");
        VideoCodecBox.Text = Child(video, "codec") ?? "";
        ContainerBox.Text = Child(video, "container") ?? Get("container") ?? "";
        VideoBitrateBox.Text = Child(video, "bitrate") ?? "";
        WidthBox.Text = Child(video, "width") ?? ""; HeightBox.Text = Child(video, "height") ?? "";
        FrameRateBox.Text = Child(video, "framerate") ?? ""; HdrBox.Text = Child(video, "hdrtype") ?? "";
        AspectBox.Text = Child(video, "aspect") ?? "";
        AudioTracksBox.Text = string.Join(Environment.NewLine, StreamElements("audio").Select(x => string.Join(" | ", new[] {
            Child(x,"codec"), Child(x,"language"), Child(x,"channels"), Child(x,"bitrate"), Child(x,"samplerate") ?? Child(x,"samplingrate"), Child(x,"title")
        }.Select(v => v ?? ""))));
        SubtitlesBox.Text = string.Join(Environment.NewLine, StreamElements("subtitle").Select(x => string.Join(" | ", new[] {
            Child(x,"language"), Child(x,"codec"), Child(x,"title"), Child(x,"default"), Child(x,"forced")
        }.Select(v => v ?? ""))));
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
            Set("plot", PlotBox.Text); Set("year", YearBox.Text); Set("premiered", PremieredBox.Text);
            Set("runtime", RuntimeBox.Text); Set("mpaa", CertificationBox.Text);
            SetList("studio", StudiosBox.Text); SetList("country", CountriesBox.Text); SetList("genre", GenresBox.Text);
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
        var lines = Lines(RatingsBox.Text).Select(ParseLine).Where(x => x.Length >= 2 && x[0].Length > 0).ToList();
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
        var items = Lines(IdentifiersBox.Text).Select(ParseLine).Where(x => x.Length >= 2 && x[0].Length > 0 && x[1].Length > 0).ToList();
        foreach (var old in Elements("uniqueid")) _root.RemoveChild(old);
        foreach (var p in items) { var id = _doc.CreateElement("uniqueid"); id.SetAttribute("type", p[0]); id.InnerText = p[1]; _root.AppendChild(id); }
        var tmdb = items.FirstOrDefault(x => x[0].Equals("tmdb", StringComparison.OrdinalIgnoreCase));
        var imdb = items.FirstOrDefault(x => x[0].Equals("imdb", StringComparison.OrdinalIgnoreCase));
        if (tmdb != null) Set("tmdbid", tmdb[1]);
        if (imdb != null) Set("imdbid", imdb[1]);
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
        SetChild(video, "codec", VideoCodecBox.Text); SetChild(video, "container", ContainerBox.Text);
        SetChild(video, "bitrate", VideoBitrateBox.Text); SetChild(video, "width", WidthBox.Text);
        SetChild(video, "height", HeightBox.Text); SetChild(video, "framerate", FrameRateBox.Text);
        SetChild(video, "hdrtype", HdrBox.Text); SetChild(video, "aspect", AspectBox.Text);
        Set("container", ContainerBox.Text);
        ReplaceTracks(parent, "audio", AudioTracksBox.Text, new[] { "codec", "language", "channels", "bitrate", "samplerate", "title" });
        ReplaceTracks(parent, "subtitle", SubtitlesBox.Text, new[] { "language", "codec", "title", "default", "forced" });
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
