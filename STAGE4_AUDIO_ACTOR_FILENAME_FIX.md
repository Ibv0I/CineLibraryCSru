# Stage 4 — audio channel label and actor photo filenames

- Fixed audio labels in the movie detail dialog: channel values that already contain `кан.`/`channels` are normalized before display, preventing the suffix from appearing again. Common layouts such as `5.1` and `7.1` remain compact.
- Actor profile images now use only the sanitized actor name as the filename, e.g. `.actors/Брэд_Питт.jpg`, without the order-number prefix.

Build was not run in this environment; verify with GitHub Actions.
