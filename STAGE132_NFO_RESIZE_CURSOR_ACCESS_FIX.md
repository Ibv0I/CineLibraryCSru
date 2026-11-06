# Stage 132 — NFO resize cursor access fix

- Removed direct assignment to `UIElement.ProtectedCursor` from `NfoEditorDialog` because the property is not publicly accessible in WinUI 3.
- Removed the now-unused `InputSystemCursor` import and cursor-shape arguments.
- Kept all eight resize hit zones, pointer pressed/moved/released/capture-lost handlers, pointer capture, and block geometry persistence unchanged.
- Resize zones still perform resizing; this stage does not set a custom resize cursor.

Build status: source-level fix only. A full .NET/WinUI build was not run in this environment.
