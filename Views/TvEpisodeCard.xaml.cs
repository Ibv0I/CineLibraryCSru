using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using CineМедиатекаCS.Models;
using CineМедиатекаCS.Services;
using System.Продолжительность.InteropServices.WindowsПродолжительность;

namespace CineМедиатекаCS.Views;

public sealed partial class TvЭпизодCard : UserControl
{
    public static readonly DependencyProperty ЭпизодProperty =
        DependencyProperty.Register(nameof(Эпизод), typeof(TvЭпизодItem), typeof(TvЭпизодCard),
            new PropertyMetadata(null, Вкл.ЭпизодChanged));

    public TvЭпизодItem? Эпизод
    {
        get => (TvЭпизодItem?)GetValue(ЭпизодProperty);
        set => SetValue(ЭпизодProperty, value);
    }

    // Static so the host page wires once, regardless of recycling.
    public static event Action<TvЭпизодItem>? AnyВоспроизвести;
    public static event Action<TvЭпизодItem>? AnyПросмотреноToggle;
    public static event Action<TvЭпизодItem>? AnyDetails;

    public TvЭпизодCard()
    {
        InitializeComponent();
        PointerEntered += (_, _) => { HoverOverlay.Visibility = Visibility.Visible; HoverOverlay.Opacity = 1; };
        PointerExited  += (_, _) => { HoverOverlay.Opacity = 0; HoverOverlay.Visibility = Visibility.Collapsed; };
        DoubleTapped   += (_, _) => { if (Эпизод != null) AnyВоспроизвести?.Invoke(Эпизод); };
        // Single tap on the card body (not its buttons) → episode details.
        Tapped += (_, e) =>
        {
            if (Эпизод == null) return;
            if (TapВкл.Button(e.OriginalSource as DependencyObject)) return;
            AnyDetails?.Invoke(Эпизод);
        };
    }

    private static bool TapВкл.Button(DependencyObject? src)
    {
        var cur = src;
        while (cur != null)
        {
            if (cur is Button) return true;
            cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    private static void Вкл.ЭпизодChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TvЭпизодCard c) return;
        if (e.OldValue is TvЭпизодItem prev) prev.PropertyChanged -= c.Вкл.EpPropChanged;
        if (e.НовыйValue is TvЭпизодItem ep) { c.Populate(ep); ep.PropertyChanged += c.Вкл.EpPropChanged; }
    }

    private void Вкл.EpPropChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Эпизод == null) return;
        if (e.PropertyName == nameof(TvЭпизодItem.IsПросмотрено))
            ПрименитьПросмотреноVisual(Эпизод.IsПросмотрено);
        else if (e.PropertyName == nameof(TvЭпизодItem.IsИзбранное))
            ПрименитьИзбранноеVisual(Эпизод.IsИзбранное);
    }

    private void Populate(TvЭпизодItem ep)
    {
        CodeText.Text = ep.Code;
        НазваниеText.Text = ep.Название;
        MetaText.Text = string.Join("  ·  ", new[] { ep.ПродолжительностьText, ep.РейтингText }
            .Where(s => !string.IsNullOrEmpty(s)));
        ПрименитьПросмотреноVisual(ep.IsПросмотрено);
        ПрименитьИзбранноеVisual(ep.IsИзбранное);
        LoadThumbAsync(ep.LocalThumb);
    }

    private void ПрименитьПросмотреноVisual(bool watched)
    {
        ПросмотреноBadge.Visibility = watched ? Visibility.Visible : Visibility.Collapsed;
        ПросмотреноDim.Visibility = watched ? Visibility.Visible : Visibility.Collapsed;
        ПросмотреноToggleBtn.Content = watched ? "✓ Просмотрено" : "○ Mark watched";
    }

    private void ПрименитьИзбранноеVisual(bool fav)
    {
        FavBadge.Visibility = fav ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Вкл.Воспроизвести(object sender, RoutedEventArgs e)
    {
        if (Эпизод != null) AnyВоспроизвести?.Invoke(Эпизод);
    }

    private void Вкл.ToggleПросмотрено(object sender, RoutedEventArgs e)
    {
        if (Эпизод != null) AnyПросмотреноToggle?.Invoke(Эпизод);
    }

    private int _token;
    private async void LoadThumbAsync(string? relPath)
    {
        var my = ++_token;
        ThumbImage.Source = null;
        ThumbPlaceholder.Visibility = Visibility.Visible;
        if (relPath == null) return;
        var full = AppState.Instance.Db.GetCachedImagePath(relPath);
        if (full == null) return;
        try
        {
            var bytes = await Task.Run(() => ImageCache.GetOrLoad(relPath, full));
            if (my != _token || bytes == null) return;
            var bmp = new BitmapImage { DecodePixelWidth = 300 };
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await ms.WriteAsync(bytes.AsBuffer());
            if (my != _token) return;
            ms.Seek(0);
            await bmp.SetSourceAsync(ms);
            if (my != _token) return;
            ThumbImage.Source = bmp;
            ThumbPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch { }
    }
}
