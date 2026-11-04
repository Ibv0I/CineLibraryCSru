# Stage 120 — unified MTV statuette mapping

- Updated `GetAwardIconFileName` in `Views/MovieDetailDialog.xaml.cs`.
- `Премия канала MTV`, `MTV`, `MTV Movie Award(s)`, `Кинонаграды «MTV-Россия»`, `MTV-Россия`, and `MTV Russia` now all resolve to `Assets/Awards/mtv-channel.png`.
- Removed the separate `mtv-russia.png` mapping from the award icon resolver. No new images were generated; the existing golden-popcorn icon is reused.
- No NFO, database, or library metadata is modified.
