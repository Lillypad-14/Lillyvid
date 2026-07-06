using VideoSyncPrototype.Phone.Core.Games;
using VideoSyncPrototype.Phone.Core.Home;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Core.Wallpapers;

namespace VideoSyncPrototype.Phone;

// Slim configuration surface for the vendored Phone module. Persisted as a nested object
// inside Lillypad Toolkit's real VideoSyncPrototype.Configuration (Dalamud only allows one
// IPluginConfiguration per plugin), so Save() delegates back to the host's SavePluginConfig
// via SaveHook rather than owning the config file itself.
[Serializable]
internal sealed class Configuration
{
    public bool WelcomeShown { get; set; }
    public bool LockPosition { get; set; }
    public bool StandaloneMode { get; set; }
    public float PhoneScale { get; set; } = 1.25f;
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Dark;
    public string AccentName { get; set; } = "Violet";
    public string LightWallpaperId { get; set; } = "ShadowDark";
    public string DarkWallpaperId { get; set; } = "ShadowDark";
    public List<CustomWallpaper> CustomWallpapers { get; set; } = new();
    public HomeLayout? Home { get; set; }
    public List<GameStatRecord> GameStats { get; set; } = new();

    // Wired by the host at startup so Save() persists the whole plugin config.
    [NonSerialized] private Action? saveHook;

    public void AttachSaveHook(Action hook) => saveHook = hook;

    public void Save() => saveHook?.Invoke();
}
