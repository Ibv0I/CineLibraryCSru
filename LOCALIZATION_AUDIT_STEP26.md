# Localization pass 26 — visible interface only

This pass focuses on active movie-library UI, backup, Watched & Gone, drive feedback, and file-path actions. Hidden TV Shows/episode screens and TMDb dialogs are excluded.

Changes:
- Added 14 exact-text localization mappings for user-facing help, empty states, and operation feedback.
- Added matching entries to both `ru-RU` and `en-US` resources.
- Fixed the backup dialog description: it was translated as a prefix followed by English concatenated fragments; it now translates the complete sentence as one string.

Validation performed: XML parsing, resource-name uniqueness and parity, C# mapping-key uniqueness, ZIP integrity. A full C# build and Windows runtime launch were not performed.
