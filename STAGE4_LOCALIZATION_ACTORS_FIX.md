# Stage 4 — metadata localization and actor photos

- Prefer Russian regional release date from TMDb release_dates (RU); fall back to general release date when unavailable.
- Prefer Russian age certification (RU); fall back to the available US certification if TMDb has no Russian rating.
- Translate common production-country ISO codes into Russian names.
- Normalize MediaInfo video codec identifiers (including MPEGHISOHEVC -> hevc), calculate aspect ratio from frame dimensions instead of enum text, normalize audio codec identifiers, remove the localized “кан.” suffix from channel counts, and write language tags as three-letter codes (rus/eng).
- Add a checkbox to download cast profile photos to each movie folder's `.actors` directory. NFO actor thumb entries point to the downloaded local image when available; otherwise they retain the TMDb URL. Existing actor photos are not overwritten.

Build not run in this environment because the .NET SDK is unavailable.
