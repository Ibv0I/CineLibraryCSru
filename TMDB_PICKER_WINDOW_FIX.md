# TMDb picker window fix

The TMDb picker was changed from a WinUI ContentDialog to a resizable Window. ContentDialog's internal presenter was constraining the width despite Width/MaxWidth settings, which is why the earlier archives appeared unchanged. The new window is resized and centered relative to the main application window before Activate(), so the first visible frame should already have its final size. Search, year and button use separate fixed/star columns; result overview has the remaining width.

The caller awaits ShowAsync(), which returns ContentDialogResult for compatibility. Owner window is disabled while the picker is open and re-enabled on close. Full Windows/.NET build was not available in the authoring environment; verify with GitHub Actions.
