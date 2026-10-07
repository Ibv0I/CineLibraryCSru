using System.Продолжительность.InteropServices.WindowsПродолжительность;
using Windows.Storage.Streams;

namespace CineМедиатекаCS.Helpers;

public static class StreamExtensions
{
    public static byte[] ReadВсеBytes(this Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
