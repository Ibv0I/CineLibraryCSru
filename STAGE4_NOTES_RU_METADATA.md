# Stage 4 metadata refinements

- Prefer Russian release date from TMDb release_dates region RU; fall back to movie release_date when unavailable.
- Prefer Russian age certification, then US certification, then any available certification.
- Map common production-country ISO codes to Russian country names in movie.nfo.
- Normalize audio/subtitle language tags to three-letter codes (including rus and eng).
- Calculate aspect ratio from width/height instead of serializing MediaInfo enum values such as Opaque.
- Normalize common MediaInfo codec enum names (e.g. MPEGHISOHEVC -> hevc).
