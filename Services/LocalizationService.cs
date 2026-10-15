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
        ["Cast & crew"] = "ScopeCastCrewLabel",
        ["Search title, actor, director…"] = "SearchPlaceholder",
        ["LIBRARY"] = "LibraryGroup",
        ["All Movies"] = "AllMovies",
        ["All TV Shows"] = "AllTvShows",
        ["Favorites"] = "Favorites",
        ["To Watch"] = "ToWatch",
        ["Notes"] = "Notes",
        ["Watched & Gone"] = "WatchedGoneLabel",
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
        ["Movie details"] = "MovieDetails",
        ["Play"] = "Play",
        ["Open folder"] = "OpenFolder",
        ["Genres"] = "Genres",
        ["Cast"] = "Cast",
        ["Director"] = "Director",
        ["Writer"] = "Writer",
        ["Country"] = "Country",
        ["Released"] = "Released",
        ["File size"] = "FileSize",
        ["Resolution"] = "Resolution",
        ["Video codec"] = "VideoCodec",
        ["Audio"] = "Audio",
        ["Subtitles"] = "Subtitles",
        ["Duration"] = "Duration",
        ["YOUR NOTE"] = "YourNote",
        ["Anything you want to remember about this episode…"] = "EpisodeNotePlaceholder",
        ["♡ Favorite"] = "Favorite",
        ["♥ Favorited"] = "Favorited",
        ["▶ Play"] = "PlayWithIcon",
        ["Mark watched"] = "MarkWatched",
        ["Mark unwatched"] = "MarkUnwatched",
        ["Can't play yet"] = "CantPlayYet",
        ["OK"] = "OK",
        ["TV Shows"] = "TvShows",
        ["Seasons"] = "Seasons",
        ["Episodes"] = "Episodes",
        ["No TV shows yet"] = "NoTvShowsYet",
        ["No episodes yet"] = "NoEpisodesYet",
        ["All shows"] = "AllShows",
        ["Continue watching"] = "ContinueWatchingLower",
        ["Add tag"] = "AddTag",
        ["Remove tag"] = "RemoveTag",
        ["Name ↑"] = "CollectionNameAsc",
        ["Name ↓"] = "CollectionNameDesc",
        ["Movies ↓"] = "CollectionMoviesDesc",
        ["Movies ↑"] = "CollectionMoviesAsc",
        ["No collections yet"] = "NoCollectionsYet",
        ["MediaElch collections (franchises, sagas) appear here once at least two of their movies are in your library and tagged in their .nfo files."] = "CollectionsEmptyHint",
        ["Collections with movies you haven't watched yet"] = "CollectionsUnwatchedTooltip",
        ["Collections you've watched every movie of"] = "CollectionsWatchedTooltip",
        ["Refresh changes"] = "RefreshChanges",
        ["Add Drive"] = "AddDrive",
        ["External drives that hold your movies. Identified by hardware ID, so changing drive letters won't break anything."] = "DrivesDescription",
        ["No drives yet"] = "NoDrivesYet",
        ["Click + Add Drive to get started."] = "AddDriveHint",
        ["Update"] = "Update",
        ["Add folder"] = "AddFolder",
        ["Scanning…"] = "Scanning",
        ["Cancel"] = "Cancel",
        ["Browse movies"] = "BrowseMovies",
        ["Rename drive"] = "RenameDrive",
        ["Sync to drive (state + fetched art)"] = "SyncDrive",
        ["Remove drive"] = "RemoveDrive",
        ["Remove folder"] = "RemoveFolder",
        ["Rescan all connected drives for new or changed files"] = "RescanAllDrivesTooltip",
        ["Rescan this drive's folders for new or changed movies"] = "RescanDriveTooltip",
        ["More drive actions"] = "MoreDriveActions",
        ["More actions"] = "MoreActions",
        ["Your collection at a glance"] = "StatsSubtitle",
        ["Total movies"] = "TotalMovies",
        ["Total runtime"] = "TotalRuntime",
        ["Avg rating"] = "AvgRating",
        ["Drives tracked"] = "DrivesTracked",
        ["✓ Watch progress"] = "WatchProgress",
        ["📺 TV Shows"] = "StatsTvShows",
        ["TV shows"] = "TvShowsLower",
        ["Episode runtime"] = "EpisodeRuntime",
        ["Avg show rating"] = "AvgShowRating",
        ["✓ Episodes watched"] = "EpisodesWatched",
        ["📅 Movies by decade"] = "MoviesByDecade",
        ["No year data yet."] = "NoYearData",
        ["🏷 Top genres"] = "TopGenres",
        ["No genres tagged yet."] = "NoGenresData",
        ["🎥 Top directors"] = "TopDirectors",
        ["No director data yet."] = "NoDirectorData",
        ["🎭 Top actors"] = "TopActors",
        ["No actor data yet."] = "NoActorData",
        ["No duplicates found"] = "NoDuplicatesFound",
        ["Every movie in your library appears once. Copies you keep in different languages, quality or editions aren't counted as duplicates."] = "NoDuplicatesHint",
        ["✓ keep"] = "KeepCopy",
        ["→ W&G"] = "SendToWGShort",
        ["Browse"] = "Browse",
        ["Nothing to browse yet"] = "NothingToBrowse",
        ["Add a folder of movies on the Drives page first."] = "BrowseNeedsDrive",
        ["Movies you watched and let go — kept as records, with your notes and history, so you never re-download by mistake."] = "WatchedGoneDescription",
        ["Search records…"] = "SearchRecords",
        ["Recently gone"] = "RecentlyGone",
        ["Add watched movie"] = "AddWatchedMovie",
        ["Export the records list to CSV"] = "ExportRecordsTooltip",
        ["No records yet"] = "NoRecordsYet",
        ["🎞  YOU WATCHED ON THIS DATE"] = "WatchedOnDate",
        ["🎬  RELEASED ON THIS DATE"] = "ReleasedOnDate",
        ["Nothing tied to today"] = "NothingTiedToToday",
        ["Check back tomorrow — On This Day surfaces movies you watched on this date in past years, plus films released on this date in past years."] = "OnThisDayHint",
        ["Match this movie on TMDb"] = "MatchMovieTmdb",
        ["Use this match"] = "UseThisMatch",
        ["Pick the correct film so the missing details are filled from the right entry."] = "PickCorrectFilmHint",
        ["Movie title…"] = "MovieTitlePlaceholder",
        ["Year"] = "Year",
        ["Search"] = "Search",
        ["Add a watched movie"] = "AddWatchedMovieTitle",
        ["Add to Watched & Gone"] = "AddToWatchedGone",
        ["Watched a film somewhere else — at the cinema, on a streaming service, a friend's copy — and want it on record? Find it on TMDb, add your note, and it's saved here as a Watched & Gone record. No file needed."] = "AddWatchedMovieDescription",
        ["Your note (optional)"] = "YourNoteOptional",
        ["Why you liked it, where you watched it…"] = "NotePlaceholder",
        ["Tags (optional, comma-separated)"] = "TagsOptional",
        ["e.g. drama, rewatch"] = "TagsPlaceholder",
        ["Watched on"] = "WatchedOn",
        ["Size:"] = "SizeLabel",
        ["Movie Details"] = "MovieDetailsTitle",
        ["Previous movie"] = "PreviousMovie",
        ["Previous movie (←)"] = "PreviousMovieTooltip",
        ["Next movie"] = "NextMovie",
        ["Next movie (→)"] = "NextMovieTooltip",
        ["OVERVIEW"] = "Overview",
        ["Studio"] = "Studio",
        ["IMDb"] = "IMDb",
        ["RATED"] = "Rated",
        ["RUNTIME"] = "Runtime",
        ["LANGUAGE"] = "LanguageLabel",
        ["FILE TYPE"] = "FileType",
        ["SUBTITLES"] = "SubtitlesUpper",
        ["COUNTRY"] = "CountryUpper",
        ["DURATION"] = "DurationUpper",
        ["RELEASED"] = "ReleasedUpper",
        ["FILE SIZE"] = "FileSizeUpper",
        ["Located on:"] = "LocatedOn",
        ["Drive offline — note saved locally; will sync to the movie folder when reconnected."] = "DriveOfflineNote",
        ["+ Add note"] = "AddNote",
        ["+ Add tag"] = "AddTagButton",
        ["Edit"] = "Edit",
        ["Save"] = "Save",
        ["Tag name (Enter to add)"] = "TagNamePlaceholder",
        ["What did you think of this film?"] = "FilmNotePlaceholder",
        ["📂 Open Folder"] = "OpenFolderButton",
        ["📑 Add to list"] = "AddToList",
        ["📝 Notes"] = "NotesLabel",
        ["Favorite"] = "FavoriteCard",
        ["View Details"] = "ViewDetails",
        ["Watchlist"] = "Watchlist",
        ["All movies"] = "AllMoviesLabel",
        ["Esc"] = "Esc",
        ["Size: M"] = "SizeMLabel",
        ["♥ Favorite"] = "FavoriteWithHeart",
        ["✓ Watched"] = "Watched",
        ["✕ Clear filters"] = "ClearFilters",
        ["📌 Watchlist"] = "WatchlistWithPin",
        ["📑 Add to list ▾"] = "AddToListDropdown",
        ["🗑 Remove from list"] = "RemoveFromList",
        ["Sort collections"] = "SortCollections",
        ["Remove folder and its movies"] = "RemoveFolderAndMovies",
        ["Library"] = "Library",
        ["Settings"] = "Settings",
        ["🏷  TAGS"] = "TagsHeading",
        ["Movies you have more than once. Copies in a different audio language or edition are kept on purpose. Other copies, including ones that differ only in quality or codec, are flagged as possible duplicates."] = "DupesDescription",
        ["Only possible duplicates"] = "OnlyPossibleDuplicates",
        ["Show ignored"] = "ShowIgnored",
        ["Re-check the files on disk and refresh. Run this after you delete a copy."] = "RescanDupesTooltip",
        ["Rescan"] = "Rescan",
        ["Matched by title + year only, no TMDb/IMDb id. Worth a quick check."] = "DuplicateMatchHint",
        ["verify"] = "Verify",
        ["Best copy: highest quality, then largest. Recommended to keep."] = "BestCopyHint",
        ["Send this copy to Watched & Gone (keeps a record, removes it from the library)"] = "SendToWatchedGoneTooltip",
        ["0 movies"] = "ZeroMovies",
        ["Unexpected Error"] = "Auto_Unexpected_Error_d17c2e74",
        ["Can't copy yet"] = "Auto_Can_t_copy_yet_db219881",
        ["Not enough free space"] = "Auto_Not_enough_free_space_8d845d16",
        ["Send to Watched & Gone?"] = "Auto_Send_to_Watched_Gone_245193c6",
        ["Delete this record?"] = "Auto_Delete_this_record_f9187972",
        ["New list"] = "Auto_New_list_d44f1a8e",
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
            if (root is ContentDialog dialog)
            {
                if (dialog.Title is string dialogTitle)
                    dialog.Title = TranslateText(dialogTitle);
                dialog.PrimaryButtonText = TranslateText(dialog.PrimaryButtonText);
                dialog.SecondaryButtonText = TranslateText(dialog.SecondaryButtonText);
                dialog.CloseButtonText = TranslateText(dialog.CloseButtonText);
            }
            if (root is TextBlock textBlock)
                textBlock.Text = TranslateText(textBlock.Text);
            if (root is ContentControl contentControl && contentControl.Content is string content)
                contentControl.Content = TranslateText(content);
            if (root is TextBox textBox)
                textBox.PlaceholderText = TranslateText(textBox.PlaceholderText);
            if (root is ComboBox comboBox)
            {
                comboBox.PlaceholderText = TranslateText(comboBox.PlaceholderText);

                // ComboBox items are hosted in a Popup when the dropdown opens,
                // so they are not reliably reachable through VisualTreeHelper.
                // Translate the items directly to ensure every search scope and
                // sort option is localized before the user opens the dropdown.
                foreach (var item in comboBox.Items)
                {
                    if (item is ComboBoxItem comboItem)
                    {
                        if (comboItem.Content is string itemText)
                            comboItem.Content = TranslateText(itemText);
                        else if (comboItem.Content is TextBlock itemBlock)
                            itemBlock.Text = TranslateText(itemBlock.Text);
                    }
                    else if (item is string itemText)
                    {
                        var index = comboBox.Items.IndexOf(item);
                        if (index >= 0) comboBox.Items[index] = TranslateText(itemText);
                    }
                }
            }
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
