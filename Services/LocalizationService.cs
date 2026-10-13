using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace CineLibraryCS.Services;

/// <summary>
/// Loads localized strings from the language-specific Resources.resw file.
/// The app is unpackaged WinUI 3, so this intentionally avoids
/// Windows.Globalization.ApplicationLanguages and the UWP ResourceLoader,
/// which can fail during startup in unpackaged deployments.
/// </summary>
public static class LocalizationService
{
    private static readonly object Sync = new();
    private static Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private static string _language = "ru-RU";

    public static void ApplyLanguage(string language)
    {
        var normalized = language.Equals("en-US", StringComparison.OrdinalIgnoreCase)
            ? "en-US" : "ru-RU";
        lock (Sync)
        {
            _language = normalized;
            _strings = LoadStrings(normalized);
        }
    }

    public static string Get(string key)
    {
        lock (Sync)
        {
            if (_strings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return value;
            return key;
        }
    }


    // Exact text map for existing XAML literals. Keeping the mapping centralized
    // lets us localize the current UI without restructuring its layout.
    private static readonly Dictionary<string, string> UiLiteralKeys = new(StringComparer.Ordinal)
    {
        ["Show sidebar"] = "ShowSidebar",
        ["What the search matches"] = "SearchScopeTooltip",
        ["All"] = "ScopeAll",
        ["Title"] = "ScopeTitle",
        ["Cast & crew"] = "ScopeCastCrew",
        ["Search title, actor, director…"] = "SearchPlaceholder",
        ["LIBRARY"] = "LibraryGroup",
        ["All Movies"] = "AllMovies",
        ["All TV Shows"] = "AllTvShows",
        ["Favorites"] = "Favorites",
        ["To Watch"] = "ToWatch",
        ["Notes"] = "Notes",
        ["Watched & Gone"] = "WatchedGone",
        ["DISCOVER"] = "DiscoverGroup",
        ["Continue Watching"] = "ContinueWatching",
        ["Recently Added"] = "RecentlyAdded",
        ["Recently Watched"] = "RecentlyWatched",
        ["Surprise me"] = "SurpriseMe",
        ["On This Day"] = "OnThisDay",
        ["BROWSE"] = "BrowseGroup",
        ["By Genre"] = "ByGenre",
        ["By Decade"] = "ByDecade",
        ["By Rating"] = "ByRating",
        ["Collections"] = "Collections",
        ["TOOLS"] = "ToolsGroup",
        ["Statistics"] = "Statistics",
        ["Dupes"] = "Dupes",
        ["Backup"] = "Backup",
        ["Export"] = "Export",
        ["MY LISTS"] = "MyLists",
        ["Create a new list"] = "CreateNewList",
        ["LIBRARIES"] = "Libraries",
        ["Drives"] = "Drives",
        ["Switch theme"] = "SwitchTheme",
        ["Keyboard shortcuts"] = "KeyboardShortcuts",
        ["Collapse sidebar"] = "CollapseSidebar",
        ["Back"] = "Back",
        ["Unwatched"] = "FilterUnwatched",
        ["Watched"] = "FilterWatched",
        ["Title ↑"] = "SortTitleAsc",
        ["Title ↓"] = "SortTitleDesc",
        ["Year ↓"] = "SortYearDesc",
        ["Year ↑"] = "SortYearAsc",
        ["Rating ↓"] = "SortRatingDesc",
        ["Rating ↑"] = "SortRatingAsc",
        ["Runtime ↓"] = "SortRuntimeDesc",
        ["Runtime ↑"] = "SortRuntimeAsc",
        ["Date Added ↓"] = "SortDateAddedDesc",
        ["Date Added ↑"] = "SortDateAddedAsc",
        ["Last Watched ↓"] = "SortLastWatchedDesc",
        ["Last Watched ↑"] = "SortLastWatchedAsc",
        ["Poster size"] = "PosterSize",
        ["Small"] = "SizeSmall",
        ["Medium"] = "SizeMedium",
        ["Large"] = "SizeLarge",
        ["Extra large"] = "SizeExtraLarge",
        ["Grid view"] = "GridView",
        ["List view"] = "ListView",
        ["No movies match your filters"] = "NoMoviesMatch",
        ["Try clearing the search or choosing 'All' watched filter."] = "ClearSearchHint",
        ["TV SHOWS MATCHING"] = "TvShowsMatching",
        ["TV SHOWS IN THIS LIST"] = "TvShowsInList",
        ["Send to Watched and Gone"] = "SendToWatchedGone",
        ["Keep as a record, remove from library"] = "KeepRecordRemoveLibrary",
        ["Clear selection (Esc)"] = "ClearSelection",
        ["Surprise me — random pick from current view"] = "SurpriseRandomTooltip",
    };

    public static string TranslateText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!UiLiteralKeys.TryGetValue(text, out var key)) return text;
        return Get(key);
    }

    public static void TranslateTree(DependencyObject root)
    {
        if (root is null) return;
        try
        {
            if (root is TextBlock textBlock)
                textBlock.Text = TranslateText(textBlock.Text);
            if (root is ContentControl contentControl && contentControl.Content is string content)
                contentControl.Content = TranslateText(content);
            if (root is TextBox textBox)
                textBox.PlaceholderText = TranslateText(textBox.PlaceholderText);
            if (root is ComboBox comboBox)
                comboBox.PlaceholderText = TranslateText(comboBox.PlaceholderText);
            if (root is MenuFlyoutItem menuItem)
                menuItem.Text = TranslateText(menuItem.Text);

            var tooltip = ToolTipService.GetToolTip(root);
            if (tooltip is string tip)
                ToolTipService.SetToolTip(root, TranslateText(tip));
            var automationName = AutomationProperties.GetName(root);
            if (!string.IsNullOrEmpty(automationName))
                AutomationProperties.SetName(root, TranslateText(automationName));

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                TranslateTree(VisualTreeHelper.GetChild(root, i));
        }
        catch
        {
            // Translation must never break rendering of a page.
        }
    }

    private static Dictionary<string, string> LoadStrings(string language)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Strings", language, "Resources.resw");
            if (!File.Exists(path) && language != "en-US")
                path = Path.Combine(AppContext.BaseDirectory, "Strings", "ru-RU", "Resources.resw");
            if (!File.Exists(path)) return result;

            var doc = XDocument.Load(path);
            foreach (var entry in doc.Root?.Elements("data") ?? Enumerable.Empty<XElement>())
            {
                var key = (string?)entry.Attribute("name");
                var value = (string?)entry.Element("value");
                if (!string.IsNullOrWhiteSpace(key) && value is not null)
                    result[key] = value;
            }
        }
        catch
        {
            // Localization must never prevent the app from starting.
        }
        return result;
    }
}
