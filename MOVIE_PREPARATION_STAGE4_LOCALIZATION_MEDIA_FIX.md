# Movie preparation metadata refinements

- Prefer TMDb's Russian (`RU`) release date and age certification when available; keep the existing US/other-region certification fallback.
- Translate common production-country ISO codes into Russian names in `movie.nfo`.
- Normalize audio/subtitle language values to three-letter codes (`rus`, `eng`, etc.).
- Calculate video aspect ratio from width and height instead of stringifying MediaInfo's enum (which can display `Opaque`).
- Normalize common MediaInfo video codec enum values, including `MPEGHISOHEVC` -> `hevc`.
- Strip localized channel suffixes such as `кан.` from channel counts, leaving values like `5.1`.
