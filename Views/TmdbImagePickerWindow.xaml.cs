using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using System.Runtime.InteropServices;

namespace CineLibraryCS.Views;

public sealed partial class TmdbImagePickerWindow : Window
{
    private readonly TaskCompletionSource<string?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _ownerHandle;
    private bool _ownerDisabled;
    private bool _shown;
    public string? SelectedPath { get; private set; }

    public TmdbImagePickerWindow(IEnumerable<TmdbImageChoice> choices, bool isPoster, Window ownerWindow)
    {
        InitializeComponent();
        HeaderText.Text = isPoster ? "Выберите постер" : "Выберите фоновое изображение";
        ImagesGrid.ItemsSource = choices.ToList();
        if (ImagesGrid.Items.Count > 0) ImagesGrid.SelectedIndex = 0;
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
        try { var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"); if (File.Exists(icon)) appWindow.SetIcon(icon); } catch { }
        var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(ownerWindow);
        var owner = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(ownerHwnd));
        _ownerHandle = ownerHwnd;
        var titleBar = appWindow.TitleBar;
        var bg = Windows.UI.Color.FromArgb(255, 18, 18, 20);
        titleBar.BackgroundColor = bg; titleBar.InactiveBackgroundColor = bg; titleBar.ButtonBackgroundColor = bg; titleBar.ButtonInactiveBackgroundColor = bg;
        titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 235, 235, 238); titleBar.ButtonForegroundColor = titleBar.ForegroundColor;
        appWindow.Resize(new SizeInt32(isPoster ? 735 : 1120, 760));
        appWindow.Move(new PointInt32(owner.Position.X + Math.Max(0, (owner.Size.Width - appWindow.Size.Width) / 2), owner.Position.Y + Math.Max(0, (owner.Size.Height - appWindow.Size.Height) / 2)));
        if (appWindow.Presenter is OverlappedPresenter presenter) { presenter.IsResizable = true; presenter.IsMaximizable = true; }
    }

    public Task<string?> ShowAsync()
    {
        if (_shown) return _completion.Task;
        _shown = true;
        if (_ownerHandle != 0) { EnableWindow(_ownerHandle, false); _ownerDisabled = true; }
        Activate();
        return _completion.Task;
    }
    private void OnImageClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TmdbImageChoice choice) { SelectedPath = choice.FilePath; _completion.TrySetResult(SelectedPath); Close(); }
    }
    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (ImagesGrid.SelectedItem is TmdbImageChoice choice) SelectedPath = choice.FilePath;
        _completion.TrySetResult(SelectedPath); Close();
    }
    private void OnCancelClick(object sender, RoutedEventArgs e) { _completion.TrySetResult(null); Close(); }
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint hWnd, bool bEnable);
}
