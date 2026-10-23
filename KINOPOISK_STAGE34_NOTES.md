# Stage 34 — Kinopoisk artwork editing and optional rating

- Movie details pencil action detects the primary metadata provider from `movie.nfo` and supports Kinopoisk IDs as well as TMDb IDs.
- For TMDb-selected movies, the metadata/artwork window offers an optional Kinopoisk rating checkbox. It is disabled when no Kinopoisk API token is configured and displays a hint below it.
- When enabled, the app searches Kinopoisk by movie title/year, fetches the matching rating, and writes a secondary `kinopoisk` rating plus a non-default Kinopoisk ID into `movie.nfo`.
- Primary TMDb IDs are kept primary; adding a secondary Kinopoisk rating does not make the pencil action treat the movie as Kinopoisk-sourced.
- Sidebar heading changed from “Библиотеки” to “Диски”.
- Validation available in this environment: XAML and RESW XML parsing and ZIP integrity. Full .NET compilation/runtime testing requires GitHub Actions or Visual Studio.
