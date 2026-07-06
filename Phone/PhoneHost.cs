using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using VideoSyncPrototype.Phone.Apps.LillypadGo;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Analytics;
using VideoSyncPrototype.Phone.Core.Device;
using VideoSyncPrototype.Phone.Core.Wallpapers;

namespace VideoSyncPrototype.Phone;

// Compatibility facade for the vendored Aetherphone "Phone" module (Apps/Games + phone
// shell), ported from https://github.com/XeldarAlz/FFXIV-Aetherphone (AGPL-3.0-or-later).
// The vendored code references a static `Plugin` for its ambient services; this bridges
// those to Lillypad Toolkit's real Dalamud services without a second plugin bootstrap.
internal static class Plugin
{
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    public static Configuration Cfg { get; private set; } = null!;
    public static FontService Fonts { get; private set; } = null!;
    public static IAnalyticsService Analytics { get; private set; } = new NoOpAnalytics();
    public static WallpaperLibrary Wallpapers { get; private set; } = null!;
    public static DeviceStatus Device { get; private set; } = null!;
    public static LillypadGoState LillypadGo { get; private set; } = null!;
    public static LoadingState Loading { get; } = new();

    private static EncounterService? encounter;

    // Forwarded from Lillypad Toolkit's real Dalamud service container.
    public static IPluginLog Log => global::VideoSyncPrototype.Plugin.Log;
    public static ITextureProvider TextureProvider => global::VideoSyncPrototype.Plugin.TextureProvider;
    public static IClientState ClientState => global::VideoSyncPrototype.Plugin.ClientState;
    public static IObjectTable ObjectTable => global::VideoSyncPrototype.Plugin.ObjectTable;
    public static IFramework Framework => global::VideoSyncPrototype.Plugin.Framework;

    // The phone module persists its own settings (theme, home layout, game high scores)
    // to a dedicated file, decoupled from Lillypad's Dalamud IPluginConfiguration so its
    // internal vendored types don't have to be forced public.
    private static string ConfigPath =>
        Path.Combine(PluginInterface.ConfigDirectory.FullName, "phone-games.json");

    public static void Initialize(IDalamudPluginInterface pluginInterface)
    {
        PluginInterface = pluginInterface;
        Cfg = LoadConfig();
        Cfg.AttachSaveHook(SaveConfig);
        Fonts ??= new FontService(pluginInterface, Cfg.PhoneScale);
        var assemblyDir = pluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;
        var builtInWallpapers = new DirectoryInfo(Path.Combine(assemblyDir, "Wallpapers"));
        var customWallpapers = new DirectoryInfo(Path.Combine(pluginInterface.ConfigDirectory.FullName, "PhoneWallpapers"));
        Wallpapers ??= new WallpaperLibrary(TextureProvider, builtInWallpapers, customWallpapers, Cfg);
        Device ??= new DeviceStatus(
            global::VideoSyncPrototype.Plugin.ClientState,
            global::VideoSyncPrototype.Plugin.ObjectTable,
            global::VideoSyncPrototype.Plugin.DataManager);
        LillypadGo ??= LillypadGoState.Load(
            Path.Combine(pluginInterface.ConfigDirectory.FullName, "lillypadgo.json"));
        encounter ??= new EncounterService(LillypadGo, ClientState, ObjectTable, Framework);
    }

    private static Configuration LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                return JsonSerializer.Deserialize<Configuration>(File.ReadAllText(ConfigPath)) ?? new Configuration();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Phone: failed to load phone-games.json; starting fresh.");
        }

        return new Configuration();
    }

    private static void SaveConfig()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Phone: failed to save phone-games.json.");
        }
    }

    public static void Dispose()
    {
        encounter?.Dispose();
        encounter = null;
        LillypadGo?.Save();
        LillypadGo = null!;
        Device?.Dispose();
        Device = null!;
        Wallpapers?.Dispose();
        Wallpapers = null!;
        Fonts?.Dispose();
        Fonts = null!;
    }
}

// Minimal stand-in for Aetherphone's loading-screen service; the games module only
// pokes Loading.Show() from the font atlas rebuild path.
internal sealed class LoadingState
{
    public void Show()
    {
    }
}

internal sealed class NoOpAnalytics : IAnalyticsService
{
    public bool IsFirstRun => false;

    public void Track(AnalyticsEvent analyticsEvent)
    {
    }

    public void Dispose()
    {
    }
}
