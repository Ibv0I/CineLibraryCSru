using System.Diagnostics;

namespace CineLibraryCS.Services;

/// <summary>
/// v3.6.0: one place every Play button goes through. Opens the video in the
/// player chosen in Settings; with none chosen, or if that program has since
/// been removed, it falls back to whatever Windows opens video files with.
/// </summary>
public static class VideoPlayer
{
    public static async Task<bool> PlayAsync(string videoPath)
    {
        var player = UiSettings.PlayerPath;
        if (player.Length > 0 && File.Exists(player))
        {
            try
            {
                var psi = new ProcessStartInfo(player) { UseShellExecute = false };
                psi.ArgumentList.Add(videoPath);
                Process.Start(psi)?.Dispose();
                return true;
            }
            catch { /* fall back to the Windows default below */ }
        }
        return await Windows.System.Launcher.LaunchUriAsync(new Uri(videoPath));
    }
}
