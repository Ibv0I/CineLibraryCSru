# Media library preparation — stage 1

Added a new sidebar page, **Media preparation**, for discovering folders that contain video files but do not yet have a movie NFO.

- Choose a root folder using the Windows folder picker.
- Recursively search supported video extensions.
- Show each matching folder, its path, and video filename/count.
- Skip folders that already contain a movie NFO; `tvshow.nfo` and `season.nfo` do not count as movie NFO files.
- Skip inaccessible directories, system directories, and reparse-point directories to avoid permission failures and loops.
- Read-only operation: this stage does not create, overwrite, rename, or move files.

TMDb matching, artwork selection, NFO writing, and rescan are intentionally not part of stage 1.

Validation performed: XAML, project, and resource XML files parse successfully. A .NET build could not be run in this environment because the `dotnet` CLI is not installed.
