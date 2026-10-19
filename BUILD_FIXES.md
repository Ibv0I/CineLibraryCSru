# TMDb picker build fix (v9)

Fixed GitHub Actions compiler errors in Views/TmdbPickerDialog.xaml.cs:
- Window does not expose a Loaded event, so handlers now use RootGrid.Loaded.
- Added `using Microsoft.UI;` so `Win32Interop.GetWindowIdFromWindow` resolves in the WinUI namespace.
- Existing TMDb picker behavior and layout are otherwise preserved.

Build was not executed in this environment; run GitHub Actions to verify.
