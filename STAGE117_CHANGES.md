# Stage 117

- Genre browse groups now use a .NET Unicode-aware canonical key, so Cyrillic case variants (e.g. `Драма` / `драма`) are grouped correctly. Common Russian/English genre equivalents are grouped in browse and filtering only; the main library, NFO files, and stored genre values are not rewritten.
- Added separate award statuette image assets and award recognition for Sundance, Golden Eagle, MTV Movie Awards, European Film Academy, Karlovy Vary, Goya, Georges, San Sebastián, Asian Film Academy, and MTV-Russia.
- Project files are placed directly at the archive root; no nested `stage...` folders.
