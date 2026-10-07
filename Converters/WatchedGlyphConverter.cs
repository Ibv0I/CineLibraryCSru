using Microsoft.UI.Xaml.Data;

namespace CineМедиатекаCS.Converters;

/// <summary>true → "✓" (watched), false → "○" (unwatched). Used by the TV
/// episode list's watched toggle.</summary>
public class ПросмотреноGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? "✓" : "○";

    public object ConvertНазад(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
