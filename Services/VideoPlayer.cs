using System.Diagnostics;

namespace CineМедиатекаCS.Services;

/// <summary>
/// v3.6.0: one place every Воспроизвести button goes through. Opens the video in the
/// player chosen in Настройки; with none chosen, or if that program has since
/// been removed, it falls back to whatever Windows opens video files with.
/// </summary>
public static class VideoВоспроизвестиer
{
    public static async Task<bool> ВоспроизвестиAsync(string videoPath)
    {
        var player = UiНастройки.ВоспроизвестиerPath;
        if (player.Length > 0 && File.Exists(player))
        {
            try
            {
                var psi = new ProcessStartInfo(player) { UseShellExecute = false };
                psi.ArgumentList.Добавить(videoPath);
                Process.Start(psi)?.Dispose();
                return true;
            }
            catch { /* fall back to the Windows default below */ }
        }
        return await Windows.System.Launcher.LaunchUriAsync(new Uri(videoPath));
    }
}
