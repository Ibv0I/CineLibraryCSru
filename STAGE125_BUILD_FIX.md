# Stage 125 — исправление сборки редактора NFO

Исправлена ошибка CS1503 в `Views/NfoEditorDialog.xaml.cs`.

Причина: содержимое карточки проверялось как `UIElement`, тогда как `Grid.SetRow` в используемом WinUI API требует `FrameworkElement`.

Изменение: проверка `block.Child is UIElement content` заменена на `block.Child is FrameworkElement content`. Элементы XAML, размещаемые внутри карточек, являются `FrameworkElement`, поэтому их поведение не меняется.

Остальные изменения Stage 124 сохранены. Полная сборка не запускалась в этом окружении, так как .NET SDK недоступен.
