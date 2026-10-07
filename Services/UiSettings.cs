namespace CineМедиатекаCS.Services;

/// <summary>
/// v3.0.0 — user-facing UI preferences exposed through the Настройки dialog.
///
/// Назадed by the existing prefs table (AppState.GetPref/SetPref) so they
/// persist across sessions. <see cref="Changed"/> fires whenever a value is
/// updated, so live UI (movie cards) can react without a restart or reload.
///
/// Both visual extras default to OFF, so the out-of-the-box experience is the
/// fast, flat one — performance-minded users keep it, and anyone who wants the
/// richer look opts in.
/// </summary>
public static class UiНастройки
{
    private const string KeyCardShadows = "ui_cardShadows";
    private const string KeyReduceMotion = "ui_reduceMotion";
    private const string KeyCardBorders = "ui_cardBorders";
    private const string KeyMica = "ui_mica";              // legacy bool
    private const string KeyMicaLevel = "ui_micaLevel";   // v3.3.2 — off|subtle|strong
    private const string KeyВоспроизвестиerPath = "ui_playerPath"; // v3.6.0: "" = Windows default

    /// <summary>How much Windows Mica material shows behind the window.
    /// Выкл. = flat solid (also best on weak GPUs); Subtle / Strong control how
    /// translucent the floating sidebar is over the wallpaper-tinted backdrop.</summary>
    public enum MicaLevel { Выкл., Subtle, Strong }

    /// <summary>Selected Mica intensity. По умолчанию Subtle.</summary>
    public static MicaLevel Mica { get; private set; } = MicaLevel.Subtle;

    /// <summary>True whenever Mica is on (either intensity).</summary>
    public static bool MicaEnabled => Mica != MicaLevel.Выкл.;

    /// <summary>Thin outline around movie cards. По умолчанию on.</summary>
    public static bool CardBorders { get; private set; } = true;

    /// <summary>Faint resting drop shadow on movie cards. По умолчанию off.</summary>
    public static bool CardShadows { get; private set; }

    /// <summary>Disable the card hover zoom/lift animation. По умолчанию off.</summary>
    public static bool ReduceMotion { get; private set; }

    /// <summary>v3.6.0: the program Воспроизвести opens videos with (VLC, MPC-HC, ...).
    /// Empty = whatever Windows opens video files with. See VideoВоспроизвестиer.</summary>
    public static string ВоспроизвестиerPath { get; private set; } = "";

    /// <summary>Raised after any setting changes so live UI can re-apply it.</summary>
    public static event Action? Changed;

    /// <summary>Load persisted values. Call once at startup, after AppState.Initialize().</summary>
    public static void Load()
    {
        CardBorders  = AppState.Instance.GetPref(KeyCardBorders,  "true")  == "true";
        CardShadows  = AppState.Instance.GetPref(KeyCardShadows,  "false") == "true";
        ReduceMotion = AppState.Instance.GetPref(KeyReduceMotion, "false") == "true";
        ВоспроизвестиerPath   = AppState.Instance.GetPref(KeyВоспроизвестиerPath,   "");

        var lvl = AppState.Instance.GetPref(KeyMicaLevel, "");
        Mica = lvl switch
        {
            "off"    => MicaLevel.Выкл.,
            "subtle" => MicaLevel.Subtle,
            "strong" => MicaLevel.Strong,
            // Migrate the old on/off pref for existing users.
            _ => AppState.Instance.GetPref(KeyMica, "true") == "true" ? MicaLevel.Subtle : MicaLevel.Выкл.,
        };
    }

    public static void SetMica(MicaLevel value)
    {
        if (Mica == value) return;
        Mica = value;
        AppState.Instance.SetPref(KeyMicaLevel,
            value switch { MicaLevel.Выкл. => "off", MicaLevel.Strong => "strong", _ => "subtle" });
        Changed?.Invoke();
    }

    public static void SetCardBorders(bool value)
    {
        if (CardBorders == value) return;
        CardBorders = value;
        AppState.Instance.SetPref(KeyCardBorders, value ? "true" : "false");
        Changed?.Invoke();
    }

    public static void SetCardShadows(bool value)
    {
        if (CardShadows == value) return;
        CardShadows = value;
        AppState.Instance.SetPref(KeyCardShadows, value ? "true" : "false");
        Changed?.Invoke();
    }

    /// <summary>No Changed event: the player choice doesn't affect any visible UI.</summary>
    public static void SetВоспроизвестиerPath(string value)
    {
        ВоспроизвестиerPath = value;
        AppState.Instance.SetPref(KeyВоспроизвестиerPath, value);
    }

    public static void SetReduceMotion(bool value)
    {
        if (ReduceMotion == value) return;
        ReduceMotion = value;
        AppState.Instance.SetPref(KeyReduceMotion, value ? "true" : "false");
        Changed?.Invoke();
    }
}
