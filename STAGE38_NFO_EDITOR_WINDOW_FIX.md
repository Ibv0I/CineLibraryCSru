# Stage 38 — NFO editor window fixes

- Replaced the fixed-size `ContentDialog` with a resizable WinUI `Window`.
- The editor now uses an explicit dark theme and dark root background, avoiding the white content flash/background.
- Window dimensions are restored and saved to `movie library_data/NfoEditorWindow.settings.json`; resize writes are debounced and the final size is saved on close.
- The editor is centered over the main window and disables/re-enables the owner while open.
- The save/cancel result contract remains compatible with the details-page caller, so the movie folder rescan still runs only after saving.
