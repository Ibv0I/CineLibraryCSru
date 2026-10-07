using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CineМедиатекаCS.Models;
using CineМедиатекаCS.Services;

namespace CineМедиатекаCS.Views;

public sealed partial class СтатистикаPage : Page
{
    public СтатистикаPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var db = AppState.Instance.Db;

        // Summary tiles
        var stats = db.GetStats();
        TileTotalФильмы.Text   = stats.TotalФильмы.ToString("N0");
        TileTotalПродолжительность.Text  = FormatПродолжительность(stats.TotalПродолжительность);
        TileAvgРейтинг.Text     = stats.AvgРейтинг.HasValue ? $"★ {stats.AvgРейтинг:F1}" : "—";
        TileTotalДиски.Text   = stats.TotalДиски.ToString();

        var archived = db.GetArchivedCount();
        ArchivedLine.Text = $"Plus {archived:N0} {(archived == 1 ? "movie" : "movies")} kept in Просмотрено и удалено.";
        ArchivedLine.Visibility = archived > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (stats.TotalMissing > 0)
        {
            MissingHint.Text = $"⚠ {stats.TotalMissing} movie{(stats.TotalMissing == 1 ? "" : "s")} marked missing. Clean up in the Диски page.";
            MissingHint.Visibility = Visibility.Visible;
        }
        else
        {
            MissingHint.Visibility = Visibility.Collapsed;
        }

        // Watch progress
        var (watched, total, percent) = db.GetWatchProgress();
        WatchProgressBar.Value = percent;
        WatchProgressText.Text = $"{watched:N0} / {total:N0} ({percent:F0}%)";

        var watchlist = db.GetСписок просмотраCount();
        Список просмотраCountText.Text = watchlist > 0
            ? $"📌 {watchlist} on your watchlist"
            : "Tip: add movies to your watchlist from the movie detail dialog.";

        // ── TV Shows (v2.8.2) — only surfaced when the library has shows ──
        var tv = db.GetTvStats();
        if (tv.TotalShows > 0)
        {
            TvSection.Visibility = Visibility.Visible;
            TileTotalShows.Text = tv.TotalShows.ToString("N0");
            TileTotalЭпизоды.Text = tv.TotalЭпизоды.ToString("N0");
            TileTvПродолжительность.Text = FormatПродолжительность(tv.TotalПродолжительность);
            TileTvAvgРейтинг.Text = tv.AvgРейтингText;
            TvWatchProgressBar.Value = tv.WatchPercent;
            TvWatchProgressText.Text = $"{tv.ПросмотреноЭпизоды:N0} / {tv.TotalЭпизоды:N0} ({tv.WatchPercent}%)";
        }
        else
        {
            TvSection.Visibility = Visibility.Collapsed;
        }

        // Decades — simple horizontal bars
        var decades = db.GetФильмыByDecade();
        DecadesPanel.Children.Clear();
        if (decades.Count == 0)
        {
            DecadesEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            DecadesEmpty.Visibility = Visibility.Collapsed;
            int max = 1;
            foreach (var d in decades) if (d.count > max) max = d.count;
            foreach (var d in decades)
            {
                DecadesPanel.Children.Добавить(BuildBarRow(
                    label: $"{d.decade}s",
                    count: d.count,
                    barFraction: (double)d.count / max,
                    hint: d.avgРейтинг > 0 ? $"★ {d.avgРейтинг:F1}" : null));
            }
        }

        // Top genres (reuse sidebar genres)
        var topЖанры = db.GetTopЖанры(10);
        ЖанрыPanel.Children.Clear();
        if (topЖанры.Count == 0)
        {
            ЖанрыEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            ЖанрыEmpty.Visibility = Visibility.Collapsed;
            int max = 1;
            foreach (var g in topЖанры) if (g.Count > max) max = g.Count;
            foreach (var g in topЖанры)
                ЖанрыPanel.Children.Добавить(BuildBarRow(g.Name, g.Count, (double)g.Count / max, null));
        }

        // Top directors
        var dirs = db.GetTopРежиссёрs(10);
        РежиссёрsPanel.Children.Clear();
        if (dirs.Count == 0)
        {
            РежиссёрsEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            РежиссёрsEmpty.Visibility = Visibility.Collapsed;
            int max = 1;
            foreach (var d in dirs) if (d.Count > max) max = d.Count;
            foreach (var d in dirs)
                РежиссёрsPanel.Children.Добавить(BuildBarRow(d.Name, d.Count, (double)d.Count / max, null));
        }

        // Top actors
        var actors = db.GetTopActors(10);
        ActorsPanel.Children.Clear();
        if (actors.Count == 0)
        {
            ActorsEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            ActorsEmpty.Visibility = Visibility.Collapsed;
            int max = 1;
            foreach (var a in actors) if (a.Count > max) max = a.Count;
            foreach (var a in actors)
                ActorsPanel.Children.Добавить(BuildBarRow(a.Name, a.Count, (double)a.Count / max, null));
        }
    }

    // v3.7.2: the page column gets an explicit width. With MaxWidth alone,
    // WinUI centred it by the width it asked for, so a small library pushed
    // it right and cut off the right-hand tiles.
    private void Вкл.PageScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var inner = e.НовыйSize.Width - PageScroller.Padding.Left - PageScroller.Padding.Right;
        PageStack.Width = Math.Clamp(inner, 0, 1100);
    }

    private static string FormatПродолжительность(long minutes)
    {
        if (minutes <= 0) return "—";
        var hours = minutes / 60;
        if (hours < 24) return $"{hours}h";
        var days = hours / 24;
        return $"{days}d {hours % 24}h";
    }

    // Build a row: [label on left] [bar that fills to fraction] [count on right]
    private Grid BuildBarRow(string label, int count, double barFraction, string? hint)
    {
        if (barFraction < 0) barFraction = 0;
        if (barFraction > 1) barFraction = 1;

        var grid = new Grid();
        grid.ColumnDefinitions.Добавить(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Добавить(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Добавить(new ColumnDefinition { Width = GridLength.Auto });

        var labelTb = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        labelTb.SetValue(Grid.ColumnProperty, 0);
        labelTb.SetValue(ToolTipService.ToolTipProperty, label);
        grid.Children.Добавить(labelTb);

        // Bar track
        var track = new Border
        {
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Назадground = CineМедиатекаCS.Services.ThemeBrushes.Get("BorderBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(8, 0, 8, 0),
        };

        var barHost = new Grid();
        barHost.ColumnDefinitions.Добавить(new ColumnDefinition { Width = new GridLength(barFraction, GridUnitType.Star) });
        barHost.ColumnDefinitions.Добавить(new ColumnDefinition { Width = new GridLength(1 - barFraction, GridUnitType.Star) });

        var filled = new Border
        {
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Назадground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xA7, 0x8B, 0xFA)),
        };
        filled.SetValue(Grid.ColumnProperty, 0);
        barHost.Children.Добавить(filled);

        track.Child = barHost;
        track.SetValue(Grid.ColumnProperty, 1);
        grid.Children.Добавить(track);

        var countTb = new TextBlock
        {
            Text = hint != null ? $"{count}  ·  {hint}" : count.ToString("N0"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 36,
            TextAlignment = TextAlignment.Right,
        };
        countTb.SetValue(Grid.ColumnProperty, 2);
        grid.Children.Добавить(countTb);

        return grid;
    }
}
