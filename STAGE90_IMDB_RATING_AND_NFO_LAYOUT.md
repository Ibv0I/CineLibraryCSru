# Stage 90 — IMDb rating and NFO editor layout

Changes:
- Renamed the metadata dialog option to “Добавить данные фильма с КиноПоиска”.
- During a new movie addition, if a Kinopoisk API token is configured, the app also requests the matched film details and captures IMDb rating/vote count. This IMDb rating is saved to `movie.nfo` in the Kodi `<ratings>` block as `name="imdb"`.
- The checkbox still controls whether the Kinopoisk ID and Kinopoisk rating are included. Failure to retrieve optional IMDb data no longer blocks adding the movie; an explicitly requested Kinopoisk lookup failure keeps the dialog open.
- In the NFO editor, Genres now sits below Countries; Budget and Worldwide gross sit below Studios.

Validation:
- Both edited XAML files parse as well-formed XML.
- C# delimiter counts are balanced.
- A .NET build could not be run in this environment because the `dotnet` SDK is not installed.
