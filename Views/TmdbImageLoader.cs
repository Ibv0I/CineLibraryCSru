using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using CineLibraryCS.Services.Tmdb;

namespace CineLibraryCS.Views;

/// <summary>Loads remote TMDb images through the configured HttpClient so proxy settings are respected.</summary>
internal static class TmdbImageLoader
{
    public static async Task<BitmapImage?> LoadAsync(TmdbClient client, string? url, int decodePixelWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var bytes = await client.GetImageBytesAsync(url);
            var bitmap = new BitmapImage();
            if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TMDb image preview failed: {ex.Message}");
            return null;
        }
    }
}
