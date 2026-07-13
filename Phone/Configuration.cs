using SpotifyAPI.Web;
using VideoSyncPrototype.Phone.Core.Games;
using VideoSyncPrototype.Phone.Core.Home;
using VideoSyncPrototype.Phone.Core.Songs;
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
    public List<SongRecord> SongRecents { get; set; } = new();

    // Spotify remote. The client id is per-user: Spotify now requires each user to register their
    // own app (see THIRD-PARTY-NOTICES). The token is stored as-is, like FantasyPlayer does.
    public string SpotifyClientId { get; set; } = string.Empty;
    public PKCETokenResponse? SpotifyToken { get; set; }

    // Wired by the host at startup so Save() persists the whole plugin config.
    [NonSerialized] private Action? saveHook;

    public void AttachSaveHook(Action hook) => saveHook = hook;

    public void Save() => saveHook?.Invoke();
}
