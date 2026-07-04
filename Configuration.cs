using Dalamud.Configuration;

namespace VideoSyncPrototype;

/// <summary>
/// Persisted plugin settings. Stored by Dalamud next to the plugin and reloaded
/// on every launch, so the user only ever pastes their Watch2Gether API key once.
/// </summary>
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// Watch2Gether account API key. Used to create rooms server-side through the
    /// official API (no browser scraping). Empty until the user sets it up.
    /// </summary>
    public string Watch2GetherApiKey { get; set; } = string.Empty;

    /// <summary>Block YouTube ads in the in-world player.</summary>
    public bool AdBlockEnabled { get; set; } = true;

    /// <summary>
    /// Video upscaling preset: 0 Off (bilinear, the original look), 1 Fast,
    /// 2 Balanced, 3 Quality, 4 Ultra, 5 Custom. Off is the default so the screen
    /// looks exactly as it always has until the user opts in.
    /// </summary>
    public int UpscaleMode { get; set; }

    /// <summary>Filter used when <see cref="UpscaleMode"/> is Custom: 0 Bilinear, 1 Bicubic, 2 Lanczos.</summary>
    public int UpscaleFilter { get; set; }

    /// <summary>Adaptive sharpen amount (0..1) applied after upscaling when Custom.</summary>
    public float UpscaleSharpness { get; set; }

    /// <summary>
    /// Show a live debug readout under the upscaling controls (resolved filter,
    /// sharpen amount, source resolution, and whether the filter is actually active).
    /// Off by default.
    /// </summary>
    public bool UpscaleDebugOverlay { get; set; }

    /// <summary>
    /// Capture/source resolution the renderer bridge renders the browser at:
    /// 0 = 720p (default, the original look), 1 = 1080p, 2 = 1440p, 3 = 4K.
    /// Higher is sharper but costs more VRAM/GPU. Applied when the renderer bridge
    /// (re)starts, since the capture window is sized at launch.
    /// </summary>
    public int ScreenResolution { get; set; }

    /// <summary>
    /// Capture pipeline tuning for the renderer bridge: 0 = Default (balanced, vsync-capped),
    /// 1 = Smooth (extra frame buffering), 2 = High FPS (extra buffering + the browser's
    /// vsync/framerate cap removed so it paints as fast as it can). Higher modes cost more
    /// GPU. Applied when the renderer bridge (re)starts.
    /// </summary>
    public int CaptureMode { get; set; }

    /// <summary>
    /// Experimental: keep the capture window composited at full refresh rate (via DWM
    /// cloaking + top-most) instead of buried behind the game, to defeat the occlusion
    /// throttling that can cap capture fps. Invisible and click-free, but unproven on all
    /// setups — off by default. Applied when the renderer bridge (re)starts.
    /// </summary>
    public bool ForegroundCapture { get; set; }

    /// <summary>Debanding strength preset: 0 Off, 1 Low, 2 Medium, 3 High. Off by default.</summary>
    public int DebandMode { get; set; }

    /// <summary>Compression-artifact cleanup preset: 0 Off, 1 Low, 2 Medium, 3 High. Off by default.</summary>
    public int ArtifactMode { get; set; }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
