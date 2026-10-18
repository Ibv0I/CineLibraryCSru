# Movie Library — fixes for media preparation UI

This archive includes the TMDb media-preparation UI changes:

- Removed the **Clear** button from the media preparation page.
- Replaced manual root-path entry with the standard Windows folder picker, matching the picker used by the Drives page.
- Enlarged the TMDb match dialog and set it to dark theme.
- Improved spacing for title/year/search fields, and increased result row/poster sizing.
- Made result titles, years/subtitles, and descriptions readable on the dark background; changed the cancel button label to Russian.

The TMDb selection still only records the match for the current session; this stage does not write NFO or image files.

Build validation in this environment was limited to source/archive checks and XML parsing of the edited XAML files. A .NET build was not run here.
