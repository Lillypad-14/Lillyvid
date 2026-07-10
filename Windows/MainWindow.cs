using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using VideoSyncPrototype.Emotes;
using VideoSyncPrototype.Fun;
using VideoSyncPrototype.Rendering;

namespace VideoSyncPrototype.Windows;

// Large window split across MainWindow.*.cs partial files: the Screen, Style and Settings tabs
// live in MainWindow.ScreenTab.cs / .StyleTab.cs / .Settings.cs. This file holds fields, the
// Video/Watch tabs, and the render-surface overlays.
public sealed partial class MainWindow : Window, IDisposable
{
    private const string DefaultStatus = "Ready. Paste a YouTube link on the Watch tab to get started.";
    private const double SoftSyncDriftThresholdSeconds = 0.35;
    private const double RunningSyncDriftThresholdSeconds = 1.25;
    private const double StartupSyncDriftThresholdSeconds = 0.8;
    private const double SnowHeartbeatSeconds = 30;
    private const double SnowUpdateDebounceSeconds = 1.5;
    private const int SyncTransportSnowcloak = 0;
    private const int SyncTransportCwls = 1;
    private static readonly string[] FrameStyleNames = ["None", "Classic wood", "Magitek", "Neon club", "Allagan"];
    private static readonly string[] CinemaPresetNames = ["Generic TV", "Cozy room", "Neon club", "Drive-in", "Allagan cinema"];
    private static readonly string[] UpscaleModeNames = ["Off (default)", "Fast", "Balanced", "Quality", "Ultra", "Custom"];
    private static readonly string[] UpscaleModeHints =
    [
        "The original look — plain bilinear, no processing.",
        "Bilinear with a light sharpen. Cheap crispness.",
        "Bicubic — softer, cleaner edges.",
        "Lanczos — sharp, detailed upscale.",
        "Lanczos plus a strong sharpen.",
        "Pick the filter and sharpen amount yourself.",
    ];
    private static readonly string[] UpscaleFilterNames = ["Bilinear", "Bicubic", "Lanczos"];
    private static readonly string[] ScreenResolutionNames = ["720p (default)", "1080p", "1440p", "4K"];
    private static readonly (int Width, int Height)[] ScreenResolutionSizes =
        [(1280, 720), (1920, 1080), (2560, 1440), (3840, 2160)];
    // Casual one-tap enhancement presets → (upscaleMode, debandMode, artifactMode). Balanced
    // so effects don't fight (e.g. heavy cleanup is never paired with max sharpen). Resolution
    // is deliberately NOT part of a preset — it stays its own separate control. A trailing
    // "Custom" name (with no preset entry) is shown when Advanced values match none of these.
    private static readonly string[] PicturePresetNames = ["Off", "Light", "Balanced", "Strong", "Ultra", "Custom"];
    private static readonly (int Upscale, int Deband, int Artifact)[] PicturePresets =
    [
        (0, 0, 0), // Off — raw source, no processing
        (2, 1, 0), // Light — bicubic upscale + gentle debanding
        (3, 2, 1), // Balanced — Lanczos + medium deband + light cleanup
        (4, 3, 2), // Strong — Lanczos+sharpen + high deband + medium cleanup
        (4, 3, 3), // Ultra — Lanczos+sharpen + high deband + high cleanup
    ];
    private static readonly string[] PicturePresetHints =
    [
        "No processing — the raw captured image.",
        "A light touch: cleaner upscale and gentle debanding.",
        "Recommended. Sharp upscale, medium debanding, light artifact cleanup.",
        "Heavier: high debanding and cleanup with a sharper upscale.",
        "Maximum cleanup. Best on rough/low-quality streams; can look over-processed on good ones.",
        "Custom — settings were hand-tuned in Advanced.",
    ];
    private static readonly string[] EnhanceModeNames = ["Off", "Low", "Medium", "High"];
    private static readonly float[] DebandStrengths = [0f, 0.4f, 0.7f, 1.0f];
    private static readonly float[] ArtifactStrengths = [0f, 0.3f, 0.55f, 0.8f];
    private static readonly string[] CaptureModeNames = ["Default", "Smooth"];
    private static readonly string[] CaptureModeHints =
    [
        "Balanced capture, synced to your refresh rate. Best default.",
        "Extra frame buffering to recover dropped frames. Slightly more latency.",
    ];

    private readonly string pluginDirectory;
    private readonly Configuration config;
    private string youtubeUrl = string.Empty;
    private bool creatingWatch2GetherRoom;
    private Task<Watch2GetherRoom>? watch2GetherCreateTask;
    private bool showApiKey;
    private bool apiKeyFieldActive;
    private bool forceOpenHostSetupSection = true;
    private bool forceOpenCreateRoomSection = true;
    private bool forceOpenJoinRoomSection = true;
    private string lastWatch2GetherRoomUrl = string.Empty;
    private string lastWatch2GetherRoomCode = string.Empty;
    private string pasteWatch2GetherRoomCode = string.Empty;
    private Watch2GetherRoom? incomingWatch2GetherRoom;
    private string incomingWatch2GetherRoomKey = string.Empty;
    private DateTime incomingWatch2GetherRoomUtc = DateTime.MinValue;
    private string lastOutboundWatch2GetherRoomKey = string.Empty;
    private DateTime lastOutboundWatch2GetherRoomUtc = DateTime.MinValue;
    private readonly HashSet<string> ignoredWatch2GetherRoomKeys = [];
    private string startDelayText = "10";
    private string offsetText = "0:00";
    private string syncshellText = "1";
    private int selectedCwlIndex;
    private int syncTransportIndex = SyncTransportCwls;
    private readonly List<SnowSyncshell> snowSyncshells = [];
    private int selectedSnowSyncshellIndex = -1;
    private string snowSyncshellStatus = "Not refreshed yet.";
    private DateTime lastSnowSyncshellRefreshUtc = DateTime.MinValue;
    private readonly List<SnowSyncBurst> pendingSnowSyncBursts = [];
    private bool hostSnowSync;
    private bool autoJoinSnowSync;
    private SyncPayload? discoveredParty;
    private string discoveredPartyCode = string.Empty;
    private DateTime discoveredPartyUtc = DateTime.MinValue;
    private string generatedCode = string.Empty;
    private string pasteCode = string.Empty;
    private string status = DefaultStatus;
    private string decodedSummary = string.Empty;
    private Process? rendererProcess;
    private DateTime rendererStartedUtc;
    private IDalamudTextureWrap? frameTexture;
    private Task<IDalamudTextureWrap>? frameTextureTask;
    private DateTime lastLoadedFrameWriteUtc;
    private DateTime lastSharedInfoWriteUtc;
    private nint sharedTextureHandle;
    private long controlSeq;
    private double playbackTime;
    private double playbackDuration;
    private bool playbackPaused = true;
    private bool playingWatch2GetherRoom;
    private DateTime lastStatusReadUtc;
    private DateTime pauseOverrideUntilUtc = DateTime.MinValue;
    private DateTime playbackTimeOverrideUntilUtc = DateTime.MinValue;
    private float seekDragValue = -1f;
    private float masterVolume = 0.7f;
    private bool audioMuted;
    private bool spatialAudio = true;
    private float audioRange = 30f;
    private float lastSentVolume = -1f;
    private float lastSentPan;
    private bool lastSentMuted;
    private DateTime lastAudioWriteUtc;
    private bool adBlockEnabled = true;
    private bool browserShown;
    private bool videoFullscreen;
    private string currentVideoId = string.Empty;
    private DateTime lastSnowSyncBroadcastUtc;
    private DateTime pendingSnowSyncBroadcastUtc;
    private string lastOutboundSnowSyncCode = string.Empty;
    private DateTime lastOutboundSnowSyncUtc;
    private int remoteSyncVersion;
    private bool worldScreenEnabled;
    private Vector3? worldScreenAnchor;
    private float worldScreenRotation;
    private float worldScreenWidth = 4f;
    private float worldScreenHeight = 2.25f;
    private bool worldScreenLockAspect = true;
    private float worldScreenElevation;
    private float worldScreenPush;
    private float worldScreenDistance = 3f;
    private float worldScreenHeightOffset = 1.6f;
    private bool drawImguiWorldScreen;
    private bool userChose2DFallback;
    private DateTime lastNativeInstallRetryUtc;
    private bool worldScreenActorOcclusion = true;
    private float worldScreenOcclusionPadding = 8f;
    private int tvFrameStyle;
    private bool ambientGlowEnabled;
    private Vector4 ambientGlowColor = new(0.42f, 0.78f, 1.0f, 0.35f);
    private float ambientGlowIntensity = 0.28f;
    private float ambientGlowSize = 0.42f;
    private float frameThickness = 0.16f;
    private int cinemaPresetIndex;
    private int upscaleMode;
    private int upscaleFilter;
    private float upscaleSharpness;
    private bool upscaleDebug;
    private int screenResolution;
    private int captureMode;
    private bool foregroundCapture;
    private int debandMode;
    private int artifactMode;
    private bool compareSplit;
    private string? lastRendererUrl;
    private string? lastRendererShareUrl;
    private long lastCaptureFrameCount;
    private DateTime lastCaptureFpsSampleUtc;
    private float captureFps;
    private string realRendererProbe = "Not probed yet.";
    private readonly PresentHookProbe presentHookProbe = new();
    private readonly Phone.Apps.LillypadGo.FollowerRenderer pokemonFollower = new();
    private readonly PlayerSearch.PlayerSearchTab playerSearchTab = new();
    private readonly EmoteRemapperTab emoteRemapperTab;
    private readonly FunTab funTab;
    private readonly Inventory.InventoryTab inventoryTab;
    private readonly Inventory.ItemSearchTab itemSearchTab = new();
    private Phone.PhoneScreen? phoneScreen;
    private bool gamesTabActive;
    private readonly WindowSizeConstraints? defaultConstraints;

    public VideoSurfaceWindow SurfaceWindow { get; }

    internal MainWindow(string pluginDirectory, Configuration config, EmoteRemapperService emoteRemapperService)
        : base("Lillypad Toolkit###VideoSyncPrototypeMain")
    {
        this.pluginDirectory = pluginDirectory;
        this.config = config;
        this.emoteRemapperTab = new EmoteRemapperTab(config, emoteRemapperService);
        this.funTab = new FunTab(config);
        this.inventoryTab = new Inventory.InventoryTab(config);
        this.adBlockEnabled = config.AdBlockEnabled;
        this.upscaleMode = Math.Clamp(config.UpscaleMode, 0, UpscaleModeNames.Length - 1);
        this.upscaleFilter = Math.Clamp(config.UpscaleFilter, 0, UpscaleFilterNames.Length - 1);
        this.upscaleSharpness = Math.Clamp(config.UpscaleSharpness, 0f, 1f);
        this.upscaleDebug = config.UpscaleDebugOverlay;
        this.screenResolution = Math.Clamp(config.ScreenResolution, 0, ScreenResolutionNames.Length - 1);
        this.captureMode = Math.Clamp(config.CaptureMode, 0, CaptureModeNames.Length - 1);
        this.foregroundCapture = config.ForegroundCapture;
        this.debandMode = Math.Clamp(config.DebandMode, 0, EnhanceModeNames.Length - 1);
        this.artifactMode = Math.Clamp(config.ArtifactMode, 0, EnhanceModeNames.Length - 1);
        this.SurfaceWindow = new VideoSurfaceWindow(this);
        this.defaultConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(470, 440),
            MaximumSize = new Vector2(1000, 1100),
        };
        this.SizeConstraints = this.defaultConstraints;
    }

    public void Dispose()
    {
        this.DisposeNetworkSync();
        this.presentHookProbe.Dispose();
        this.inventoryTab.Dispose();
        this.phoneScreen?.Dispose();
        this.StopInWindowPreview();
    }

    public override void PreDraw()
    {
        var cfg = Phone.Plugin.Cfg;
        var standalone = cfg?.StandaloneMode == true;
        // The phone is on screen either as the Games tab, or as the whole (standalone) window.
        var phoneShown = standalone || this.gamesTabActive;
        var flags = ImGuiWindowFlags.None;

        // Lock engaged: stop click-and-drag inside the phone from dragging the window.
        if (phoneShown && cfg?.LockPosition == true)
        {
            flags |= ImGuiWindowFlags.NoMove;
        }

        if (standalone)
        {
            // Drop all window chrome and size the window to the phone, so only the phone shows.
            flags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar |
                     ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground |
                     ImGuiWindowFlags.NoCollapse;
            this.SizeConstraints = null;
            this.Size = Phone.Core.Theme.PhoneSizeCatalog.SizeFor(cfg!.PhoneScale);
            this.SizeCondition = ImGuiCond.Always;
        }
        else
        {
            this.SizeConstraints = this.defaultConstraints;
            this.SizeCondition = ImGuiCond.FirstUseEver;
        }

        this.Flags = flags;
        base.PreDraw();
    }

    public override void Draw()
    {
        // Standalone mode: no tabs, no chrome — just the phone filling the (phone-sized) window.
        if (Phone.Plugin.Cfg?.StandaloneMode == true)
        {
            this.phoneScreen ??= new Phone.PhoneScreen(Phone.Plugin.Cfg);
            this.DrawPhoneFramed();
            return;
        }

        using var theme = UiTheme.PushWindowStyle();

        this.TickRendererHealth();
        this.TickWatch2GetherRoomCreation();
        this.TickSnowSyncBroadcast();
        var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;

        // Pinned above the tabs so it's reachable from any tab while a video plays.
        if (running)
        {
            this.DrawTopMiniBrowserBar();
            ImGui.Spacing();
        }

        var showStatusBar = !string.Equals(this.status, DefaultStatus, StringComparison.Ordinal);
        var footerHeight = showStatusBar
            ? (ImGui.GetTextLineHeightWithSpacing() * 2f) + ImGui.GetStyle().ItemSpacing.Y
            : 0f;
        ImGui.BeginChild("##videosync-body", new Vector2(0f, -footerHeight));

        // Top-level features live side by side here. Each existing video screen stays a
        // sub-tab under "Video"; new features (Player Search, and anything later) get their
        // own top-level tab so the layout scales without touching the video UI.
        if (ImGui.BeginTabBar("##videosync-top-tabs"))
        {
            if (ImGui.BeginTabItem("Video"))
            {
                this.DrawVideoTabs(running);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Player Search"))
            {
                this.playerSearchTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Emote Remapper"))
            {
                this.emoteRemapperTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Inventory"))
            {
                this.inventoryTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Item"))
            {
                this.itemSearchTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Fun"))
            {
                this.funTab.Draw();
                ImGui.EndTabItem();
            }

            var gamesTabOpen = ImGui.BeginTabItem("Games");
            this.gamesTabActive = gamesTabOpen;
            if (gamesTabOpen)
            {
                this.DrawGamesTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.EndChild();
        if (showStatusBar)
        {
            this.DrawStatusBar(running);
        }
    }

    // The original four video tabs, unchanged — just nested under the "Video" top-level tab.
    private void DrawVideoTabs(bool running)
    {
        if (ImGui.BeginTabBar("##videosync-tabs"))
        {
            if (ImGui.BeginTabItem("Watch"))
            {
                this.DrawWatchTab(running);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Nearby Sync"))
            {
                this.DrawNearbySyncTab(running);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Screen"))
            {
                this.DrawScreenTab(running);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Style"))
            {
                this.DrawStyleTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                this.DrawSettingsTab(running);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // Hosts the vendored Aetherphone phone shell (home screen + 16 mini-games). The phone
    // renders at its native portrait aspect, centred in whatever space the tab is given.
    private void DrawGamesTab()
    {
        this.phoneScreen ??= new Phone.PhoneScreen(Phone.Plugin.Cfg);
        this.DrawPhoneFramed();
    }

    // Draws the phone at its native portrait aspect, centred in the current content region.
    // In standalone mode the window is already phone-sized, so the phone fills it.
    private void DrawPhoneFramed()
    {
        if (this.phoneScreen is null)
        {
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(avail);
        if (avail.X < 40f || avail.Y < 40f)
        {
            return;
        }

        var native = Phone.Core.Theme.PhoneSizeCatalog.SizeFor(Phone.Plugin.Cfg.PhoneScale);
        var aspect = native.X / native.Y;
        var height = avail.Y;
        var width = height * aspect;
        if (width > avail.X)
        {
            width = avail.X;
            height = width / aspect;
        }

        var min = new Vector2(origin.X + ((avail.X - width) * 0.5f), origin.Y + ((avail.Y - height) * 0.5f));
        this.phoneScreen.Draw(new Phone.Core.Rect(min, min + new Vector2(width, height)));
    }

    private void DrawWatchTab(bool running)
    {
        ImGui.Spacing();

        // If a friend just shared a room with us, that offer jumps to the top.
        this.DrawWatch2GetherRoomOffer();
        this.DrawDiscoveredPartyOffer();

        // Creating (hosting) needs a Watch2Gether key; JOINING never does. So the
        // create area gates on the key, but Join is always shown below it — a random
        // user with no key can still open a friend's room.
        //
        // The key input saves on every keystroke, which flips this branch the instant
        // the first character lands. If we let that swap the layout mid-edit, the input
        // widget is unmounted and ImGui drops keyboard focus — the "can only type one
        // letter" bug. So while the key field is being edited we keep the setup section
        // mounted no matter what, and only reveal the create-room UI once focus leaves it.
        if (string.IsNullOrWhiteSpace(this.config.Watch2GetherApiKey) || this.apiKeyFieldActive)
        {
            if (UiTheme.BeginCollapsibleSection("Host setup", defaultOpen: true, forceOpen: this.forceOpenHostSetupSection))
            {
                this.DrawHostSetupSection();
                ImGui.TreePop();
            }

            this.forceOpenHostSetupSection = false;
        }
        else
        {
            if (UiTheme.BeginCollapsibleSection("Create movie room", defaultOpen: true, primary: true, forceOpen: this.forceOpenCreateRoomSection))
            {
                this.DrawCreateRoomSection();
                ImGui.TreePop();
            }

            this.forceOpenCreateRoomSection = false;

            if (!string.IsNullOrWhiteSpace(this.lastWatch2GetherRoomCode))
            {
                ImGui.Spacing();
                if (UiTheme.BeginCollapsibleSection("Current share code", defaultOpen: true, primary: true))
                {
                    this.DrawCurrentShareCode();
                    ImGui.TreePop();
                }
            }
        }

        ImGui.Spacing();
        if (UiTheme.BeginCollapsibleSection("Join a room", defaultOpen: true, primary: true, forceOpen: this.forceOpenJoinRoomSection))
        {
            this.DrawJoinRoomSection();
            ImGui.TreePop();
        }

        this.forceOpenJoinRoomSection = false;

        if (running)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Playback, volume, and screen controls are on the Screen tab.");
        }
    }

    // A persistent, always-visible action pinned above the tabs whenever a video is
    // running, so bringing the player on screen (to click past ads / consent / sign-ins)
    // is one click away no matter which tab you're on. The overlay's Done button sends
    // itself back, so this intentionally does not become a second "Hide" control.
    private void DrawTopMiniBrowserBar()
    {
        var pressed = UiTheme.QuietButton(
            "Show mini-browser",
            new Vector2(-1f, 30f));

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("The video runs in a hidden browser window. Show it whenever a video is stuck on a consent screen, ad, sign-in, or age check, then press Done to send it back.");
        }

        if (pressed)
        {
            this.AllowRendererForeground();
            this.SendPlaybackCommand("show");
            this.status = "Mini-browser is on screen. Click through any consent dialog, ad, or sign-in, then press Done.";
        }
    }

    private void DrawCreateRoomSection()
    {
        ImGui.TextColored(UiTheme.AccentHovered, "Paste a video link, then create a shared room.");
        ImGui.TextDisabled("YouTube, Vimeo, Twitch, or a direct video file.");
        ImGui.Spacing();

        const float buttonWidth = 112f;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        var submitted = ImGui.InputTextWithHint(
            "##videosync-url",
            "https://www.youtube.com/watch?v=...",
            ref this.youtubeUrl,
            512,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        var busy = this.creatingWatch2GetherRoom;
        if (busy)
        {
            ImGui.BeginDisabled();
        }

        var pressed = UiTheme.PrimaryButton(busy ? "Creating..." : "Create", new Vector2(buttonWidth, 0f));
        if (busy)
        {
            ImGui.EndDisabled();
        }

        if ((pressed || submitted) && !busy)
        {
            this.CreateMovieRoom();
        }

        ImGui.Spacing();
        if (ImGui.SmallButton("Play just for me"))
        {
            this.PlayUrlInWorld(this.youtubeUrl);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Play this link on your screen only, without creating a shared room.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Open browser"))
        {
            this.OpenFreshBrowserOnScreen(this.youtubeUrl);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Open a normal browser page on the in-world TV. If you are hosting a Nearby Sync shell, friends will follow its navigation.");
        }

        ImGui.SameLine();
        ImGui.TextDisabled(" | ");
        ImGui.SameLine();
        if (ImGui.Checkbox("Block ads", ref this.adBlockEnabled))
        {
            this.config.AdBlockEnabled = this.adBlockEnabled;
            this.config.Save();
        }
    }

    private void DrawCurrentShareCode()
    {
        ImGui.TextDisabled("Carries the room and your exact screen layout. Friends paste it into Join, or open the link in any browser.");
        ImGui.Spacing();

        // Rebuild from the live layout each frame so the code always matches the
        // TV as it stands now — the screen isn't placed yet when the room is first
        // created, and the host may nudge/resize it before sharing.
        this.lastWatch2GetherRoomCode = this.BuildRoomShareCode(new Watch2GetherRoom(this.lastWatch2GetherRoomUrl));

        const float buttonWidth = 96f;
        var fieldWidth = Math.Max(120f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);

        ImGui.SetNextItemWidth(fieldWidth);
        var code = this.lastWatch2GetherRoomCode;
        ImGui.InputText("##videosync-w2g-code", ref code, 1024, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (UiTheme.PrimaryButton("Copy code", new Vector2(buttonWidth, 0f)))
        {
            ImGui.SetClipboardText(this.lastWatch2GetherRoomCode);
            this.status = "Share code copied.";
        }

        ImGui.SetNextItemWidth(fieldWidth);
        var url = this.lastWatch2GetherRoomUrl;
        ImGui.InputText("##videosync-w2g-url", ref url, 1024, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGui.Button("Copy link", new Vector2(buttonWidth, 0f)))
        {
            ImGui.SetClipboardText(this.lastWatch2GetherRoomUrl);
            this.status = "Room link copied.";
        }

        ImGui.Spacing();
        var halfWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        if (UiTheme.PrimaryButton("Open on screen", new Vector2(halfWidth, 0f)))
        {
            this.PlayWatch2GetherRoomInWorld(new Watch2GetherRoom(this.lastWatch2GetherRoomUrl));
        }

        ImGui.SameLine();
        if (ImGui.Button("Open on the web", new Vector2(-1f, 0f)))
        {
            this.OpenWatch2GetherRoom(new Watch2GetherRoom(this.lastWatch2GetherRoomUrl));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Open this room in your desktop web browser (Chrome, Edge...) instead of the in-world screen.");
        }
    }

    private void DrawJoinRoomSection()
    {
        ImGui.TextColored(UiTheme.AccentHovered, "Paste a friend's room code or W2G link.");
        ImGui.Spacing();

        const float buttonWidth = 78f;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        var joinPressed = ImGui.InputTextWithHint(
            "##videosync-w2g-join-code",
            "Paste W2G code or room link...",
            ref this.pasteWatch2GetherRoomCode,
            1024,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        if (UiTheme.PrimaryButton("Join", new Vector2(buttonWidth, 0f)) || joinPressed)
        {
            this.OpenPastedWatch2GetherRoom();
        }
    }

    private void DrawHostSetupSection()
    {
        ImGui.TextWrapped("Hosting needs a free Watch2Gether key (one-time). Joining a friend's room doesn't.");
        ImGui.Spacing();
        ImGui.BulletText("Log in at w2g.tv, open Edit Profile.");
        ImGui.BulletText("Under \"API Access\", click New.");
        ImGui.BulletText("Paste the key below.");
        ImGui.Spacing();

        if (ImGui.Button("Open Watch2Gether"))
        {
            this.OpenUrl("https://w2g.tv/");
        }

        ImGui.Spacing();
        this.DrawApiKeyInput();
    }

    private void DrawApiKeyInput()
    {
        var key = this.config.Watch2GetherApiKey;
        var flags = this.showApiKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        ImGui.SetNextItemWidth(-1f);

        // Persist on every edit (including paste): waiting only for the field to lose
        // focus was unreliable — a user could paste the key and immediately click
        // Create without ever deactivating the input, so it never reached disk.
        if (ImGui.InputTextWithHint("##videosync-w2g-apikey", "Paste your Watch2Gether API key", ref key, 128, flags))
        {
            this.config.Watch2GetherApiKey = key.Trim();
            this.config.Save();
            this.status = "Watch2Gether API key saved.";
        }

        // Track focus so DrawWatchTab won't unmount this field (and steal keyboard
        // focus) between keystrokes as the saved key toggles the tab's layout branch.
        this.apiKeyFieldActive = ImGui.IsItemActive();

        ImGui.Checkbox("Show key", ref this.showApiKey);
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            this.status = $"Couldn't open the browser: {ex.Message}";
        }
    }

    private void DrawWatch2GetherRoomOffer()
    {
        if (this.incomingWatch2GetherRoom is not { } room)
        {
            return;
        }

        UiTheme.SectionTitle("Shared video room");
        ImGui.TextWrapped("A shared video room was created. Open it?");
        ImGui.TextDisabled(room.NormalizedUrl);

        if (UiTheme.PrimaryButton("Open room"))
        {
            this.PlayWatch2GetherRoomInWorld(room);
            this.ClearIncomingWatch2GetherRoom();
        }

        ImGui.SameLine();
        if (ImGui.Button("On the web"))
        {
            this.OpenWatch2GetherRoom(room);
            this.ClearIncomingWatch2GetherRoom();
        }

        ImGui.SameLine();
        if (ImGui.Button("Ignore"))
        {
            this.ignoredWatch2GetherRoomKeys.Add(this.incomingWatch2GetherRoomKey);
            this.ClearIncomingWatch2GetherRoom();
            this.status = "Ignored the shared Watch2Gether room.";
        }

        if (DateTime.UtcNow - this.incomingWatch2GetherRoomUtc > TimeSpan.FromMinutes(30))
        {
            this.ClearIncomingWatch2GetherRoom();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawDiscoveredPartyOffer()
    {
        if (this.discoveredParty is not { } party)
        {
            return;
        }

        // Hosts re-broadcast every 30 seconds; three misses means the party ended.
        if (DateTime.UtcNow - this.discoveredPartyUtc > TimeSpan.FromSeconds(90))
        {
            return;
        }

        UiTheme.SectionTitle("Watch party found");
        var target = FormatTimestamp(Math.Max(0, party.GetCurrentVideoSeconds()));
        ImGui.TextWrapped(party.PlaybackRate == 0
            ? $"Someone in your linkshell has a video paused at {target}. Joining spawns their screen for you."
            : $"Someone in your linkshell is watching a video (now at {target}). Joining spawns their screen for you.");

        if (UiTheme.PrimaryButton("Join watch party"))
        {
            this.ReceiveSnowSync(this.discoveredPartyCode, force: true);
        }

        ImGui.SameLine();
        if (ImGui.Button("Ignore"))
        {
            this.ClearDiscoveredParty();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawPlaybackSection()
    {
        this.TryUpdatePlaybackStatus();
        this.TryUpdateFrameTexture();

        if (this.hostSnowSync)
        {
            const string hostingLabel = "HOSTING";
            var hostingPad = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(hostingLabel).X;
            if (hostingPad > 0f)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + hostingPad);
            }

            ImGui.TextColored(UiTheme.Accent, hostingLabel);
        }

        ImGui.Spacing();
        // A room's video lives in w2g's synced player, so its transport works differently
        // from a direct "Play just for me" video (which exposes a real <video> we can seek).
        if (this.playingWatch2GetherRoom)
        {
            this.DrawRoomTransport();
        }
        else
        {
            this.DrawTransportControls();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(this.videoFullscreen ? "Exit fullscreen" : "Fullscreen"))
        {
            this.SetVideoFullscreen(!this.videoFullscreen);
            this.QueueSnowSyncBroadcast();
            this.status = this.videoFullscreen
                ? "The video now fills the whole screen surface."
                : "Returned the video to the normal page view.";
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Make the video fill the whole in-world screen instead of showing the web page around it.");
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("Block ads", ref this.adBlockEnabled))
        {
            this.config.AdBlockEnabled = this.adBlockEnabled;
            this.config.Save();
            this.SendPlaybackCommand("adblock", this.adBlockEnabled ? 1 : 0);
            this.QueueSnowSyncBroadcast();
            this.status = this.adBlockEnabled ? "Ad blocking is on." : "Ad blocking is off.";
        }

        ImGui.SameLine();
        const float stopWidth = 130f;
        var stopPad = ImGui.GetContentRegionAvail().X - stopWidth;
        if (stopPad > 0f)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + stopPad);
        }

        if (UiTheme.DangerButton("Stop screen", new Vector2(stopWidth, 0f)))
        {
            this.StopPlayback();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Stop playback and hide the in-world screen.");
        }
    }

    private void DrawRoomTransport()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Watch2Gether controls this room");
    }

    private void DrawTransportControls()
    {
        var estimatedTime = this.GetEstimatedPlaybackTime();
        var maxTime = (float)Math.Max(1, this.playbackDuration);
        var seekValue = this.seekDragValue >= 0f ? this.seekDragValue : (float)estimatedTime;
        UiTheme.PushSliderAccent();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##videosync-seek", ref seekValue, 0f, maxTime, FormatTimestamp(seekValue)))
        {
            this.seekDragValue = seekValue;
        }

        UiTheme.PopSliderAccent();
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            if (this.seekDragValue >= 0f)
            {
                this.SendPlaybackCommand("seek", this.seekDragValue);
                this.SetOptimisticPlaybackTime(this.seekDragValue);
                this.QueueSnowSyncBroadcast();
            }

            this.seekDragValue = -1f;
        }

        if (UiTheme.IconButton(
                this.playbackPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause,
                "videosync-playpause",
                new Vector2(38f, 0f),
                this.playbackPaused ? "Play" : "Pause",
                primary: true))
        {
            var wantPause = !this.playbackPaused;
            this.SendPlaybackCommand(wantPause ? "pause" : "play");

            // Flip the UI immediately; the status file lags by up to a second.
            this.playbackPaused = wantPause;
            this.pauseOverrideUntilUtc = DateTime.UtcNow.AddSeconds(1.5);
            this.QueueSnowSyncBroadcast();
        }

        ImGui.SameLine();
        if (ImGui.Button("-10s"))
        {
            this.SendPlaybackCommand("nudge", -10);
            this.SetOptimisticPlaybackTime(Math.Max(0, estimatedTime - 10));
            this.QueueSnowSyncBroadcast();
        }

        ImGui.SameLine();
        if (ImGui.Button("+10s"))
        {
            this.SendPlaybackCommand("nudge", 10);
            this.SetOptimisticPlaybackTime(estimatedTime + 10);
            this.QueueSnowSyncBroadcast();
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"{FormatTimestamp(estimatedTime)} / {FormatTimestamp(this.playbackDuration)}");
    }

    private void DrawAudioControls()
    {
        if (UiTheme.IconButton(
                this.audioMuted ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp,
                "videosync-mute",
                new Vector2(32f, 0f),
                this.audioMuted ? "Unmute" : "Mute"))
        {
            this.audioMuted = !this.audioMuted;
            this.QueueSnowSyncBroadcast();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f);
        UiTheme.PushSliderAccent();
        if (ImGui.SliderFloat("##videosync-volume", ref this.masterVolume, 0f, 1f, $"{this.masterVolume * 100f:0}%%"))
        {
            this.QueueSnowSyncBroadcast();
        }

        UiTheme.PopSliderAccent();
        ImGui.SameLine();
        if (ImGui.Checkbox("3D audio", ref this.spatialAudio))
        {
            this.QueueSnowSyncBroadcast();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Volume fades with distance from the screen and pans left or right as you move.");
        }

        if (this.spatialAudio)
        {
            ImGui.SetNextItemWidth(-1);
            UiTheme.PushSliderAccent();
            if (ImGui.SliderFloat("##videosync-range", ref this.audioRange, 5f, 100f, "fades at %.0f yalms away"))
            {
                this.QueueSnowSyncBroadcast();
            }

            UiTheme.PopSliderAccent();
        }
    }

    private void DrawCardPreviewImage()
    {
        this.TryUpdateFrameTexture();
        var width = Math.Min(ImGui.GetContentRegionAvail().X, 520f);
        var size = new Vector2(width, width * 9f / 16f);
        if (this.frameTexture is not null)
        {
            ImGui.Image(this.frameTexture.Handle, size);
            return;
        }

        // The renderer is up but hasn't streamed its first frame yet (the browser is
        // still warming up). Show an animated loading state instead of a blank gap so
        // the video reads as "loading", not "broken".
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        this.DrawLoadingSurface(cursor, size);
    }

    // Animated placeholder drawn over a video surface while we wait for the first
    // browser frame. Kept to confirmed draw-list calls so it works with this pinned
    // (pre-1.90) ImGui binding.
    private void DrawLoadingSurface(Vector2 cursor, Vector2 size)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(cursor, cursor + size, 0xFF0A0A0A, 4f);
        drawList.AddRect(cursor, cursor + size, 0xAA2A2A2A, 4f);

        var center = cursor + (size * 0.5f);
        var orbit = Math.Clamp(MathF.Min(size.X, size.Y) * 0.09f, 10f, 24f);
        var dotRadius = Math.Clamp(orbit * 0.16f, 2f, 4f);
        var t = (float)ImGui.GetTime();
        const int dots = 8;
        for (var i = 0; i < dots; i++)
        {
            var angle = (t * 2.4f) - (i * (MathF.PI * 2f / dots));
            var pos = center + (new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * orbit);
            var fade = 0.2f + (0.8f * (i / (float)(dots - 1)));
            drawList.AddCircleFilled(pos, dotRadius, ImGui.ColorConvertFloat4ToU32(UiTheme.Accent with { W = fade }));
        }

        var label = "Loading video…";
        var labelSize = ImGui.CalcTextSize(label);
        var labelPos = new Vector2(center.X - (labelSize.X * 0.5f), center.Y + orbit + 8f);
        drawList.AddText(labelPos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 0.85f, 0.88f, 1f)), label);

        var elapsed = this.rendererStartedUtc == DateTime.MinValue
            ? 0d
            : (DateTime.UtcNow - this.rendererStartedUtc).TotalSeconds;
        if (elapsed <= 6d)
        {
            return;
        }

        var hint = "First run warms up the player — hang tight.";
        var hintSize = ImGui.CalcTextSize(hint);
        if (hintSize.X < size.X - 16f)
        {
            var hintPos = new Vector2(center.X - (hintSize.X * 0.5f), labelPos.Y + labelSize.Y + 4f);
            drawList.AddText(hintPos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.55f, 0.58f, 1f)), hint);
        }
    }

    private void DrawStatusBar(bool running)
    {
        ImGui.Separator();
        var color = !running ? UiTheme.Idle : this.hostSnowSync ? UiTheme.Accent : UiTheme.Live;
        UiTheme.StatusDot(color);
        ImGui.SameLine();
        ImGui.TextWrapped(this.status);
    }

    private string BuildTechStatusLine()
    {
        var renderer = this.rendererProcess is not null && !this.rendererProcess.HasExited
            ? "renderer: on"
            : "renderer: off";
        var transport = this.GetSyncTransportLabel();
        var snow = this.hostSnowSync
            ? $"{transport}: host/{SnowHeartbeatSeconds:0}s"
            : this.autoJoinSnowSync ? $"{transport}: auto-join" : $"{transport}: manual";
        var tx = this.lastSnowSyncBroadcastUtc == DateTime.MinValue
            ? "last tx: never"
            : $"last tx: {(DateTime.UtcNow - this.lastSnowSyncBroadcastUtc).TotalSeconds:0}s";
        var screen = this.worldScreenEnabled ? "screen: on" : "screen: off";
        return $"{renderer} | {screen} | {snow} | {tx}";
    }

    private void StartHostingWatchParty()
    {
        if (this.rendererProcess is null || this.rendererProcess.HasExited)
        {
            this.PlayUrlInWorld(this.youtubeUrl);
        }

        var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;
        if (!running)
        {
            return;
        }

        this.hostSnowSync = true;
        this.SetVideoFullscreen(true);
        this.BroadcastCurrentSnowSync("host");
        this.status = $"Hosting watch party through {this.GetSyncTransportLabel()}.";
    }

    private void DrawLegacyPartySync(bool running)
    {
        ImGui.TextWrapped("Broadcasts the in-world screen's video and timing over a chat channel. Superseded by Watch2Gether rooms on the Watch tab — kept only for the old code/syncshell workflow.");
        ImGui.Spacing();

        this.DrawSyncTransportPicker();
        ImGui.Spacing();

        ImGui.Checkbox("Join watch parties automatically (skip the prompt)", ref this.autoJoinSnowSync);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Off by default: incoming parties show a Join offer on the Watch tab instead of taking over your game.");
        }
        ImGui.Checkbox("Host: keep the party in sync", ref this.hostSnowSync);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("While hosting, your playback is re-shared every 30 seconds and whenever you pause, seek, or move the screen.");
        }

        ImGui.Spacing();
        if (running)
        {
            if (UiTheme.PrimaryButton("Broadcast current video now"))
            {
                this.BroadcastCurrentSnowSync("manual");
            }
        }
        else
        {
            ImGui.TextDisabled("Start a video on the Watch tab to broadcast it.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Test channel"))
        {
            this.SendSyncTransportTest();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Share with a code instead"))
        {
            this.DrawShareCodeSection();
        }
    }

    private void DrawShareCodeSection()
    {
        ImGui.TextWrapped("No syncshell? A code carries the video, timing, screen placement, and audio settings. Send it to a friend any way you like.");
        ImGui.Spacing();

        UiTheme.SectionTitle("Create a code");
        ImGui.SetNextItemWidth(-140f);
        ImGui.InputText("YouTube link", ref this.youtubeUrl, 512);
        ImGui.SetNextItemWidth(90f);
        ImGui.InputText("Starts in (seconds)", ref this.startDelayText, 16);
        ImGui.SetNextItemWidth(90f);
        ImGui.InputText("Skip to (m:ss)", ref this.offsetText, 32);

        if (ImGui.Button("Generate code"))
        {
            this.GenerateCode();
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy") && !string.IsNullOrWhiteSpace(this.generatedCode))
        {
            ImGui.SetClipboardText(this.generatedCode);
            this.status = "Copied the code. Paste it to your friends.";
        }

        ImGui.SameLine();
        if (ImGui.Button("Send to channel") && !string.IsNullOrWhiteSpace(this.generatedCode))
        {
            this.ShareViaSnowcloak(this.generatedCode);
        }

        if (!string.IsNullOrWhiteSpace(this.generatedCode))
        {
            ImGui.InputTextMultiline("##videosync-generated", ref this.generatedCode, 4096, new Vector2(-1, 60), ImGuiInputTextFlags.ReadOnly);
        }

        ImGui.Spacing();
        UiTheme.SectionTitle("Join with a code");
        ImGui.InputTextMultiline("##videosync-paste", ref this.pasteCode, 4096, new Vector2(-1, 60));

        if (UiTheme.PrimaryButton("Join video"))
        {
            if (TryDecode(this.pasteCode, out var payload, out var error))
            {
                this.ApplyRemoteSync(payload, "shared code");
            }
            else
            {
                this.status = error;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Preview details"))
        {
            this.DecodePaste();
        }

        ImGui.SameLine();
        if (ImGui.Button("Open in browser"))
        {
            if (TryDecode(this.pasteCode, out var payload, out var error))
            {
                this.OpenInDefaultBrowser(payload);
                this.status = "Opened the shared video in your browser.";
            }
            else
            {
                this.status = error;
            }
        }

        if (!string.IsNullOrWhiteSpace(this.decodedSummary))
        {
            ImGui.TextWrapped(this.decodedSummary);
        }
    }

    private void GenerateCode()
    {
        if (!TryExtractYouTubeId(this.youtubeUrl, out var videoId))
        {
            this.status = "Could not find a YouTube video id in that URL.";
            return;
        }

        if (!double.TryParse(this.startDelayText, out var delaySeconds))
        {
            this.status = "Start delay must be a number of seconds.";
            return;
        }

        if (!TryParseTimestamp(this.offsetText, out var offsetSeconds))
        {
            this.status = "Offset should look like 0:00, 1:23, 1:02:03, or plain seconds.";
            return;
        }

        var payload = new SyncPayload(
            videoId,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, delaySeconds)).ToUnixTimeSeconds(),
            offsetSeconds,
            1.0,
            this.CreateCurrentSyncScreen(),
            this.CreateCurrentSyncAudio(),
            this.CreateCurrentSyncOptions());

        this.generatedCode = SyncCode.Encode(payload);
        this.decodedSummary = FormatSummary(payload);
        this.status = "Generated code. Share it with someone, then they can paste and open it.";
    }

    private void DecodePaste()
    {
        if (!TryDecode(this.pasteCode, out var payload, out var error))
        {
            this.decodedSummary = string.Empty;
            this.status = error;
            return;
        }

        this.decodedSummary = FormatSummary(payload);
        this.status = "Decoded pasted code.";
    }

    private void DrawSyncTransportPicker()
    {
        var transportPreview = this.syncTransportIndex == SyncTransportCwls
            ? "CWLS"
            : "Snowcloak";
        ImGui.SetNextItemWidth(160f);
        if (ImGui.BeginCombo("Transport", transportPreview))
        {
            if (ImGui.Selectable("CWLS", this.syncTransportIndex == SyncTransportCwls))
            {
                this.syncTransportIndex = SyncTransportCwls;
            }

            if (ImGui.Selectable("Snowcloak", this.syncTransportIndex == SyncTransportSnowcloak))
            {
                this.syncTransportIndex = SyncTransportSnowcloak;
            }

            ImGui.EndCombo();
        }

        if (this.syncTransportIndex == SyncTransportCwls)
        {
            var preview = FormatCwlsLabel(this.selectedCwlIndex);
            ImGui.SetNextItemWidth(Math.Max(180f, ImGui.GetContentRegionAvail().X - 170f));
            if (ImGui.BeginCombo("Cross-world linkshell", preview))
            {
                for (var i = 0; i < GameChat.CrossworldLinkshellSlots; i++)
                {
                    var selected = i == this.selectedCwlIndex;
                    if (ImGui.Selectable(FormatCwlsLabel(i), selected))
                    {
                        this.selectedCwlIndex = i;
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled("Syncs go out as /cwlinkshell chat messages that everyone's plugin picks up automatically.");
            ImGui.PopTextWrapPos();
            return;
        }

        this.DrawSnowSyncshellPicker();
    }

    private void DrawSnowSyncshellPicker()
    {
        if (this.lastSnowSyncshellRefreshUtc == DateTime.MinValue)
        {
            this.RefreshSnowSyncshells();
        }

        var preview = this.GetSelectedSnowSyncshellLabel();
        ImGui.SetNextItemWidth(Math.Max(180f, ImGui.GetContentRegionAvail().X - 90f));
        if (ImGui.BeginCombo("Snowcloak syncshell", preview))
        {
            for (var i = 0; i < this.snowSyncshells.Count; i++)
            {
                var shell = this.snowSyncshells[i];
                var selected = i == this.selectedSnowSyncshellIndex;
                var label = FormatSnowSyncshellLabel(shell);
                if (ImGui.Selectable(label, selected))
                {
                    this.selectedSnowSyncshellIndex = i;
                    this.syncshellText = shell.ShellNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            if (this.snowSyncshells.Count == 0)
            {
                ImGui.TextDisabled("No syncshells found.");
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
        {
            this.RefreshSnowSyncshells();
        }

        ImGui.SetNextItemWidth(100f);
        if (ImGui.InputText("Manual /ss#", ref this.syncshellText, 4))
        {
            this.selectedSnowSyncshellIndex = this.FindSnowSyncshellIndex(this.syncshellText);
        }

        if (!string.IsNullOrWhiteSpace(this.snowSyncshellStatus))
        {
            ImGui.TextDisabled(this.snowSyncshellStatus);
        }
    }

}
