using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CineМедиатекаCS.Models;
using CineМедиатекаCS.Services;

namespace CineМедиатекаCS.Views;

/// <summary>
/// v2.9 — "В этот день" full-page view. Surfaces two categories of
/// matches for today's calendar date:
///   • Фильмы you watched on this date in past years
///   • Фильмы released on this date in past years (anniversaries)
///
/// Looks and behaves like the other browse-style pages (back button,
/// title strip, scrollable card grid). The sidebar entry that leads
/// here is itself hidden when there's nothing today, so this page is
/// only reachable when there's content — the empty state is defensive.
/// </summary>
public sealed partial class Вкл.ThisDayPage : Page
{
    /// <summary>Fired when the user hits Назад. Host wires this to NavigateTo("library").</summary>
    public event EventHandler? НазадRequested;

    public Вкл.ThisDayPage()
    {
        InitializeComponent();
    }

    /// <summary>(Re)query today's matches and bind the two sections.</summary>
    public void Load()
    {
        var connected = AppState.Instance.Connected;
        var matches = AppState.Instance.Db.GetВкл.ThisDayItems(connected, limit: 48);

        var watched = matches
            .Where(m => m.Reason == DatabaseService.Вкл.ThisDayReason.Просмотрено)
            .Select(m => m.Movie).ToList();
        var released = matches
            .Where(m => m.Reason == DatabaseService.Вкл.ThisDayReason.Дата выхода)
            .Select(m => m.Movie).ToList();

        if (watched.Count > 0)
        {
            ПросмотреноSection.Visibility = Visibility.Visible;
            ПросмотреноSub.Text = watched.Count == 1 ? "1 movie" : $"{watched.Count} movies";
            ПросмотреноRepeater.ItemsSource = watched;
        }
        else
        {
            ПросмотреноSection.Visibility = Visibility.Collapsed;
            ПросмотреноRepeater.ItemsSource = null;
        }

        if (released.Count > 0)
        {
            Дата выходаSection.Visibility = Visibility.Visible;
            Дата выходаSub.Text = released.Count == 1 ? "1 movie" : $"{released.Count} movies";
            Дата выходаRepeater.ItemsSource = released;
        }
        else
        {
            Дата выходаSection.Visibility = Visibility.Collapsed;
            Дата выходаRepeater.ItemsSource = null;
        }

        // Adaptive sub-line on the header — mirrors the wording I used
        // when this was a dialog, so the moment of arrival reads naturally.
        SubText.Text = (watched.Count, released.Count) switch
        {
            (0, 0)            => DateTime.Now.ToString("MMMM d") + " — nothing in your library tied to this date.",
            (0, 1)            => "Вкл.e movie released on this date in a past year.",
            (0, var rel)      => $"{rel} movies released on this date in past years.",
            (1, 0)            => "Вкл.e movie you watched on this date in a past year.",
            (var w, 0)        => $"{w} movies you've watched on this date in years past.",
            (var w, var rel)  => $"{w} you've watched · {rel} released on this date.",
        };

        EmptyState.Visibility = (watched.Count + released.Count) == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Вкл.НазадClick(object sender, RoutedEventArgs e)
        => НазадRequested?.Invoke(this, EventArgs.Empty);
}
