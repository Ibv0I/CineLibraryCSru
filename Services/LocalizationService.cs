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
        [CineLibraryCS.Services.LocalizationService.TranslateText("Favorites")] = CineLibraryCS.Services.LocalizationService.TranslateText("Favorites"),
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
        [CineLibraryCS.Services.LocalizationService.TranslateText("Watched")] = "FilterWatched",
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
        [CineLibraryCS.Services.LocalizationService.TranslateText("No movies match your filters")] = "NoMoviesMatch",
        ["Try clearing the search or choosing 'All' watched filter."] = "ClearSearchHint",
        ["TV SHOWS MATCHING"] = "TvShowsMatching",
        [CineLibraryCS.Services.LocalizationService.TranslateText("TV SHOWS IN THIS LIST")] = "TvShowsInList",
        ["Send to Watched and Gone"] = "SendToWatchedGone",
        ["Keep as a record, remove from library"] = "KeepRecordRemoveLibrary",
        ["Clear selection (Esc)"] = "ClearSelection",
        ["Surprise me — random pick from current view"] = "SurpriseRandomTooltip",
        ["Movie details"] = "MovieDetails",
        ["Play"] = "Play",
        ["Open folder"] = "OpenFolder",
        ["Genres"] = "Genres",
        ["Cast"] = "Cast",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Director")] = CineLibraryCS.Services.LocalizationService.TranslateText("Director"),
        ["Writer"] = "Writer",
        ["Country"] = "Country",
        ["Released"] = "Released",
        [CineLibraryCS.Services.LocalizationService.TranslateText("File size")] = "FileSize",
        ["Resolution"] = "Resolution",
        ["Video codec"] = "VideoCodec",
        ["Audio"] = "Audio",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Subtitles")] = CineLibraryCS.Services.LocalizationService.TranslateText("Subtitles"),
        [CineLibraryCS.Services.LocalizationService.TranslateText("Duration")] = CineLibraryCS.Services.LocalizationService.TranslateText("Duration"),
        ["YOUR NOTE"] = "YourNote",
        ["Anything you want to remember about this episode…"] = "EpisodeNotePlaceholder",
        ["♡ Favorite"] = CineLibraryCS.Services.LocalizationService.TranslateText("Favorite"),
        ["♥ Favorited"] = "Favorited",
        ["▶ Play"] = "PlayWithIcon",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Mark watched")] = "MarkWatched",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Mark unwatched")] = "MarkUnwatched",
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
        [CineLibraryCS.Services.LocalizationService.TranslateText("No collections yet")] = "NoCollectionsYet",
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
        [CineLibraryCS.Services.LocalizationService.TranslateText("Favorite")] = "FavoriteCard",
        ["View Details"] = "ViewDetails",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Watchlist")] = CineLibraryCS.Services.LocalizationService.TranslateText("Watchlist"),
        [CineLibraryCS.Services.LocalizationService.TranslateText("All movies")] = "AllMoviesLabel",
        ["Esc"] = "Esc",
        ["Size: M"] = "SizeMLabel",
        ["♥ Favorite"] = "FavoriteWithHeart",
        ["✓ Watched"] = CineLibraryCS.Services.LocalizationService.TranslateText("Watched"),
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
        ["No folders"] = "NoFolders",
        ["Add at least one folder to this drive before updating."] = "AddFolderBeforeUpdate",
        ["Watch progress"] = "WatchProgress",
        ["Movies by decade"] = "MoviesByDecade",
        ["Top genres"] = "TopGenres",
        ["Top directors"] = "TopDirectors",
        ["Top actors"] = "TopActors",
        ["YOU WATCHED ON THIS DATE"] = "WatchedOnThisDate",
        ["RELEASED ON THIS DATE"] = "ReleasedOnThisDate",
        ["Add a drive whose folders contain tvshow.nfo (MediaElch-scraped shows), then Update Database."] = "Step7_450122a958",
        ["All TV shows"] = "Step7_d87d7e8e93",
        ["CAST"] = "Step7_2839299076",
        ["Cine"] = "Step7_b1924390f7",
        ["CineLibrary"] = "Step7_ce30dce6a9",
        ["Fetch missing info from TMDB"] = "Step7_938ed82249",
        ["TMDb"] = "Step7_1fca052e2d",
        ["When you watch a movie and delete it from your drive, right-click it in the library and choose “Send to Watched & Gone”. Or use “Add watched movie” above for films you saw elsewhere — at the cinema, on a streaming service, a friend's copy. Either way, the poster, details, your notes and watch history stay here as a permanent record."] = "Step7_589327642b",
        ["📝 Add note"] = "Step7_77554d16b4",
        ["English"] = "Step7_78463a384a",
        ["Download"] = "Step7_801ab24683",
        ["Undo"] = "Step7_1cdc076b28",
        ["Rename"] = "Step7_904a830405",
        ["Delete list"] = "Step7_953dd1a47b",
        ["Skip existing"] = "Step7_0c95b32dcd",
        ["Overwrite"] = "Step7_da364eb37e",
        ["Delete"] = "Step7_f2a6c498fb",
        ["List name"] = "Step7_5102491a98",
        ["Close"] = "Step7_d3d2e61733",
        ["Save or restore everything personal — favorites, watchlist, notes,"] = "Step7_9546a778b4",
        ["Building backup…"] = "Step7_6d66d60555",
        ["Reading backup…"] = "Step7_379b2b5114",
        ["Card shortcuts (F, W, Delete) act on the current selection — "] = "Step7_ab6b5e268e",
        ["A fast, native movie catalog for MediaElch-scraped collections."] = "Step7_f61fe2d7f8",
        ["Browse, search and play your movies across multiple external drives."] = "Step7_ce5d56dd33",
        ["Built with C# + WinUI 3."] = "Step7_44f9d33f8a",
        ["MISSING"] = "Step7_10d1208b48",
        ["Open details"] = "Step7_0da9511b88",
        ["Send to Watched & Gone"] = "Step7_dba2bf42a0",
        ["Send"] = "Step7_94966d9074",
        ["Restore to library"] = "Step7_5177379c3b",
        ["Delete record permanently"] = "Step7_e106f7c626",
        ["Delete forever"] = "Step7_43a5f35567",
        ["Create"] = "Step7_686e697538",
        ["Drive label (e.g. Seagate Red 5TB)"] = "Step7_4cb84bdfd6",
        ["Select drive:"] = "Step7_bb7c692886",
        ["Drive label:"] = "Step7_8d62dc7c05",
        ["After adding, use “+ Add Folder” on the drive card to pick which folders to index."] = "Step7_fd7158320b",
        ["Add"] = "Step7_ec211f7c20",
        ["Remove folder?"] = "Step7_dfb56eab5a",
        ["Preparing…"] = "Step7_c2a63550a3",
        ["Scan cancelled"] = "Step7_06a80d6e8d",
        ["Refresh cancelled"] = "Step7_7d3b47d0b8",
        ["Keep"] = "Step7_02bce93bff",
        ["Keep all"] = "Step7_74a2c67179",
        ["Keep none"] = "Step7_a79b7c4caa",
        ["Review missing movies"] = "Step7_b3496d385f",
        ["Apply"] = "Step7_9639e32cab",
        ["e.g. Seagate Red 5TB - Movies"] = "Step7_0bb70da7ba",
        ["Give this drive a unique name so you can tell it apart from other drives with the same model."] = "Step7_7061810abd",
        ["Rename Drive"] = "Step7_a886056c6d",
        ["Save that state to the drive first (so it comes back if you re-add this drive later)"] = "Step7_8665a5e259",
        ["Remove Drive?"] = "Step7_f10e2b6b07",
        ["Welcome to CineLibrary"] = "Step7_e2a791de17",
        ["This list is empty"] = "Step7_fe26697e19",
        ["Match this show on TMDb"] = "Step7_950585184a",
        ["Pick the correct show so the missing details are filled from the right entry."] = "Step7_f2c91c31aa",
        ["Show title…"] = "Step7_f87f9755a0",
        ["Couldn't reach TMDb. Check your connection and try again."] = "Step7_79489082c2",
        ["OFFLINE"] = "Step7_7ed929f45b",
        ["ONLINE"] = "Step7_86d1debf3f",
        ["Select drive"] = "Step7_da5e187c66",
        ["Choose All to see every show."] = "Step7_2ed2a9ae6a",
        ["Anything you want to remember about this show…"] = "Step7_35a9bba459",
        ["Add a folder"] = "Step7_9b708126ae",
        ["Error"] = "Step8_902b0d55fd",
        ["📂 Add my movies folder"] = "Step8_f91c0f84b9",
        ["Drive"] = "Step8_f2c6151d6c",
        ["📌 In Watchlist"] = "Step8_26b92fd2e4",
        ["☐ Add to Watchlist"] = "Step8_4c181008ff",
        ["No fully watched shows yet"] = "Step8_a44a1ac120",
        ["Nothing left to watch"] = "Step8_53618325f6",
        ["This view ({label}) as CSV…"] = "Step8_c379c76ac7",
        ["This view ({label}) as HTML…"] = "Step8_4ecffc7ed2",
        ["Add movies by right-clicking any poster and choosing "] = "Step8_1878895391",
        ["Nothing to copy."] = "Step9_28df8b5d32",
        ["Continue with {count} online"] = "Step9_b39e6393d2",
        ["Movie {done} of {total}"] = "Step9_826e98db8d",
        ["Cancelled — {items}"] = "Step9_ceaa2747d4",
        ["Rename list"] = "Step9_cfdd1ac0c1",
        ["Delete \"{name}\"?"] = "Step9_dba7c442e0",
        ["All movies are on offline drives. Plug in:\n• {drives}\n\nThen try again."] = "Step9_4cd60de197",
        ["Nothing unwatched left — fully caught up 🎉"] = "Step9_b00ff06edd",
        ["Clear search, then clear selection"] = "Step9_22326acdfa",
        ["Select every card on screen"] = "Step9_14d9e343f9",
        ["Add / remove a single card"] = "Step9_80d23ae510",
        ["Remove selection from the current list"] = "Step9_83532b446d",
        ["Saved to {path}"] = "Step9_c77d33a8b2",
        ["Export failed: {error}"] = "Step9_d5f410c242",
        ["Import failed: {error}"] = "Step9_50d4cc3db7",
        ["Save or restore everything personal — favorites, watchlist, notes, "] = "Step9_7b8c3e4bd9",
        ["Added {count} to watchlist"] = "Step9_f6c723d55d",
        ["Added {count} to “{list}”"] = "Step9_f242c4646c",
        ["Exported {count} movies to {format}"] = "Step9_5e291e8465",
        ["Playback"] = "Step9_8dc55bfcd3",
        ["Choose"] = "Step9_961f2247a2",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Continue")] = "Step9_a0bfb8e59e",
        ["Open"] = "Step9_c3bf447eab",
        ["Folder"] = "Step9_b0f2b97dc5",
        ["All movies as CSV…"] = "Step9_1627055788",
        ["All movies as HTML…"] = "Step9_61ab113e7c",
        ["Tags"] = "Step9_189f63f277",
        ["Unknown"] = "Step9_88183b946c",
        ["Not connected"] = "Step9_7b277018e4",
        ["Connected as {letter}:"] = "Step9_3897afd76e",
        ["Yes"] = "Step9_93cba07454",
        ["No"] = "Step9_bafd7322c6",
        ["Record a movie you watched but never had in your library"] = "Step11_82490a0689",
        ["Drive offline"] = "Step11_e6622d7f1b",
        ["This drive is not connected."] = "Step11_5d9c78c0e2",
        ["Added to Watched & Gone"] = "Step11_58f3ffed53",
        ["Exported records to CSV"] = "Step11_d7c37433f1",
        ["By genre"] = "Step11_d46a1c070a",
        ["By decade"] = "Step11_8a6e97e174",
        ["By rating"] = "Step11_70b1482091",
        ["By studio"] = "Step11_cec6cc8c5e",
        ["Pick a genre to filter the library."] = "Step11_ef337f57da",
        ["Movies grouped by their decade of release."] = "Step11_1d14e6c793",
        ["Movies grouped by their star rating."] = "Step11_55e1e55586",
        ["Movies grouped by studio."] = "Step11_777cf5cbae",
        ["📂 Copy movies to folder…"] = "Step12_4847410da7",
        ["📤 Export as image…"] = "Step12_3bb5570bb3",
        ["Rendering image…"] = "Step12_d11a255b26",
        ["Image export failed — see debug log"] = "Step12_0335760c29",
        ["Copy cancelled"] = "Step12_cdbc3c20a1",
        ["Untitled list"] = "Step12_92990f9202",
        ["📤  Export backup…"] = "Step12_8b16353cd5",
        ["Focus the search box"] = "Step12_9be6ff5d68",
        ["Copy movies to folder"] = "Step12_57dfa476c0",
        ["Export as image"] = "Step12_1cb9d18e0f",
        ["Appearance"] = "Step12_a1c58e9422",
        ["BackgroundMaterial"] = "Step12_014148d64a",
        ["CardBorders"] = "Step12_d0fb9fe741",
        ["CardShadows"] = "Step12_c1b5ba2be6",
        ["ReduceMotion"] = "Step12_22c735a84e",
        ["VideoPlayer"] = "Step12_c1c6353ee8",
        ["Language"] = "Step12_4994a8ffeb",
        ["ApplyLanguageRestart"] = "Step12_3a6d8e8443",
        ["Done"] = "Step12_f92965e2c8",
        ["No drives found"] = "Step13_e9fb87c9c5",
        ["No external drives are currently connected. Connect a drive and try again."] = "Step13_9a7c42ce5e",
        ["All drives added"] = "Step13_19f809d43e",
        ["All connected drives are already in your library."] = "Step13_015bf20d24",
        ["Wrong drive"] = "Step13_af0eaf83bb",
        ["Already added"] = "Step13_d12c93ba9a",
        ["(drive root)"] = "Step13_001253b37f",
        ["Scan error"] = "Step13_4f463e7373",
        ["No online drives"] = "Step13_9b61b7bb26",
        ["Plug in at least one drive that has movies on it, then try again."] = "Step13_5b6aeebd54",
        ["Nothing to refresh"] = "Step13_1a3162c09d",
        ["None of the online drives have any folders configured to scan. "] = "Step13_b36882e7e0",
        ["Add a folder on a drive card first."] = "Step13_a3d3c3817c",
        ["Refresh error"] = "Step13_846a54fef5",
        ["Has note"] = "Step13_1b5e187b22",
        ["Their folders may have been renamed or deleted on the drive.\n\n"] = "Step13_8a52dcbd93",
        ["Removing them will delete their entries (and cached posters/fanart) from the CineLibrary database. "] = "Step13_0fb3372859",
        ["The actual files on the drive are not touched.\n\n"] = "Step13_d31d24ed6f",
        ["Tip: if the drive was only partially connected or you scanned the wrong folder, "] = "Step13_54ee0eade8",
        ["Cleaned up"] = "Step13_512f66e61d",
        ["Remove"] = "Step13_1063e38cb5",
        [CineLibraryCS.Services.LocalizationService.TranslateText("No fully watched collections yet")] = "Step13_a8a65c8ae8",
        ["Choose All to see every collection."] = "Step13_035c80e135",
        [CineLibraryCS.Services.LocalizationService.TranslateText("One movie released on this date in a past year.")] = "Step13_09346abb6f",
        [CineLibraryCS.Services.LocalizationService.TranslateText("One movie you watched on this date in a past year.")] = "Step13_2e831aaf9f",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Not yet rated")] = "Step13_d144329e3e",
        [CineLibraryCS.Services.LocalizationService.TranslateText("CSV file")] = "Step13_0072cbeb5f",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Specials")] = "Step13_d1aa22a312",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Mark all watched")] = "Step13_067e645453",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Mark all unwatched")] = "Step13_fd30bfacbe",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Previous episodes")] = "Step13_d738ee70ea",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Next episodes")] = "Step13_8f34961967",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Fetching cast photos…")] = "Step13_87705a4615",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Updated.")] = "Step13_4e43554896",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Something went wrong. Please try again.")] = "Step13_e9f36d65b0",
        [CineLibraryCS.Services.LocalizationService.TranslateText("TV SHOW MATCHING")] = "Step13_42198fb58b",
        [CineLibraryCS.Services.LocalizationService.TranslateText("TV SHOWS")] = "Step13_c6a30884c1",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Record deleted")] = "Step13_a67e750ed0",
        [CineLibraryCS.Services.LocalizationService.TranslateText("Tip: add movies to your watchlist from the movie detail dialog.")] = "Step13_8087d3dae9",
        ["Stereo"] = "Step13_bbc45d9b35",
        ["Previous"] = "Step13_dd1f775e44",
        ["Next"] = "Step13_10ac3d0425",
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
