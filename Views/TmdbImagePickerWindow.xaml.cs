using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CineLibraryCS.Views;

public sealed partial class TmdbImagePickerWindow : Window
{
    private readonly TaskCompletionSource<string?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _ownerHandle;
    private bool _ownerDisabled;
    private bool _shown;
    private AppWindow? _appWindow;
    private string _sizeKey = "poster";
    private bool _savingSize;
    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "TmdbImagePickerWindow.settings.json");
    public string? SelectedPath { get; private set; }

    public TmdbImagePickerWindow(IEnumerable<TmdbImageChoice> choices, bool isPoster, Window ownerWindow)
    {
        InitializeComponent();
        HeaderText.Text = isPoster ? "Выберите постер" : "Выберите фоновое изображение";
        ImagesGrid.ItemsSource = choices.ToList();
        ConfigureWindow(isPoster, ownerWindow);
        Closed += (_, _) =>
        {
            if (_ownerDisabled && _ownerHandle != 0) { EnableWindow(_ownerHandle, true); _ownerDisabled = false; }
            if (!_completion.Task.IsCompleted) _completion.TrySetResult(null);
        };
    }

    private void ConfigureWindow(bool isPoster, Window ownerWindow)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow = appWindow;
        _sizeKey = isPoster ? "poster" : "backdrop";
        try { var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"); if (File.Exists(icon)) appWindow.SetIcon(icon); } catch { }
        var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(ownerWindow);
        var owner = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(ownerHwnd));
        _ownerHandle = ownerHwnd;
        var titleBar = appWindow.TitleBar;
        var bg = Windows.UI.Color.FromArgb(255, 18, 18, 20);
        titleBar.BackgroundColor = bg; titleBar.InactiveBackgroundColor = bg; titleBar.ButtonBackgroundColor = bg; titleBar.ButtonInactiveBackgroundColor = bg;
        titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 235, 235, 238); titleBar.ButtonForegroundColor = titleBar.ForegroundColor;
        var defaultSize = new SizeInt32(isPoster ? 735 : 1120, 760);
        var savedSize = LoadSavedSize(_sizeKey);
        appWindow.Resize(savedSize ?? defaultSize);
        appWindow.Changed += OnAppWindowChanged;
        appWindow.Move(new PointInt32(owner.Position.X + Math.Max(0, (owner.Size.Width - appWindow.Size.Width) / 2), owner.Position.Y + Math.Max(0, (owner.Size.Height - appWindow.Size.Height) / 2)));
        if (appWindow.Presenter is OverlappedPresenter presenter) { presenter.IsResizable = true; presenter.IsMaximizable = true; }
    }

    private static SizeInt32? LoadSavedSize(string key)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var settings = JsonSerializer.Deserialize<Dictionary<string, SavedWindowSize>>(File.ReadAllText(SettingsPath));
            if (settings != null && settings.TryGetValue(key, out var size) && size.Width >= 500 && size.Height >= 400)
                return new SizeInt32(size.Width, size.Height);
        }
        catch { }
        return null;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _savingSize) return;
        _savingSize = true;
        try
        {
            Dictionary<string, SavedWindowSize> settings;
            try { settings = File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Dictionary<string, SavedWindowSize>>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
            catch { settings = new(); }
            settings[_sizeKey] = new SavedWindowSize { Width = sender.Size.Width, Height = sender.Size.Height };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Settings may be read-only when the app is installed under Program Files. */ }
        finally { _savingSize = false; }
    }

    private sealed class SavedWindowSize
    {
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public Task<string?> ShowAsync()
    {
        if (_shown) return _completion.Task;
        _shown = true;
        if (_ownerHandle != 0) { EnableWindow(_ownerHandle, false); _ownerDisabled = true; }
        Activate();
        return _completion.Task;
    }
    private void OnImageItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TmdbImageChoice choice) SelectImage(choice);
    }

    private void OnImageTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is TmdbImageChoice choice)
        {
            SelectImage(choice);
            e.Handled = true;
        }
    }

    private void SelectImage(TmdbImageChoice choice)
    {
        ImagesGrid.SelectedItem = choice;
        // Make the first click visually apparent even though the card template is custom.
        ImagesGrid.UpdateLayout();
        if (ImagesGrid.ContainerFromItem(choice) is GridViewItem item)
            item.IsSelected = true;
    }

    private void OnChooseClick(object sender, RoutedEventArgs e)
    {
        if (ImagesGrid.SelectedItem is TmdbImageChoice choice)
        {
            SelectedPath = choice.FilePath;
            _completion.TrySetResult(SelectedPath);
            Close();
        }
    }
    private void OnCancelClick(object sender, RoutedEventArgs e) { _completion.TrySetResult(null); Close(); }
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint hWnd, bool bEnable);
}
