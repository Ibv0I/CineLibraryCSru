# Исправления сборки этапа TMDb

В предоставленном исходном архиве страница называется `Views/MoviePreparationPage.xaml.cs`; файла `MediaPreparationPage.xaml.cs` в архиве нет. Поэтому исходные ошибки GitHub Actions не воспроизводились буквально на этом архиве.

В этой версии оставлен используемый проектный класс `MoviePreparationPage`, который подключён в `MainWindow.xaml.cs`. Код не обращается к несуществующему `LocalizationService.CurrentLanguage`: язык TMDb задаётся поддерживаемым явным значением `ru-RU`. Код также не использует отсутствующий `PickerLocationId.Videos`; путь к медиатеке задаётся пользователем в поле страницы, поэтому недоступный enum не требуется.

Проверка наличия проблемных символов: `LocalizationService.CurrentLanguage` и `PickerLocationId.Videos` в исходниках отсутствуют. Компиляция Windows/WinUI в этой среде недоступна, поэтому успешная сборка на GitHub Actions не может быть гарантирована без запуска CI.
