# Исправления ошибок сборки этапа 2

В исходном архиве этапа 2 страница подготовки называется `Views/MoviePreparationPage.xaml.cs`, а не `Views/MediaPreparationPage.xaml.cs`. В этой версии уже используются корректные для проекта API:

- Локаль TMDb задаётся как `ru-RU`; `LocalizationService` не содержит свойства `CurrentLanguage` (у сервиса доступны `ApplyLanguage(...)` и `Get(...)`).
- Для выбора папки в WinUI 3 используйте `PickerLocationId.ComputerFolder` или `PickerLocationId.DocumentsLibrary`; значения `PickerLocationId.Videos` в используемом API нет.

Если GitHub Actions продолжает выдавать ошибки именно из `MediaPreparationPage.xaml.cs`, значит собирается другая версия/ветка файла, не совпадающая с исходниками этого архива. В ней нужно внести эти же замены и проверить, что в `MainWindow.xaml.cs` используется `MoviePreparationPage`.
