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
using VideoSyncPrototype.Rendering;

namespace VideoSyncPrototype.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private const double SoftSyncDriftThresholdSeconds = 0.35;
    private const double RunningSyncDriftThresholdSeconds = 1.25;
    private const double StartupSyncDriftThresholdSeconds = 0.8;
    private const double SnowHeartbeatSeconds = 30;
    private const double SnowUpdateDebounceSeconds = 1.5;
    private const int SyncTransportSnowcloak = 0;
    private const int SyncTransportCwls = 1;
    private static readonly string[] FrameStyleNames = ["None", "Classic wood", "Magitek", "Neon club", "Allagan"];
    private static readonly string[] CinemaPresetNames = ["Generic TV", "Cozy room", "Neon club", "Drive-in", "Allagan cinema"];

    private readonly string pluginDirectory;
    private readonly Configuration config;
    private string youtubeUrl = string.Empty;
    private bool creatingWatch2GetherRoom;
    private Task<Watch2GetherRoom>? watch2GetherCreateTask;
    private bool showApiKey;
    private bool apiKeyFieldActive;
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
    private string status = "Ready. Paste a YouTube link on the Watch tab to get started.";
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
    private string realRendererProbe = "Not probed yet.";
    private readonly PresentHookProbe presentHookProbe = new();

    public VideoSurfaceWindow SurfaceWindow { get; }

    public MainWindow(string pluginDirectory, Configuration config)
        : base("VideoSync###VideoSyncPrototypeMain")
    {
        this.pluginDirectory = pluginDirectory;
        this.config = config;
        this.adBlockEnabled = config.AdBlockEnabled;
        this.SurfaceWindow = new VideoSurfaceWindow(this);
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(470, 440),
            MaximumSize = new Vector2(1000, 1100),
        };
    }

    public void Dispose()
    {
        this.presentHookProbe.Dispose();
        this.StopInWindowPreview();
    }

    public override void Draw()
    {
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

        var footerHeight = (ImGui.GetTextLineHeightWithSpacing() * 2f) + ImGui.GetStyle().ItemSpacing.Y;
        ImGui.BeginChild("##videosync-body", new Vector2(0f, -footerHeight));
        if (ImGui.BeginTabBar("##videosync-tabs"))
        {
            if (ImGui.BeginTabItem("Watch"))
            {
                this.DrawWatchTab(running);
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

        ImGui.EndChild();
        this.DrawStatusBar(running);
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
            this.DrawHostSetupSection();
        }
        else
        {
            this.DrawCreateRoomSection();
            if (!string.IsNullOrWhiteSpace(this.lastWatch2GetherRoomCode))
            {
                ImGui.Spacing();
                this.DrawCurrentShareCode();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        this.DrawJoinRoomSection();

        if (running)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Playback, volume, and screen controls are on the Screen tab.");
        }
    }

    // A persistent, always-visible toggle pinned above the tabs whenever a video is
    // running, so bringing the player on screen (to click past ads / consent / sign-ins)
    // is one click away no matter which tab you're on.
    private void DrawTopMiniBrowserBar()
    {
        var pressed = UiTheme.PrimaryButton(
            this.browserShown ? "Hide mini-browser" : "Show mini-browser",
            new Vector2(-1f, 30f));

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("The video runs in a hidden browser window. Show it whenever a video is stuck on a consent screen, ad, sign-in, or age check, then press Done to send it back.");
        }

        if (pressed)
        {
            this.browserShown = !this.browserShown;
            if (this.browserShown)
            {
                this.AllowRendererForeground();
            }

            this.SendPlaybackCommand(this.browserShown ? "show" : "hide");
            this.QueueSnowSyncBroadcast();
            this.status = this.browserShown
                ? "Mini-browser is on screen. Click through any consent dialog, ad, or sign-in, then press Done."
                : "Sent the mini-browser back off screen.";
        }
    }

    private void DrawCreateRoomSection()
    {
        UiTheme.SectionTitle("Create a movie room");
        ImGui.TextDisabled("YouTube, Vimeo, Twitch, or a direct video file.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1f);
        var submitted = ImGui.InputTextWithHint(
            "##videosync-url",
            "https://www.youtube.com/watch?v=...",
            ref this.youtubeUrl,
            512,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.Spacing();
        var busy = this.creatingWatch2GetherRoom;
        if (busy)
        {
            ImGui.BeginDisabled();
        }

        var pressed = UiTheme.PrimaryButton(busy ? "Creating room..." : "Create Movie Room", new Vector2(-1f, 40f));
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
        UiTheme.SectionTitle("Current share code");
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
        UiTheme.SectionTitle("Join a room");
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
        UiTheme.SectionTitle("Host a movie room");
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

        this.DrawAudioControls();

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

    private string GetSelectedSnowSyncshellLabel()
    {
        if (this.selectedSnowSyncshellIndex >= 0 && this.selectedSnowSyncshellIndex < this.snowSyncshells.Count)
        {
            return FormatSnowSyncshellLabel(this.snowSyncshells[this.selectedSnowSyncshellIndex]);
        }

        return int.TryParse(this.syncshellText, out var shellNumber)
            ? $"Manual /ss{shellNumber}"
            : "Choose a syncshell";
    }

    private string GetSyncTransportLabel()
    {
        return this.syncTransportIndex == SyncTransportCwls
            ? FormatCwlsLabel(this.selectedCwlIndex)
            : int.TryParse(this.syncshellText, out var shellNumber) ? $"/ss{shellNumber}" : "Snowcloak";
    }

    private static string FormatCwlsLabel(int index)
    {
        var name = GameChat.GetCrossworldLinkshellName(index + 1);
        return name is null
            ? $"CWLS {index + 1} (no linkshell)"
            : $"{index + 1}: {name}";
    }

    private bool TryGetSyncChatPrefix(out string commandPrefix, out string label, out string error)
    {
        commandPrefix = string.Empty;
        label = string.Empty;
        error = string.Empty;

        if (this.syncTransportIndex == SyncTransportCwls)
        {
            var cwlNumber = this.selectedCwlIndex + 1;
            if (cwlNumber is < 1 or > GameChat.CrossworldLinkshellSlots)
            {
                error = "CWLS slot must be 1-8. Use the number from /cwl1 through /cwl8.";
                return false;
            }

            if (GameChat.GetCrossworldLinkshellName(cwlNumber) is null)
            {
                error = $"You have no cross-world linkshell in slot {cwlNumber}. Pick one that has a name.";
                return false;
            }

            commandPrefix = $"/cwlinkshell{cwlNumber}";
            label = FormatCwlsLabel(this.selectedCwlIndex);
            return true;
        }

        if (!int.TryParse(this.syncshellText, out var shellNumber) || shellNumber is < 1 or > 50)
        {
            error = "Snowcloak syncshell number must be 1-50.";
            return false;
        }

        commandPrefix = $"/ss{shellNumber}";
        label = $"Snowcloak /ss{shellNumber}";
        return true;
    }

    private void RefreshSnowSyncshells()
    {
        try
        {
            var shells = ReadSnowSyncshellsFromConfig();
            this.snowSyncshells.Clear();
            this.snowSyncshells.AddRange(shells);
            this.selectedSnowSyncshellIndex = this.FindSnowSyncshellIndex(this.syncshellText);

            if (this.selectedSnowSyncshellIndex < 0)
            {
                var firstEnabled = this.snowSyncshells.FindIndex(shell => shell.Enabled);
                if (firstEnabled >= 0)
                {
                    this.selectedSnowSyncshellIndex = firstEnabled;
                    this.syncshellText = this.snowSyncshells[firstEnabled].ShellNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            this.lastSnowSyncshellRefreshUtc = DateTime.UtcNow;
            this.snowSyncshellStatus = this.snowSyncshells.Count == 0
                ? "No Snowcloak syncshells found. You can still type a manual /ss number."
                : $"Loaded {this.snowSyncshells.Count} Snowcloak syncshell(s).";
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not refresh Snowcloak syncshell list.");
            this.lastSnowSyncshellRefreshUtc = DateTime.UtcNow;
            this.snowSyncshellStatus = "Could not read Snowcloak syncshells. Manual /ss# still works.";
        }
    }

    private int FindSnowSyncshellIndex(string shellText)
    {
        if (!int.TryParse(shellText, out var shellNumber))
        {
            return -1;
        }

        return this.snowSyncshells.FindIndex(shell => shell.ShellNumber == shellNumber);
    }

    private static string FormatSnowSyncshellLabel(SnowSyncshell shell)
    {
        return shell.Enabled
            ? $"{shell.Gid} (/ss{shell.ShellNumber})"
            : $"{shell.Gid} (/ss{shell.ShellNumber}, disabled)";
    }

    private static List<SnowSyncshell> ReadSnowSyncshellsFromConfig()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dbPath = Path.Combine(appData, "XIVLauncher", "pluginConfigs", "Snowcloak", "Snowcloak.sqlite");
        if (!File.Exists(dbPath))
        {
            return [];
        }

        var snowcloakDirectory = FindSnowcloakInstallDirectory(appData);
        if (snowcloakDirectory is null)
        {
            return [];
        }

        string payload;
        try
        {
            payload = ReadSnowcloakStateDocument(snowcloakDirectory, dbPath, "syncshells.json");
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Falling back to raw Snowcloak syncshell scan.");
            payload = TryReadSnowSyncshellJsonByScanningSqlite(dbPath);
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        var shells = new List<SnowSyncshell>();
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("ServerShellStorage", out var serverStorage) ||
            serverStorage.ValueKind != JsonValueKind.Object)
        {
            return shells;
        }

        foreach (var server in serverStorage.EnumerateObject())
        {
            if (!server.Value.TryGetProperty("GidShellConfig", out var gidConfig) ||
                gidConfig.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var shellProperty in gidConfig.EnumerateObject())
            {
                var shellElement = shellProperty.Value;
                if (!shellElement.TryGetProperty("ShellNumber", out var shellNumberElement) ||
                    !shellNumberElement.TryGetInt32(out var shellNumber))
                {
                    continue;
                }

                var enabled = shellElement.TryGetProperty("Enabled", out var enabledElement) &&
                              enabledElement.ValueKind == JsonValueKind.True;
                shells.Add(new SnowSyncshell(shellProperty.Name, shellNumber, enabled));
            }
        }

        shells.Sort((left, right) =>
        {
            var enabledCompare = right.Enabled.CompareTo(left.Enabled);
            if (enabledCompare != 0)
            {
                return enabledCompare;
            }

            var numberCompare = left.ShellNumber.CompareTo(right.ShellNumber);
            return numberCompare != 0
                ? numberCompare
                : string.Compare(left.Gid, right.Gid, StringComparison.OrdinalIgnoreCase);
        });
        return shells;
    }

    private static string? FindSnowcloakInstallDirectory(string appData)
    {
        var root = Path.Combine(appData, "XIVLauncher", "installedPlugins", "Snowcloak");
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateDirectories(root)
            .OrderByDescending(ParseDirectoryVersion)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "Microsoft.Data.Sqlite.dll")));
    }

    private static Version ParseDirectoryVersion(string path)
    {
        return Version.TryParse(Path.GetFileName(path), out var version)
            ? version
            : new Version(0, 0);
    }

    private static string ReadSnowcloakStateDocument(string snowcloakDirectory, string dbPath, string documentName)
    {
        var sqlitePath = Path.Combine(snowcloakDirectory, "Microsoft.Data.Sqlite.dll");
        if (!File.Exists(sqlitePath))
        {
            return string.Empty;
        }

        var oldPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        ResolveEventHandler resolver = (_, args) =>
        {
            var assemblyName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                return null;
            }

            var candidate = Path.Combine(snowcloakDirectory, assemblyName + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        AppDomain.CurrentDomain.AssemblyResolve += resolver;
        Environment.SetEnvironmentVariable("PATH", snowcloakDirectory + Path.PathSeparator + oldPath);
        try
        {
            var sqliteAssembly = Assembly.LoadFrom(sqlitePath);
            var connectionType = sqliteAssembly.GetType("Microsoft.Data.Sqlite.SqliteConnection", throwOnError: true)!;
            using var connection = (IDisposable)Activator.CreateInstance(
                connectionType,
                $"Data Source={dbPath};Mode=ReadOnly")!;
            connectionType.GetMethod("Open", Type.EmptyTypes)!.Invoke(connection, null);

            using var command = (IDisposable)connectionType.GetMethod("CreateCommand", Type.EmptyTypes)!.Invoke(connection, null)!;
            var commandType = command.GetType();
            commandType.GetProperty("CommandText")!.SetValue(
                command,
                "select payload from state_documents where document_name = $documentName");
            var parameters = commandType.GetProperty("Parameters")!.GetValue(command)!;
            var addWithValue = parameters.GetType().GetMethod("AddWithValue", [typeof(string), typeof(object)])!;
            addWithValue.Invoke(parameters, ["$documentName", documentName]);

            using var reader = (IDisposable)commandType.GetMethod("ExecuteReader", Type.EmptyTypes)!.Invoke(command, null)!;
            var readerType = reader.GetType();
            var read = (bool)readerType.GetMethod("Read", Type.EmptyTypes)!.Invoke(reader, null)!;
            if (!read)
            {
                return string.Empty;
            }

            return (string)readerType.GetMethod("GetString", [typeof(int)])!.Invoke(reader, [0])!;
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            AppDomain.CurrentDomain.AssemblyResolve -= resolver;
        }
    }

    private static string TryReadSnowSyncshellJsonByScanningSqlite(string dbPath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(dbPath);
        }
        catch (IOException)
        {
            return string.Empty;
        }

        var text = Encoding.UTF8.GetString(bytes);
        var searchIndex = 0;
        while (true)
        {
            var markerIndex = text.IndexOf("\"ServerShellStorage\"", searchIndex, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return string.Empty;
            }

            var objectStart = text.LastIndexOf('{', markerIndex);
            if (objectStart < 0)
            {
                return string.Empty;
            }

            var candidate = ExtractJsonObject(text, objectStart);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                try
                {
                    using var _ = JsonDocument.Parse(candidate);
                    return candidate;
                }
                catch (JsonException)
                {
                    // Keep scanning; SQLite can retain stale fragments in free pages.
                }
            }

            searchIndex = markerIndex + 1;
        }
    }

    private static string ExtractJsonObject(string text, int objectStart)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = objectStart; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return text.Substring(objectStart, i - objectStart + 1);
            }
        }

        return string.Empty;
    }

    public void ReceiveSharedCode(string code)
    {
        // Our own share arrives back as a chat echo; do not re-open on it.
        if (string.Equals(code, this.lastOutboundSnowSyncCode, StringComparison.Ordinal)
            && DateTime.UtcNow - this.lastOutboundSnowSyncUtc < TimeSpan.FromSeconds(8))
        {
            return;
        }

        if (!TryDecode(code, out var payload, out var error))
        {
            this.status = $"Ignored invalid shared code: {error}";
            return;
        }

        this.pasteCode = code;
        this.decodedSummary = FormatSummary(payload);
        this.status = "Received a sync code from chat. Use Open Pasted or Open Overlay to join it.";
        this.IsOpen = true;
    }

    public void ReceiveSnowSync(string code)
    {
        this.ReceiveSnowSync(code, force: false);
    }

    private void ReceiveSnowSync(string code, bool force)
    {
        if (!force &&
            string.Equals(code, this.lastOutboundSnowSyncCode, StringComparison.Ordinal)
            && DateTime.UtcNow - this.lastOutboundSnowSyncUtc < TimeSpan.FromSeconds(8))
        {
            return;
        }

        if (!TryDecode(code, out var payload, out var error))
        {
            this.status = $"Ignored invalid chat video sync: {error}";
            return;
        }

        var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;
        var sameVideo = running && string.Equals(this.currentVideoId, payload.VideoId, StringComparison.Ordinal);

        // Corrections for the party we are already in always apply; anything else
        // only becomes a "watch party found" offer unless the user opted into
        // joining automatically.
        if (!force && !sameVideo && !this.autoJoinSnowSync)
        {
            var isNewDiscovery = this.discoveredParty is not { } known
                || !string.Equals(known.VideoId, payload.VideoId, StringComparison.Ordinal)
                || DateTime.UtcNow - this.discoveredPartyUtc > TimeSpan.FromSeconds(90);
            this.discoveredParty = payload;
            this.discoveredPartyCode = code;
            this.discoveredPartyUtc = DateTime.UtcNow;

            if (isNewDiscovery)
            {
                this.status = "Found a watch party in your linkshell. Join it from the Watch tab.";
                Plugin.ChatGui.Print("[VideoSync] A watch party is running in your linkshell. Type /videosync to join.");
            }

            return;
        }

        this.ClearDiscoveredParty();
        this.pasteCode = code;
        this.decodedSummary = FormatSummary(payload);
        this.ApplyRemoteSync(payload, "linkshell");
    }

    private void ClearDiscoveredParty()
    {
        this.discoveredParty = null;
        this.discoveredPartyCode = string.Empty;
        this.discoveredPartyUtc = DateTime.MinValue;
    }

    public void ReceiveWatch2GetherRoom(Watch2GetherRoom room)
    {
        var key = room.NormalizedUrl;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (string.Equals(key, this.lastOutboundWatch2GetherRoomKey, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - this.lastOutboundWatch2GetherRoomUtc < TimeSpan.FromSeconds(8))
        {
            return;
        }

        if (this.ignoredWatch2GetherRoomKeys.Contains(key) ||
            string.Equals(key, this.incomingWatch2GetherRoomKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        this.incomingWatch2GetherRoom = room;
        this.incomingWatch2GetherRoomKey = key;
        this.incomingWatch2GetherRoomUtc = DateTime.UtcNow;
        this.status = "A shared Watch2Gether room was created. Open it from the Watch tab.";
        Plugin.ChatGui.Print("[VideoSync] A shared video room was created. Type /videosync to open it.");
        this.IsOpen = true;
    }

    private void ClearIncomingWatch2GetherRoom()
    {
        this.incomingWatch2GetherRoom = null;
        this.incomingWatch2GetherRoomKey = string.Empty;
        this.incomingWatch2GetherRoomUtc = DateTime.MinValue;
    }

    private void CreateMovieRoom()
    {
        if (this.creatingWatch2GetherRoom)
        {
            return;
        }

        var apiKey = this.config.Watch2GetherApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            this.status = "Add your Watch2Gether API key in Settings first.";
            return;
        }

        // Safety net: if this key is being used to create a room, make sure it's on
        // disk (the field-edit handler already saves, but persist here too so a key
        // that works is never lost across restarts).
        if (this.config.Watch2GetherApiKey != apiKey)
        {
            this.config.Watch2GetherApiKey = apiKey;
        }

        this.config.Save();

        if (!Uri.TryCreate(this.youtubeUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            this.status = "Paste a valid video link first (a full http/https URL).";
            return;
        }

        // Kick off the API call on a background task and poll it from the UI thread in
        // TickWatch2GetherRoomCreation. One POST creates the room and preloads the
        // video server-side, so there's no page to scrape and no popup to close.
        this.creatingWatch2GetherRoom = true;
        this.status = "Creating room...";
        this.watch2GetherCreateTask = Watch2GetherApi.CreateRoomAsync(apiKey, uri.ToString());
    }

    private void TickWatch2GetherRoomCreation()
    {
        if (this.watch2GetherCreateTask is not { IsCompleted: true } task)
        {
            return;
        }

        this.watch2GetherCreateTask = null;
        this.creatingWatch2GetherRoom = false;

        if (task.IsCompletedSuccessfully)
        {
            var room = task.Result;
            this.SaveWatch2GetherRoom(room);

            // Auto-open on the in-world screen so the host is one click into watching.
            // The video is already the room's current item, so it autoplays.
            this.PlayWatch2GetherRoomInWorld(room);
            this.status = "Room ready. Video is loading on the screen — share the code below.";
        }
        else
        {
            var message = task.Exception?.InnerException is Watch2GetherApiException apiEx
                ? apiEx.Message
                : "Failed to create the room. Please try again.";
            this.status = message;
        }
    }

    private void SetActionStatus(string message)
    {
        this.status = message;
        Plugin.ChatGui.Print($"[VideoSync] {message}");
    }

    private void SendWatch2GetherRoom(Watch2GetherRoom room, bool openInWorld = true)
    {
        this.SaveWatch2GetherRoom(room);
        if (openInWorld)
        {
            this.PlayWatch2GetherRoomInWorld(room);
        }

        if (!this.TryGetSyncChatPrefix(out var commandPrefix, out var label, out var error))
        {
            this.SetActionStatus($"Created the Watch2Gether room and opened it on the TV, but could not share it: {error}");
            return;
        }

        var code = this.BuildRoomShareCode(room);
        if (!this.TrySendChatCommand($"{commandPrefix} [VideoSync-W2G] {code}"))
        {
            this.SetActionStatus("Created the Watch2Gether room and opened it on the TV, but the chat invite could not be sent.");
            return;
        }

        var key = room.NormalizedUrl;
        this.lastOutboundWatch2GetherRoomKey = key;
        this.lastOutboundWatch2GetherRoomUtc = DateTime.UtcNow;
        this.status = $"Created and shared a Watch2Gether room through {label}.";
    }

    private void SaveWatch2GetherRoom(Watch2GetherRoom room)
    {
        this.lastWatch2GetherRoomUrl = room.NormalizedUrl;
        this.lastWatch2GetherRoomCode = this.BuildRoomShareCode(room);
        this.lastSentVolume = -1f;
        this.lastAudioWriteUtc = DateTime.MinValue;
    }

    private void OpenPastedWatch2GetherRoom()
    {
        if (!Watch2GetherRoomParser.TryParse(this.pasteWatch2GetherRoomCode, out var room))
        {
            this.SetActionStatus("Paste a valid W2G join code or Watch2Gether room URL first.");
            return;
        }

        this.SaveWatch2GetherRoom(room);
        this.PlayWatch2GetherRoomInWorld(room);
    }

    private void PlayWatch2GetherRoomInWorld(Watch2GetherRoom room)
    {
        if (!this.StartRendererBridge(room.NormalizedUrl))
        {
            return;
        }

        this.currentVideoId = string.Empty;
        var matchedHostLayout = false;
        if (room.Layout is { } layout)
        {
            // The join code carried the host's exact placement — reproduce it so the
            // joiner's TV matches position, size, stretch, and occlusion 1:1, even if
            // this client already had a screen up from a previous room.
            this.ApplyRoomScreenLayout(layout);
            matchedHostLayout = true;
        }
        else if (this.worldScreenAnchor is null)
        {
            this.PlaceWorldScreenInFrontOfPlayer();
        }

        this.EnableNativeWorldScreen();
        this.playingWatch2GetherRoom = true;
        this.ignoredWatch2GetherRoomKeys.Add(room.NormalizedUrl);
        this.status = matchedHostLayout
            ? "Joined the room and matched the host's screen — same spot, size, and stretch."
            : "Opened the Watch2Gether room on the in-world screen.";
    }

    private void OpenWatch2GetherRoom(Watch2GetherRoom room)
    {
        Process.Start(new ProcessStartInfo(room.NormalizedUrl) { UseShellExecute = true });
        this.ignoredWatch2GetherRoomKeys.Add(room.NormalizedUrl);
        this.status = "Opened the shared Watch2Gether room in your browser.";
    }

    private void TestReceiveSnowSync()
    {
        string code;
        if (TryDecode(this.pasteCode, out _, out _))
        {
            code = this.pasteCode.Trim();
        }
        else if (TryDecode(this.generatedCode, out _, out _))
        {
            code = this.generatedCode.Trim();
        }
        else if (this.TryCreateCurrentSyncPayload(out var payload, out var error))
        {
            code = SyncCode.Encode(payload);
        }
        else
        {
            this.status = $"Could not create a test receive payload: {error}";
            return;
        }

        this.StopPlayback();
        this.worldScreenAnchor = null;
        this.currentVideoId = string.Empty;
        this.ReceiveSnowSync(code, force: true);
        this.status = "Simulated a fresh incoming chat VSYNC locally. " + this.status;
    }

    private void ShareViaSnowcloak(string code)
    {
        if (!this.TryGetSyncChatPrefix(out var commandPrefix, out var label, out var error))
        {
            this.status = error;
            return;
        }

        if (!this.TrySendChatCommand($"{commandPrefix} [VideoSync] {code}"))
        {
            return;
        }

        this.lastOutboundSnowSyncCode = code.Trim();
        this.lastOutboundSnowSyncUtc = DateTime.UtcNow;
        this.status = $"Shared sync code through {label}.";
    }

    private void SendSyncTransportTest()
    {
        if (!this.TryGetSyncChatPrefix(out var commandPrefix, out var label, out var error))
        {
            this.status = error;
            return;
        }

        if (this.TrySendChatCommand($"{commandPrefix} [VideoSync] channel test"))
        {
            this.status = $"Sent a test message through {label}.";
        }
    }

    private void BroadcastCurrentSnowSync(string reason)
    {
        if (!this.TryCreateCurrentSyncPayload(out var payload, out var error))
        {
            this.status = error;
            return;
        }

        if (!this.TryGetSyncChatPrefix(out var commandPrefix, out var label, out var transportError))
        {
            this.status = transportError;
            return;
        }

        var code = SyncCode.Encode(payload);
        if (!this.SendSnowSyncCode(commandPrefix, code))
        {
            return;
        }

        this.generatedCode = code;
        this.decodedSummary = FormatSummary(payload);
        this.lastSnowSyncBroadcastUtc = DateTime.UtcNow;
        this.lastOutboundSnowSyncCode = code;
        this.lastOutboundSnowSyncUtc = DateTime.UtcNow;
        if (reason is not "heartbeat")
        {
            this.status = $"Broadcast {reason} video sync through {label}.";
        }

        if (reason is "host")
        {
            this.ScheduleSnowSyncBurst(commandPrefix, code);
        }
    }

    private bool SendSnowSyncCode(string commandPrefix, string code)
    {
        // VS2 codes are self-identifying, so live syncs are just the bare code.
        return this.TrySendChatCommand($"{commandPrefix} {code}");
    }

    private bool TrySendChatCommand(string command)
    {
        try
        {
            // Snowcloak's /ss commands are Dalamud commands; game chat commands
            // like /cwlinkshell go through the real chat box entry point.
            if (this.syncTransportIndex == SyncTransportSnowcloak &&
                Plugin.CommandManager.ProcessCommand(command))
            {
                return true;
            }

            if (!GameChat.TrySendMessage(command, out var error))
            {
                this.status = $"Could not send the chat message: {error}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not send video sync chat command.");
            this.status = "Could not send the chat message. Check the selected channel and try Test channel.";
            return false;
        }
    }

    private void ScheduleSnowSyncBurst(string commandPrefix, string code)
    {
        var now = DateTime.UtcNow;
        this.pendingSnowSyncBursts.Add(new SnowSyncBurst(now.AddMilliseconds(1500), commandPrefix, code));
    }

    private void TickSnowSyncBroadcast()
    {
        var now = DateTime.UtcNow;
        for (var i = this.pendingSnowSyncBursts.Count - 1; i >= 0; i--)
        {
            var burst = this.pendingSnowSyncBursts[i];
            if (now < burst.DueUtc)
            {
                continue;
            }

            this.SendSnowSyncCode(burst.CommandPrefix, burst.Code);
            this.lastOutboundSnowSyncCode = burst.Code;
            this.lastOutboundSnowSyncUtc = now;
            this.pendingSnowSyncBursts.RemoveAt(i);
        }

        if (!this.hostSnowSync)
        {
            this.pendingSnowSyncBroadcastUtc = DateTime.MinValue;
            return;
        }

        if (this.rendererProcess is null || this.rendererProcess.HasExited)
        {
            return;
        }

        if (this.pendingSnowSyncBroadcastUtc != DateTime.MinValue && now >= this.pendingSnowSyncBroadcastUtc)
        {
            this.pendingSnowSyncBroadcastUtc = DateTime.MinValue;
            this.BroadcastCurrentSnowSync("updated");
            return;
        }

        if (now - this.lastSnowSyncBroadcastUtc >= TimeSpan.FromSeconds(SnowHeartbeatSeconds))
        {
            this.BroadcastCurrentSnowSync("heartbeat");
        }
    }

    private void QueueSnowSyncBroadcast()
    {
        if (!this.hostSnowSync)
        {
            return;
        }

        this.pendingSnowSyncBroadcastUtc = DateTime.UtcNow.AddSeconds(SnowUpdateDebounceSeconds);
    }

    private bool TryCreateCurrentSyncPayload(out SyncPayload payload, out string error)
    {
        payload = default;
        error = string.Empty;

        var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;
        if (!running && TryDecode(this.generatedCode, out payload, out _))
        {
            payload = payload with
            {
                Screen = this.CreateCurrentSyncScreen(),
                Audio = this.CreateCurrentSyncAudio(),
                Options = this.CreateCurrentSyncOptions(),
            };
            return true;
        }

        this.TryUpdatePlaybackStatus();

        var videoId = this.currentVideoId;
        if (string.IsNullOrWhiteSpace(videoId) && !TryExtractYouTubeId(this.youtubeUrl, out videoId))
        {
            error = "Snow sync needs a YouTube video. Start one in-world or enter a YouTube URL first.";
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        payload = new SyncPayload(
            videoId,
            now,
            Math.Round(Math.Max(0, this.GetEstimatedPlaybackTime()), 2),
            this.playbackPaused ? 0.0 : 1.0,
            this.CreateCurrentSyncScreen(),
            this.CreateCurrentSyncAudio(),
            this.CreateCurrentSyncOptions());
        return true;
    }

    private SyncScreen? CreateCurrentSyncScreen()
    {
        if (this.worldScreenAnchor is not { } anchor)
        {
            return null;
        }

        // Rounded so encoded codes stay well under the game's 500-byte chat limit.
        return new SyncScreen(
            this.worldScreenEnabled,
            MathF.Round(anchor.X, 2),
            MathF.Round(anchor.Y, 2),
            MathF.Round(anchor.Z, 2),
            MathF.Round(this.worldScreenRotation, 3),
            MathF.Round(this.worldScreenWidth, 2),
            MathF.Round(this.worldScreenDistance, 2),
            MathF.Round(this.worldScreenHeightOffset, 2),
            this.worldScreenActorOcclusion,
            MathF.Round(this.worldScreenOcclusionPadding, 1));
    }

    // Snapshot every part of the live screen placement so a room join code can
    // carry the host's exact layout: world position, rotation, size, stretch,
    // aspect lock, the elevation/push accumulators, and occlusion.
    private RoomScreenLayout? CaptureCurrentScreenLayout()
    {
        if (this.worldScreenAnchor is not { } anchor)
        {
            return null;
        }

        return new RoomScreenLayout(
            this.worldScreenEnabled,
            anchor.X,
            anchor.Y,
            anchor.Z,
            this.worldScreenRotation,
            this.worldScreenWidth,
            this.worldScreenHeight,
            this.worldScreenLockAspect,
            this.worldScreenElevation,
            this.worldScreenPush,
            this.worldScreenDistance,
            this.worldScreenHeightOffset,
            this.worldScreenActorOcclusion,
            this.worldScreenOcclusionPadding);
    }

    // Drop the host's captured screen layout straight onto this client's fields so
    // the joiner's TV matches the host's placement exactly. Callers enable the
    // screen afterwards; DrawWorldSurfaceOverlay re-derives height from width when
    // the aspect is locked, so a locked host stays locked here too.
    private void ApplyRoomScreenLayout(RoomScreenLayout layout)
    {
        this.worldScreenAnchor = new Vector3(layout.X, layout.Y, layout.Z);
        this.worldScreenRotation = layout.Rotation;
        this.worldScreenWidth = layout.Width;
        this.worldScreenHeight = layout.Height;
        this.worldScreenLockAspect = layout.LockAspect;
        this.worldScreenElevation = layout.Elevation;
        this.worldScreenPush = layout.Push;
        this.worldScreenDistance = layout.Distance;
        this.worldScreenHeightOffset = layout.HeightOffset;
        this.worldScreenActorOcclusion = layout.ActorOcclusion;
        this.worldScreenOcclusionPadding = layout.OcclusionPadding;
    }

    // Build the shareable room code from the live screen layout, so whatever the
    // host has on screen right now is what a joiner reproduces.
    private string BuildRoomShareCode(Watch2GetherRoom room)
    {
        return Watch2GetherRoomCode.Encode(room with { Layout = this.CaptureCurrentScreenLayout() });
    }

    private SyncAudio CreateCurrentSyncAudio()
    {
        return new SyncAudio(
            MathF.Round(this.masterVolume, 2),
            this.audioMuted,
            this.spatialAudio,
            MathF.Round(this.audioRange, 1));
    }

    private SyncOptions CreateCurrentSyncOptions()
    {
        return new SyncOptions(
            this.videoFullscreen,
            this.adBlockEnabled,
            !this.browserShown);
    }

    private void ApplyRemoteSync(SyncPayload payload, string source)
    {
        var syncVersion = ++this.remoteSyncVersion;
        var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;
        var targetSeconds = Math.Max(0, payload.GetCurrentVideoSeconds());

        if (running && string.Equals(this.currentVideoId, payload.VideoId, StringComparison.Ordinal))
        {
            // Already in this party: one gentle correction, and only touch the
            // presentation when something actually changed. Re-applying identical
            // state every heartbeat is what caused the periodic playback jitter.
            this.ApplyRemotePresentation(payload);
            if (this.ApplyRemotePlaybackCorrection(payload, RunningSyncDriftThresholdSeconds))
            {
                this.status = $"Applied {source} sync correction at {FormatTimestamp(targetSeconds)}.";
            }

            return;
        }

        this.StartInWindowPreview(payload);
        this.ApplyRemotePresentation(payload);
        this.SchedulePlaybackCorrections(payload, syncVersion);
        this.status = $"Joined {source} video sync at {FormatTimestamp(targetSeconds)}.";
    }

    private void ApplyRemotePresentation(SyncPayload payload)
    {
        // Hosts re-broadcast the full state every heartbeat, so skip anything that
        // matches what we already have; re-applying options or the screen causes
        // visible hitches in the player.
        if (payload.Options is { } options)
        {
            var optionsChanged = this.adBlockEnabled != options.AdBlock
                || this.browserShown != !options.HideBrowser
                || this.videoFullscreen != options.VideoFullscreen;
            this.adBlockEnabled = options.AdBlock;
            this.browserShown = !options.HideBrowser;
            this.videoFullscreen = options.VideoFullscreen;
            if (optionsChanged)
            {
                var flags = (options.AdBlock ? 1 : 0) |
                            (options.VideoFullscreen ? 2 : 0) |
                            (options.HideBrowser ? 4 : 0);
                this.SendPlaybackCommand("applyoptions", flags);
            }
        }

        if (payload.Audio is { } audio)
        {
            var newVolume = Math.Clamp(audio.Volume, 0f, 1f);
            var newRange = Math.Clamp(audio.Range, 5f, 100f);
            var audioChanged = Math.Abs(this.masterVolume - newVolume) > 0.005f
                || this.audioMuted != audio.Muted
                || this.spatialAudio != audio.Spatial
                || Math.Abs(this.audioRange - newRange) > 0.5f;
            this.masterVolume = newVolume;
            this.audioMuted = audio.Muted;
            this.spatialAudio = audio.Spatial;
            this.audioRange = newRange;
            if (audioChanged)
            {
                this.lastSentVolume = -1f;
                this.lastAudioWriteUtc = DateTime.MinValue;
            }
        }

        if (payload.Screen is not { } screen)
        {
            return;
        }

        var newAnchor = new Vector3(screen.X, screen.Y, screen.Z);
        var newWidth = Math.Clamp(screen.Width, 1f, 10f);
        var newDistance = Math.Clamp(screen.Distance, 1f, 12f);
        var newHeight = Math.Clamp(screen.HeightOffset, 0.2f, 4f);
        var newPadding = Math.Clamp(screen.OcclusionPadding, 0f, 48f);
        var screenChanged = this.worldScreenAnchor != newAnchor
            || this.worldScreenRotation != screen.Rotation
            || this.worldScreenWidth != newWidth
            || this.worldScreenDistance != newDistance
            || this.worldScreenHeightOffset != newHeight
            || this.worldScreenActorOcclusion != screen.ActorOcclusion
            || this.worldScreenOcclusionPadding != newPadding
            || this.worldScreenEnabled != screen.Enabled;
        if (!screenChanged)
        {
            return;
        }

        this.worldScreenAnchor = newAnchor;
        this.worldScreenRotation = screen.Rotation;
        this.worldScreenWidth = newWidth;
        this.worldScreenDistance = newDistance;
        this.worldScreenHeightOffset = newHeight;
        this.worldScreenActorOcclusion = screen.ActorOcclusion;
        this.worldScreenOcclusionPadding = newPadding;
        this.SetWorldScreenEnabledFromSync(screen.Enabled);
    }

    private void SetWorldScreenEnabledFromSync(bool enabled)
    {
        this.worldScreenEnabled = enabled;
        if (!enabled)
        {
            this.presentHookProbe.NativeTestDrawEnabled = false;
            this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
            this.presentHookProbe.ClearNativeQuad();
            return;
        }

        this.drawImguiWorldScreen = false;
        if (this.presentHookProbe.TryInstall())
        {
            this.presentHookProbe.NativeTestDrawEnabled = true;
            this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
            return;
        }

        this.presentHookProbe.NativeTestDrawEnabled = false;
        this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
        this.drawImguiWorldScreen = true;
        this.worldScreenActorOcclusion = true;
    }

    private bool ApplyRemotePlaybackCorrection(SyncPayload payload, double thresholdSeconds)
    {
        var targetSeconds = Math.Max(0, payload.GetCurrentVideoSeconds());
        var paused = payload.PlaybackRate == 0;
        this.TryUpdatePlaybackStatus();
        var drift = Math.Abs(this.GetEstimatedPlaybackTime() - targetSeconds);
        if (this.playbackPaused != paused)
        {
            this.SendPlaybackCommand(paused ? "syncpause" : "syncplay", targetSeconds);
            return true;
        }

        if (paused)
        {
            if (drift > SoftSyncDriftThresholdSeconds)
            {
                this.SendPlaybackCommand("syncpause", targetSeconds);
                return true;
            }

            return false;
        }

        if (drift > thresholdSeconds)
        {
            this.SendPlaybackCommand("syncplay", targetSeconds);
            return true;
        }

        if (drift > SoftSyncDriftThresholdSeconds)
        {
            this.SendPlaybackCommand("syncsoftplay", targetSeconds);
            return true;
        }

        return false;
    }

    private void SchedulePlaybackCorrections(SyncPayload payload, int syncVersion)
    {
        _ = Task.Run(async () =>
        {
            foreach (var delay in new[] { 180, 700, 1800, 3500 })
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (syncVersion != this.remoteSyncVersion)
                {
                    return;
                }

                this.ApplyRemotePlaybackCorrection(payload, StartupSyncDriftThresholdSeconds);
            }
        });
    }

    private void OpenInDefaultBrowser(SyncPayload payload)
    {
        Process.Start(new ProcessStartInfo(BuildWatchUrl(payload)) { UseShellExecute = true });
    }

    private void OpenInOverlay(SyncPayload payload)
    {
        var overlayPath = Path.Combine(this.pluginDirectory, "OverlayPlayer", "OverlayPlayer.exe");
        if (!File.Exists(overlayPath))
        {
            this.status = $"Overlay player was not found at {overlayPath}";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = overlayPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(overlayPath) ?? AppContext.BaseDirectory,
            ArgumentList = { BuildWatchUrl(payload) },
        });

        this.status = "Opened the overlay player with the normal YouTube watch page.";
    }

    private void PlayUrlInWorld(string input)
    {
        string url;
        if (TryExtractYouTubeId(input, out var videoId))
        {
            url = $"https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}&autoplay=1";
            this.currentVideoId = videoId;
        }
        else if (Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            url = uri.ToString();
            this.currentVideoId = string.Empty;
        }
        else
        {
            this.status = "Enter a YouTube (or any http/https) video URL first.";
            return;
        }

        if (!this.StartRendererBridge(url))
        {
            return;
        }

        this.playingWatch2GetherRoom = false;
        if (this.worldScreenAnchor is null)
        {
            this.PlaceWorldScreenInFrontOfPlayer();
        }

        this.EnableNativeWorldScreen();
        this.status = "Playing on the in-world screen. Use the playback controls on the Screen tab.";
    }

    private void StopPlayback()
    {
        this.StopInWindowPreview();
        this.currentVideoId = string.Empty;
        this.playingWatch2GetherRoom = false;
        this.worldScreenEnabled = false;
        this.browserShown = false;
        this.videoFullscreen = false;
        this.presentHookProbe.NativeTestDrawEnabled = false;
        this.presentHookProbe.ClearNativeQuad();
        this.status = "Stopped playback and hid the world screen.";
    }

    private void StartInWindowPreview(SyncPayload payload)
    {
        if (!this.StartRendererBridge(BuildWatchUrl(payload)))
        {
            return;
        }

        this.currentVideoId = payload.VideoId;
        if (this.worldScreenAnchor is null)
        {
            this.PlaceWorldScreenInFrontOfPlayer();
        }

        this.EnableNativeWorldScreen();
        this.status = "Started the synced video on the in-world screen.";
    }

    private bool StartRendererBridge(string url, string? watch2GetherShareUrl = null)
    {
        var overlayPath = Path.Combine(this.pluginDirectory, "OverlayPlayer", "OverlayPlayer.exe");
        if (!File.Exists(overlayPath))
        {
            this.status = $"Overlay player was not found at {overlayPath}";
            return false;
        }

        this.StopRendererProcess();
        this.frameTexture?.Dispose();
        this.frameTexture = null;
        this.frameTextureTask = null;
        this.lastLoadedFrameWriteUtc = DateTime.MinValue;
        this.browserShown = false;

        var framePath = this.GetCaptureFramePath();
        TryDeleteFile(framePath);
        TryDeleteFile(Path.Combine(this.pluginDirectory, "videosync-preview.png"));
        TryDeleteFile(this.GetSharedInfoPath());
        TryDeleteFile(this.GetSharedInfoPath() + ".tmp");
        TryDeleteFile(this.GetSharedInfoPath() + ".error.txt");
        TryDeleteFile(this.GetControlPath());
        TryDeleteFile(this.GetControlPath() + ".tmp");
        TryDeleteFile(this.GetStatusPath());
        TryDeleteFile(this.GetStatusPath() + ".tmp");
        TryDeleteFile(this.GetAudioPath());
        TryDeleteFile(this.GetAudioPath() + ".tmp");
        this.sharedTextureHandle = 0;
        this.lastSharedInfoWriteUtc = DateTime.MinValue;
        this.lastStatusReadUtc = DateTime.MinValue;
        this.playbackTime = 0;
        this.playbackDuration = 0;
        this.playbackPaused = true;
        this.lastSentVolume = -1f;
        this.lastAudioWriteUtc = DateTime.MinValue;
        this.videoFullscreen = false;

        var startInfo = new ProcessStartInfo
        {
            FileName = overlayPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(overlayPath) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--capture");
        startInfo.ArgumentList.Add(url);
        startInfo.ArgumentList.Add(framePath);
        startInfo.ArgumentList.Add("--adblock");
        startInfo.ArgumentList.Add(this.adBlockEnabled ? "enabled" : "disabled");
        if (!string.IsNullOrWhiteSpace(watch2GetherShareUrl))
        {
            startInfo.ArgumentList.Add("--w2g-share");
            startInfo.ArgumentList.Add(watch2GetherShareUrl);
        }

        if (this.presentHookProbe.TryGetGameAdapterLuid(out var adapterLuid, out var adapterName))
        {
            startInfo.ArgumentList.Add("--adapter-luid");
            startInfo.ArgumentList.Add(adapterLuid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            this.status = $"Starting renderer bridge on game GPU: {adapterName}.";
        }

        this.rendererProcess = Process.Start(startInfo);
        this.rendererStartedUtc = DateTime.UtcNow;

        return true;
    }

    private void DrawInWindowPreview()
    {
        ImGui.Text("Fallback preview (PNG snapshots; inactive while GPU streaming works)");

        if (this.rendererProcess is not null)
        {
            var running = !this.rendererProcess.HasExited;
            ImGui.Text(running ? "Renderer bridge: running" : "Renderer bridge: stopped");
            ImGui.SameLine();
            if (ImGui.Button("Stop Preview"))
            {
                this.StopInWindowPreview();
                this.status = "Stopped the renderer bridge.";
            }
        }
        else
        {
            ImGui.TextWrapped("Join or play a video to feed browser frames into this preview.");
        }

        var availableWidth = Math.Max(240, ImGui.GetContentRegionAvail().X);
        var previewWidth = Math.Min(availableWidth, 820);
        var previewSize = new Vector2(previewWidth, previewWidth * 9f / 16f);
        this.DrawScreenSurface(previewSize, showWaitingText: true);
    }

    private void TryUpdatePlaybackStatus()
    {
        var statusPath = this.GetStatusPath();
        try
        {
            if (!File.Exists(statusPath))
            {
                return;
            }

            var writeTime = File.GetLastWriteTimeUtc(statusPath);
            if (writeTime <= this.lastStatusReadUtc)
            {
                return;
            }

            // A seek/nudge was just sent; the file still reports the old position,
            // so hold the optimistic value until the player has caught up.
            if (DateTime.UtcNow < this.playbackTimeOverrideUntilUtc)
            {
                return;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statusPath));
            this.playbackTime = document.RootElement.GetProperty("t").GetDouble();
            this.playbackDuration = document.RootElement.GetProperty("d").GetDouble();
            if (DateTime.UtcNow >= this.pauseOverrideUntilUtc)
            {
                this.playbackPaused = document.RootElement.GetProperty("p").GetBoolean();
            }

            this.lastStatusReadUtc = writeTime;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            // The file is being rewritten; keep the last known state.
        }
    }

    /// <summary>
    /// The status file is only written at 2 Hz, so the raw playback time is up to
    /// half a second stale. Extrapolating from the file's write time gives a much
    /// better estimate for drift checks, broadcasts, and the seek bar.
    /// </summary>
    private double GetEstimatedPlaybackTime()
    {
        if (this.playbackPaused || this.lastStatusReadUtc == DateTime.MinValue)
        {
            return this.playbackTime;
        }

        var elapsed = Math.Clamp((DateTime.UtcNow - this.lastStatusReadUtc).TotalSeconds, 0, 5);
        var estimated = this.playbackTime + elapsed;
        return this.playbackDuration > 0
            ? Math.Min(estimated, this.playbackDuration)
            : estimated;
    }

    private void SetOptimisticPlaybackTime(double seconds)
    {
        this.playbackTime = Math.Max(0, seconds);
        this.lastStatusReadUtc = DateTime.UtcNow;
        this.playbackTimeOverrideUntilUtc = DateTime.UtcNow.AddSeconds(1.2);
    }

    private void SendPlaybackCommand(string command, double value = 0)
    {
        var seq = Interlocked.Increment(ref this.controlSeq);
        var payload =
            $"{{\"seq\":{seq},\"cmd\":\"{command}\",\"value\":{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
        try
        {
            WriteFileAtomic(this.GetControlPath(), payload);
        }
        catch (IOException)
        {
            this.status = "Could not send the playback command; is the renderer bridge still running?";
        }
    }

    private void SendNavigationCommand(string url)
    {
        var seq = Interlocked.Increment(ref this.controlSeq);
        var payload =
            $"{{\"seq\":{seq},\"cmd\":\"navigate\",\"url\":{JsonSerializer.Serialize(url)}}}";
        try
        {
            WriteFileAtomic(this.GetControlPath(), payload);
        }
        catch (IOException)
        {
            this.status = "Could not send the browser navigation command; is the renderer bridge still running?";
        }
    }

    private void SetVideoFullscreen(bool enabled)
    {
        this.videoFullscreen = enabled;
        this.SendPlaybackCommand("setvideofullscreen", enabled ? 1 : 0);
    }

    private void AllowRendererForeground()
    {
        try
        {
            if (this.rendererProcess is not null && !this.rendererProcess.HasExited)
            {
                AllowSetForegroundWindow(this.rendererProcess.Id);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Could not allow renderer foreground access.");
        }
    }

    /// <summary>
    /// Drives PyonPix-style spatial audio: volume falls off with the player's distance
    /// to the screen and the sound pans toward the screen's on-screen position.
    /// </summary>
    private void UpdateAudioBridge()
    {
        if (this.rendererProcess is null)
        {
            return;
        }

        var volume = this.audioMuted ? 0f : this.masterVolume;
        var pan = 0f;
        if (this.spatialAudio && this.worldScreenEnabled && this.worldScreenAnchor is { } anchor)
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player is not null)
            {
                const float nearDistance = 4f;
                var distance = Vector3.Distance(player.Position, anchor);
                var falloff = distance <= nearDistance
                    ? 1f
                    : Math.Max(0f, 1f - ((distance - nearDistance) / Math.Max(1f, this.audioRange - nearDistance)));
                volume *= falloff;
            }

            if (Plugin.GameGui.WorldToScreen(anchor, out var screenPos))
            {
                var display = ImGui.GetIO().DisplaySize;
                if (display.X > 0)
                {
                    pan = Math.Clamp(((screenPos.X / display.X) - 0.5f) * 1.6f, -1f, 1f);
                }
            }
        }

        var now = DateTime.UtcNow;
        if ((now - this.lastAudioWriteUtc).TotalMilliseconds < 250)
        {
            return;
        }

        if (Math.Abs(volume - this.lastSentVolume) < 0.02f &&
            Math.Abs(pan - this.lastSentPan) < 0.05f &&
            this.audioMuted == this.lastSentMuted)
        {
            return;
        }

        var payload =
            $"{{\"volume\":{volume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}," +
            $"\"pan\":{pan.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}," +
            $"\"muted\":{(this.audioMuted ? "true" : "false")}}}";
        try
        {
            WriteFileAtomic(this.GetAudioPath(), payload);
            this.lastSentVolume = volume;
            this.lastSentPan = pan;
            this.lastSentMuted = this.audioMuted;
            this.lastAudioWriteUtc = now;
        }
        catch (IOException)
        {
            // Retry on a later frame.
        }
    }

    private static void WriteFileAtomic(string path, string content)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, true);
    }

    private void DrawScreenTab(bool running)
    {
        ImGui.Spacing();

        if (running)
        {
            this.DrawPlaybackSection();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }
        else
        {
            ImGui.TextDisabled("Nothing on screen yet. Start or join a room on the Watch tab.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        UiTheme.SectionTitle("Screen placement");
        ImGui.TextDisabled("Put the floating screen where your group is looking.");
        ImGui.Spacing();

        if (UiTheme.PrimaryButton(this.worldScreenAnchor is null ? "Place screen in front of me" : "Move screen to me"))
        {
            this.PlaceWorldScreenInFrontOfPlayer();
            this.EnableNativeWorldScreen();
            this.QueueSnowSyncBroadcast();
        }

        ImGui.SameLine();
        if (ImGui.Button("Turn toward me"))
        {
            this.FaceWorldScreenToPlayer();
            this.QueueSnowSyncBroadcast();
        }

        ImGui.SameLine();
        if (this.worldScreenEnabled)
        {
            if (UiTheme.DangerButton("Hide screen"))
            {
                this.worldScreenEnabled = false;
                this.presentHookProbe.NativeTestDrawEnabled = false;
                this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
                this.presentHookProbe.ClearNativeQuad();
                this.status = "Hid the screen. Show it again any time.";
                this.QueueSnowSyncBroadcast();
            }
        }
        else if (ImGui.Button("Show screen"))
        {
            if (this.worldScreenAnchor is null)
            {
                this.PlaceWorldScreenInFrontOfPlayer();
            }

            this.EnableNativeWorldScreen();
            this.QueueSnowSyncBroadcast();
        }

        ImGui.Spacing();
        UiTheme.SectionTitle("Size");
        ImGui.Spacing();
        ImGui.PushItemWidth(-130f);
        UiTheme.PushSliderAccent();
        if (ImGui.SliderFloat("Width", ref this.worldScreenWidth, 1f, 14f, "%.1f yalms"))
        {
            if (this.worldScreenLockAspect)
            {
                this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
            }

            this.QueueSnowSyncBroadcast();
        }

        var aspectLocked = this.worldScreenLockAspect;
        if (aspectLocked)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.SliderFloat("Height", ref this.worldScreenHeight, 0.5f, 10f, "%.1f yalms"))
        {
            this.QueueSnowSyncBroadcast();
        }

        if (aspectLocked)
        {
            ImGui.EndDisabled();
        }

        UiTheme.PopSliderAccent();
        ImGui.PopItemWidth();

        if (ImGui.Checkbox("Lock to 16:9", ref this.worldScreenLockAspect) && this.worldScreenLockAspect)
        {
            this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
            this.QueueSnowSyncBroadcast();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Turn off to stretch the picture. Width and Height then move independently.");
        }

        ImGui.Spacing();
        UiTheme.SectionTitle("Position");
        ImGui.Spacing();
        ImGui.PushItemWidth(-130f);
        UiTheme.PushSliderAccent();

        var elevation = this.worldScreenElevation;
        if (ImGui.SliderFloat("Elevation", ref elevation, -4f, 8f, "%.1f yalms"))
        {
            if (this.worldScreenAnchor is { } elevated)
            {
                this.worldScreenAnchor = elevated + new Vector3(0f, elevation - this.worldScreenElevation, 0f);
            }

            this.worldScreenElevation = elevation;
            this.QueueSnowSyncBroadcast();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Raise or lower the screen from where it was placed.");
        }

        var push = this.worldScreenPush;
        if (ImGui.SliderFloat("Distance", ref push, -6f, 14f, "%.1f yalms"))
        {
            if (this.worldScreenAnchor is { } pushed)
            {
                this.worldScreenAnchor = pushed + (this.GetViewerAwayAxis() * (push - this.worldScreenPush));
            }

            this.worldScreenPush = push;
            this.QueueSnowSyncBroadcast();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Push the screen closer or farther from you.");
        }

        var yawDegrees = RadiansToDegrees(this.worldScreenRotation);
        if (ImGui.SliderFloat("Facing angle", ref yawDegrees, -180f, 180f, "%.0f deg"))
        {
            this.worldScreenRotation = DegreesToRadians(yawDegrees);
            this.QueueSnowSyncBroadcast();
        }

        UiTheme.PopSliderAccent();
        ImGui.PopItemWidth();

        ImGui.Spacing();
        if (ImGui.TreeNode("Fine position"))
        {
            if (UiTheme.IconButton(FontAwesomeIcon.ArrowLeft, "videosync-nudge-left", new Vector2(32f, 0f), "Nudge left"))
            {
                this.NudgeScreenFromViewer(-0.25f, 0f, 0f);
            }

            ImGui.SameLine();
            if (UiTheme.IconButton(FontAwesomeIcon.ArrowRight, "videosync-nudge-right", new Vector2(32f, 0f), "Nudge right"))
            {
                this.NudgeScreenFromViewer(0.25f, 0f, 0f);
            }

            ImGui.SameLine();
            if (UiTheme.IconButton(FontAwesomeIcon.ArrowUp, "videosync-nudge-up", new Vector2(32f, 0f), "Raise"))
            {
                this.NudgeScreenFromViewer(0f, 0.25f, 0f);
            }

            ImGui.SameLine();
            if (UiTheme.IconButton(FontAwesomeIcon.ArrowDown, "videosync-nudge-down", new Vector2(32f, 0f), "Lower"))
            {
                this.NudgeScreenFromViewer(0f, -0.25f, 0f);
            }

            ImGui.SameLine();
            if (ImGui.Button("Closer"))
            {
                this.NudgeScreenFromViewer(0f, 0f, -0.25f);
            }

            ImGui.SameLine();
            if (ImGui.Button("Away"))
            {
                this.NudgeScreenFromViewer(0f, 0f, 0.25f);
            }

            if (this.worldScreenAnchor is { } anchor)
            {
                ImGui.TextDisabled($"Anchor: X {anchor.X:0.00}, Y {anchor.Y:0.00}, Z {anchor.Z:0.00}");
            }
            else
            {
                ImGui.TextDisabled("The screen has not been placed yet.");
            }

            ImGui.TreePop();
        }

        if (ImGui.TreeNode("Compatibility"))
        {
            var useFallback = this.drawImguiWorldScreen;
            if (ImGui.Checkbox("Use the 2D fallback screen", ref useFallback))
            {
                this.drawImguiWorldScreen = useFallback;
                this.userChose2DFallback = useFallback;
                if (useFallback)
                {
                    this.worldScreenActorOcclusion = true;
                }
                else
                {
                    this.EnableNativeWorldScreen();
                }

                this.QueueSnowSyncBroadcast();
            }

            ImGui.TextDisabled("The 3D screen is the normal world object. Use 2D only if native hooks will not load.");
            if (this.drawImguiWorldScreen)
            {
                if (ImGui.Checkbox("Hide the screen behind players", ref this.worldScreenActorOcclusion))
                {
                    this.QueueSnowSyncBroadcast();
                }

                if (ImGui.SliderFloat("Cutout padding", ref this.worldScreenOcclusionPadding, 0f, 48f, "%.0f px"))
                {
                    this.QueueSnowSyncBroadcast();
                }
            }

            ImGui.TreePop();
        }
    }

    private void DrawStyleTab()
    {
        ImGui.Spacing();
        UiTheme.SectionTitle("TV look");
        ImGui.Spacing();

        if (ImGui.BeginCombo("Frame", FrameStyleNames[Math.Clamp(this.tvFrameStyle, 0, FrameStyleNames.Length - 1)]))
        {
            for (var i = 0; i < FrameStyleNames.Length; i++)
            {
                var selected = this.tvFrameStyle == i;
                if (ImGui.Selectable(FrameStyleNames[i], selected))
                {
                    this.tvFrameStyle = i;
                    this.status = i == 0 ? "Using the generic frameless TV." : $"Using {FrameStyleNames[i]} frame.";
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        UiTheme.PushSliderAccent();
        if (ImGui.SliderFloat("Frame thickness", ref this.frameThickness, 0.04f, 0.42f, "%.2f yalms"))
        {
            this.frameThickness = Math.Clamp(this.frameThickness, 0.04f, 0.42f);
        }

        UiTheme.PopSliderAccent();

        ImGui.Spacing();
        UiTheme.SectionTitle("Ambient glow");
        ImGui.Spacing();
        ImGui.Checkbox("Glow", ref this.ambientGlowEnabled);
        if (this.ambientGlowEnabled)
        {
            ImGui.ColorEdit4("Glow color", ref this.ambientGlowColor);
            UiTheme.PushSliderAccent();
            ImGui.SliderFloat("Glow intensity", ref this.ambientGlowIntensity, 0.05f, 0.85f, "%.2f");
            ImGui.SliderFloat("Glow spread", ref this.ambientGlowSize, 0.05f, 1.2f, "%.2f yalms");
            UiTheme.PopSliderAccent();
        }

        ImGui.Spacing();
        UiTheme.SectionTitle("Cinema presets");
        ImGui.Spacing();

        if (ImGui.BeginCombo("Preset", CinemaPresetNames[Math.Clamp(this.cinemaPresetIndex, 0, CinemaPresetNames.Length - 1)]))
        {
            for (var i = 0; i < CinemaPresetNames.Length; i++)
            {
                var selected = this.cinemaPresetIndex == i;
                if (ImGui.Selectable(CinemaPresetNames[i], selected))
                {
                    this.cinemaPresetIndex = i;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (UiTheme.PrimaryButton("Apply preset"))
        {
            this.ApplyCinemaPreset(this.cinemaPresetIndex);
        }

        ImGui.SameLine();
        if (ImGui.Button("Generic TV"))
        {
            this.ApplyCinemaPreset(0);
        }
    }

    private void ApplyCinemaPreset(int index)
    {
        this.cinemaPresetIndex = Math.Clamp(index, 0, CinemaPresetNames.Length - 1);
        switch (this.cinemaPresetIndex)
        {
            case 0:
                this.tvFrameStyle = 0;
                this.ambientGlowEnabled = false;
                this.worldScreenLockAspect = true;
                this.worldScreenWidth = 4.0f;
                this.worldScreenHeight = 2.25f;
                this.frameThickness = 0.16f;
                this.audioRange = 30f;
                break;
            case 1:
                this.tvFrameStyle = 1;
                this.ambientGlowEnabled = true;
                this.ambientGlowColor = new Vector4(1.0f, 0.63f, 0.28f, 0.34f);
                this.ambientGlowIntensity = 0.30f;
                this.ambientGlowSize = 0.34f;
                this.worldScreenLockAspect = true;
                this.worldScreenWidth = 4.8f;
                this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
                this.frameThickness = 0.20f;
                this.audioRange = 28f;
                break;
            case 2:
                this.tvFrameStyle = 3;
                this.ambientGlowEnabled = true;
                this.ambientGlowColor = new Vector4(0.15f, 0.85f, 1.0f, 0.48f);
                this.ambientGlowIntensity = 0.52f;
                this.ambientGlowSize = 0.58f;
                this.worldScreenLockAspect = true;
                this.worldScreenWidth = 5.6f;
                this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
                this.frameThickness = 0.10f;
                this.audioRange = 45f;
                break;
            case 3:
                this.tvFrameStyle = 2;
                this.ambientGlowEnabled = true;
                this.ambientGlowColor = new Vector4(0.34f, 0.66f, 1.0f, 0.36f);
                this.ambientGlowIntensity = 0.38f;
                this.ambientGlowSize = 0.62f;
                this.worldScreenLockAspect = true;
                this.worldScreenWidth = 7.0f;
                this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
                this.frameThickness = 0.24f;
                this.audioRange = 70f;
                break;
            case 4:
                this.tvFrameStyle = 4;
                this.ambientGlowEnabled = true;
                this.ambientGlowColor = new Vector4(0.12f, 0.95f, 0.78f, 0.42f);
                this.ambientGlowIntensity = 0.44f;
                this.ambientGlowSize = 0.46f;
                this.worldScreenLockAspect = true;
                this.worldScreenWidth = 6.2f;
                this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
                this.frameThickness = 0.18f;
                this.audioRange = 42f;
                break;
        }

        this.lastSentVolume = -1f;
        this.lastAudioWriteUtc = DateTime.MinValue;
        this.status = $"Applied {CinemaPresetNames[this.cinemaPresetIndex]} preset.";
    }

    private void DrawSettingsTab(bool running)
    {
        ImGui.Spacing();

        // Primary setting: the one thing most people ever touch here.
        UiTheme.SectionTitle("Watch2Gether account");
        ImGui.TextWrapped("Your free key lets you create rooms. Get one from w2g.tv → Edit Profile → API Access.");
        this.DrawApiKeyInput();
        if (ImGui.SmallButton("Get a key"))
        {
            this.OpenUrl("https://w2g.tv/");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Everything below is niche, so it stays collapsed by default and out of the way.
        if (ImGui.CollapsingHeader("Legacy party sync (chat channel)"))
        {
            ImGui.Spacing();
            this.DrawLegacyPartySync(running);
            ImGui.Spacing();
        }

        if (ImGui.CollapsingHeader("Diagnostics & troubleshooting"))
        {
            ImGui.Spacing();
            this.DrawDiagnostics();
        }
    }

    private void DrawDiagnostics()
    {
        ImGui.TextDisabled(this.BuildTechStatusLine());
        ImGui.Spacing();

        if (ImGui.Button("Pop-out player window"))
        {
            this.SurfaceWindow.IsOpen = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Test fresh receive"))
        {
            this.TestReceiveSnowSync();
        }

        ImGui.SameLine();
        if (ImGui.Button("Open pasted code in overlay"))
        {
            if (TryDecode(this.pasteCode, out var payload, out var error))
            {
                this.OpenInOverlay(payload);
            }
            else
            {
                this.status = error;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy native status"))
        {
            ImGui.SetClipboardText(this.presentHookProbe.Status);
            this.status = "Copied the native renderer status to the clipboard.";
        }

        ImGui.Spacing();
        ImGui.Separator();
        UiTheme.SectionTitle("Renderer diagnostics");
        this.DrawRealRendererProbe();

        ImGui.Separator();
        UiTheme.SectionTitle("Fallback preview");
        this.DrawInWindowPreview();
    }

    private void DrawRealRendererProbe()
    {
        ImGui.TextWrapped("The 3D screen installs native D3D hooks automatically when you place it. These controls exist only for troubleshooting.");

        if (ImGui.Button("Probe Real Renderer"))
        {
            this.realRendererProbe = RenderProbe.Capture();
            this.status = "Captured real renderer probe data. If the key pointers are non-null, the next step is a D3D draw hook.";
        }

        ImGui.SameLine();
        if (ImGui.Button("Install Native Hooks"))
        {
            this.status = this.presentHookProbe.TryInstall()
                ? "Installed native D3D hooks. If frame and target-bind counts climb, we can start drawing against real scene depth."
                : "Could not install the native D3D hooks yet. Check the renderer probe output.";
            Plugin.ChatGui.Print($"[VideoSync] {this.status}");
        }

        ImGui.SameLine();
        if (ImGui.Button("Disable Native Hooks"))
        {
            this.presentHookProbe.Disable();
            this.status = "Disabled the native D3D hooks.";
            Plugin.ChatGui.Print($"[VideoSync] {this.status}");
        }

        var nativeTestDraw = this.presentHookProbe.NativeTestDrawEnabled;
        if (ImGui.Checkbox("Native test screen quad", ref nativeTestDraw))
        {
            this.presentHookProbe.NativeTestDrawEnabled = nativeTestDraw;
            this.status = nativeTestDraw
                ? "Enabled the native D3D screen quad. It should follow the world screen placement as a translucent green rectangle."
                : "Disabled the native D3D screen quad.";
            Plugin.ChatGui.Print($"[VideoSync] {this.status}");
        }

        var nativeScreenProbe = this.presentHookProbe.NativeScreenSpaceProbeEnabled;
        if (ImGui.Checkbox("Native screen-space probe", ref nativeScreenProbe))
        {
            this.presentHookProbe.NativeScreenSpaceProbeEnabled = nativeScreenProbe;
            this.status = nativeScreenProbe
                ? "Enabled the native screen-space probe. Look for a magenta rectangle over the game."
                : "Disabled the native screen-space probe.";
            Plugin.ChatGui.Print($"[VideoSync] {this.status}");
        }

        ImGui.InputTextMultiline("Renderer Probe", ref this.realRendererProbe, 4096, new Vector2(-1, 150), ImGuiInputTextFlags.ReadOnly);
        ImGui.TextWrapped(this.presentHookProbe.Status);
    }

    private void PlaceWorldScreenInFrontOfPlayer()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
        {
            this.status = "Could not place the world screen because no local player was found.";
            return;
        }

        var rotation = player.Rotation;
        var forward = new Vector3(MathF.Sin(rotation), 0f, MathF.Cos(rotation));
        this.worldScreenAnchor = player.Position + (forward * this.worldScreenDistance) + new Vector3(0f, this.worldScreenHeightOffset, 0f);
        this.worldScreenRotation = rotation + MathF.PI;
        this.worldScreenElevation = 0f;
        this.worldScreenPush = 0f;
        this.status = "Placed the world screen in front of your character.";
    }

    private void EnableNativeWorldScreen()
    {
        if (this.worldScreenAnchor is null)
        {
            this.worldScreenEnabled = false;
            this.status = "Could not spawn the screen because no world anchor was available.";
            Plugin.ChatGui.Print($"[VideoSync] {this.status}");
            return;
        }

        this.worldScreenEnabled = true;
        this.drawImguiWorldScreen = false;

        if (this.presentHookProbe.TryInstall())
        {
            this.presentHookProbe.NativeTestDrawEnabled = true;
            this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
            this.status = "Spawned the native world screen at your character. The magenta probe is off.";
        }
        else
        {
            this.presentHookProbe.NativeTestDrawEnabled = false;
            this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
            this.drawImguiWorldScreen = true;
            this.worldScreenActorOcclusion = true;
            this.status = "Placed the screen, but native D3D hooks did not install. Open the renderer diagnostics for details.";
        }

        Plugin.ChatGui.Print($"[VideoSync] {this.status}");
    }

    private void FaceWorldScreenToPlayer()
    {
        if (this.worldScreenAnchor is not { } anchor)
        {
            this.status = "Place the world screen before facing it.";
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
        {
            this.status = "Could not face the world screen because no local player was found.";
            return;
        }

        var delta = player.Position - anchor;
        this.worldScreenRotation = MathF.Atan2(delta.X, delta.Z);
        this.status = "Turned the world screen toward your character.";
    }

    /// <summary>
    /// Nudges the screen relative to how the player sees it: horizontal moves it
    /// left/right on their monitor, depth moves it toward/away from them.
    /// </summary>
    private void NudgeScreenFromViewer(float horizontal, float vertical, float depth)
    {
        if (this.worldScreenAnchor is not { } anchor)
        {
            this.status = "Place the screen before fine-tuning its position.";
            return;
        }

        var side = new Vector3(MathF.Cos(this.worldScreenRotation), 0f, -MathF.Sin(this.worldScreenRotation));
        var away = Vector3.Zero;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is not null)
        {
            var flat = anchor - player.Position;
            flat.Y = 0f;
            if (flat.LengthSquared() > 0.01f)
            {
                away = Vector3.Normalize(flat);
                side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, flat));
            }
        }

        // Flip "right" if needed so the arrows match what the viewer sees on screen.
        if (horizontal != 0f &&
            Plugin.GameGui.WorldToScreen(anchor, out var center) &&
            Plugin.GameGui.WorldToScreen(anchor + side, out var offsetPoint) &&
            offsetPoint.X < center.X)
        {
            side = -side;
        }

        this.worldScreenAnchor = anchor
            + (side * horizontal)
            + new Vector3(0f, vertical, 0f)
            + (away * depth);
        this.worldScreenElevation += vertical;
        this.worldScreenPush += depth;
        this.QueueSnowSyncBroadcast();
    }

    private Vector3 GetViewerAwayAxis()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is not null && this.worldScreenAnchor is { } anchor)
        {
            var flat = anchor - player.Position;
            flat.Y = 0f;
            if (flat.LengthSquared() > 0.01f)
            {
                return Vector3.Normalize(flat);
            }
        }

        return new Vector3(MathF.Sin(this.worldScreenRotation), 0f, MathF.Cos(this.worldScreenRotation));
    }

    private IReadOnlyList<NativeDecorQuad> BuildWorldScreenDecorations(
        Vector3 anchor,
        Vector3 right,
        Vector3 up,
        float halfWidth,
        float halfHeight)
    {
        var quads = new List<NativeDecorQuad>(16);

        if (this.ambientGlowEnabled)
        {
            var spread = Math.Clamp(this.ambientGlowSize, 0.05f, 1.2f);
            var alpha = Math.Clamp(this.ambientGlowIntensity, 0.05f, 0.85f);
            var color = this.ambientGlowColor;
            AddRectQuad(
                quads,
                anchor,
                right,
                up,
                -halfWidth - (spread * 1.45f),
                halfWidth + (spread * 1.45f),
                -halfHeight - (spread * 1.1f),
                halfHeight + (spread * 1.1f),
                new Vector4(color.X, color.Y, color.Z, alpha * 0.22f),
                0.16f);
            AddRectQuad(
                quads,
                anchor,
                right,
                up,
                -halfWidth - spread,
                halfWidth + spread,
                -halfHeight - (spread * 0.72f),
                halfHeight + (spread * 0.72f),
                new Vector4(color.X, color.Y, color.Z, alpha * 0.36f),
                0.14f);
        }

        var t = Math.Clamp(this.frameThickness, 0.04f, 0.42f);
        switch (this.tvFrameStyle)
        {
            case 1:
                AddFrameBands(quads, anchor, right, up, halfWidth, halfHeight, t, new Vector4(0.30f, 0.15f, 0.07f, 1f));
                AddFrameBands(quads, anchor, right, up, halfWidth + (t * 0.08f), halfHeight + (t * 0.08f), t * 0.22f, new Vector4(0.72f, 0.48f, 0.22f, 1f));
                AddRectQuad(quads, anchor, right, up, -halfWidth - (t * 1.35f), halfWidth + (t * 1.35f), -halfHeight - (t * 1.55f), -halfHeight - (t * 0.95f), new Vector4(0.14f, 0.07f, 0.035f, 1f), 0.11f);
                break;
            case 2:
                AddFrameBands(quads, anchor, right, up, halfWidth, halfHeight, t, new Vector4(0.10f, 0.12f, 0.14f, 1f));
                AddFrameBands(quads, anchor, right, up, halfWidth + (t * 0.18f), halfHeight + (t * 0.18f), t * 0.18f, new Vector4(0.34f, 0.58f, 0.74f, 1f));
                AddCornerPlates(quads, anchor, right, up, halfWidth, halfHeight, t, new Vector4(0.20f, 0.23f, 0.26f, 1f));
                break;
            case 3:
                AddFrameBands(quads, anchor, right, up, halfWidth, halfHeight, t, new Vector4(0.015f, 0.016f, 0.020f, 1f));
                AddRectQuad(quads, anchor, right, up, -halfWidth - (t * 1.38f), halfWidth + (t * 1.38f), halfHeight + (t * 1.02f), halfHeight + (t * 1.34f), new Vector4(0.05f, 0.85f, 1.0f, 1f), 0.10f);
                AddRectQuad(quads, anchor, right, up, -halfWidth - (t * 1.38f), halfWidth + (t * 1.38f), -halfHeight - (t * 1.34f), -halfHeight - (t * 1.02f), new Vector4(1.0f, 0.12f, 0.72f, 1f), 0.10f);
                AddRectQuad(quads, anchor, right, up, -halfWidth - (t * 1.34f), -halfWidth - (t * 1.02f), -halfHeight - (t * 1.15f), halfHeight + (t * 1.15f), new Vector4(0.65f, 0.28f, 1.0f, 1f), 0.10f);
                AddRectQuad(quads, anchor, right, up, halfWidth + (t * 1.02f), halfWidth + (t * 1.34f), -halfHeight - (t * 1.15f), halfHeight + (t * 1.15f), new Vector4(0.06f, 1.0f, 0.70f, 1f), 0.10f);
                break;
            case 4:
                AddFrameBands(quads, anchor, right, up, halfWidth, halfHeight, t, new Vector4(0.05f, 0.20f, 0.18f, 1f));
                AddFrameBands(quads, anchor, right, up, halfWidth + (t * 0.16f), halfHeight + (t * 0.16f), t * 0.20f, new Vector4(0.86f, 0.64f, 0.28f, 1f));
                AddCornerPlates(quads, anchor, right, up, halfWidth, halfHeight, t * 1.15f, new Vector4(0.60f, 0.42f, 0.18f, 1f));
                AddRectQuad(quads, anchor, right, up, -halfWidth * 0.42f, halfWidth * 0.42f, halfHeight + (t * 1.12f), halfHeight + (t * 1.36f), new Vector4(0.12f, 0.95f, 0.78f, 1f), 0.10f);
                break;
        }

        return quads;
    }

    private static void AddFrameBands(
        List<NativeDecorQuad> quads,
        Vector3 anchor,
        Vector3 right,
        Vector3 up,
        float halfWidth,
        float halfHeight,
        float thickness,
        Vector4 color)
    {
        var outerWidth = halfWidth + thickness;
        var outerHeight = halfHeight + thickness;
        AddRectQuad(quads, anchor, right, up, -outerWidth, outerWidth, halfHeight, outerHeight, color, 0.11f);
        AddRectQuad(quads, anchor, right, up, -outerWidth, outerWidth, -outerHeight, -halfHeight, color, 0.11f);
        AddRectQuad(quads, anchor, right, up, -outerWidth, -halfWidth, -halfHeight, halfHeight, color, 0.11f);
        AddRectQuad(quads, anchor, right, up, halfWidth, outerWidth, -halfHeight, halfHeight, color, 0.11f);
    }

    private static void AddCornerPlates(
        List<NativeDecorQuad> quads,
        Vector3 anchor,
        Vector3 right,
        Vector3 up,
        float halfWidth,
        float halfHeight,
        float thickness,
        Vector4 color)
    {
        var outerWidth = halfWidth + (thickness * 1.45f);
        var innerWidth = halfWidth + (thickness * 0.28f);
        var outerHeight = halfHeight + (thickness * 1.45f);
        var innerHeight = halfHeight + (thickness * 0.28f);
        AddRectQuad(quads, anchor, right, up, -outerWidth, -innerWidth, innerHeight, outerHeight, color, 0.09f);
        AddRectQuad(quads, anchor, right, up, innerWidth, outerWidth, innerHeight, outerHeight, color, 0.09f);
        AddRectQuad(quads, anchor, right, up, -outerWidth, -innerWidth, -outerHeight, -innerHeight, color, 0.09f);
        AddRectQuad(quads, anchor, right, up, innerWidth, outerWidth, -outerHeight, -innerHeight, color, 0.09f);
    }

    private static void AddRectQuad(
        List<NativeDecorQuad> quads,
        Vector3 anchor,
        Vector3 right,
        Vector3 up,
        float left,
        float rightEdge,
        float bottom,
        float top,
        Vector4 color,
        float depthOffset)
    {
        quads.Add(new NativeDecorQuad(
            anchor + (right * left) + (up * top),
            anchor + (right * rightEdge) + (up * top),
            anchor + (right * rightEdge) + (up * bottom),
            anchor + (right * left) + (up * bottom),
            color,
            depthOffset));
    }

    public void DrawWorldSurfaceOverlay()
    {
        this.UpdateAudioBridge();
        this.TickNativeScreenRecovery();

        if (!this.worldScreenEnabled || this.worldScreenAnchor is not { } anchor)
        {
            this.presentHookProbe.ClearNativeQuad();
            this.presentHookProbe.SetNativeTexture(0);
            return;
        }

        if (this.worldScreenLockAspect)
        {
            this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
        }

        var halfWidth = Math.Max(0.5f, this.worldScreenWidth) * 0.5f;
        var halfHeight = Math.Max(0.25f, this.worldScreenHeight) * 0.5f;
        var right = new Vector3(MathF.Cos(this.worldScreenRotation), 0f, -MathF.Sin(this.worldScreenRotation));
        var up = new Vector3(0f, 1f, 0f);

        var topLeftWorld = anchor - (right * halfWidth) + (up * halfHeight);
        var topRightWorld = anchor + (right * halfWidth) + (up * halfHeight);
        var bottomRightWorld = anchor + (right * halfWidth) - (up * halfHeight);
        var bottomLeftWorld = anchor - (right * halfWidth) - (up * halfHeight);

        // The native scene-pass renderer clips against the camera itself, so the quad
        // and video texture are always kept up to date, even when corners are off screen.
        this.TryUpdateFrameTexture();
        this.TryUpdateSharedTexture();
        this.presentHookProbe.SetNativeQuad(topLeftWorld, topRightWorld, bottomRightWorld, bottomLeftWorld);
        this.presentHookProbe.SetNativeDecorations(this.BuildWorldScreenDecorations(anchor, right, up, halfWidth, halfHeight));
        this.presentHookProbe.SetNativeTexture(
            this.frameTexture is null ? 0 : (nint)this.frameTexture.Handle.Handle);
        this.presentHookProbe.SetNativeSharedTexture(this.sharedTextureHandle);

        if (!this.drawImguiWorldScreen)
        {
            return;
        }

        if (!Plugin.GameGui.WorldToScreen(topLeftWorld, out var topLeft) ||
            !Plugin.GameGui.WorldToScreen(topRightWorld, out var topRight) ||
            !Plugin.GameGui.WorldToScreen(bottomRightWorld, out var bottomRight) ||
            !Plugin.GameGui.WorldToScreen(bottomLeftWorld, out var bottomLeft))
        {
            return;
        }
        var drawList = ImGui.GetBackgroundDrawList();
        var clipRects = this.GetWorldScreenVisibleClipRects(
            anchor,
            right,
            up,
            halfWidth,
            halfHeight,
            topLeft,
            topRight,
            bottomRight,
            bottomLeft);

        if (this.frameTexture is not null)
        {
            foreach (var clipRect in clipRects)
            {
                drawList.PushClipRect(clipRect.Min, clipRect.Max, true);
                drawList.AddImageQuad(
                    this.frameTexture.Handle,
                    topLeft,
                    topRight,
                    bottomRight,
                    bottomLeft,
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, 1f),
                    0xFFFFFFFF);
                drawList.PopClipRect();
            }
        }
        else
        {
            foreach (var clipRect in clipRects)
            {
                drawList.PushClipRect(clipRect.Min, clipRect.Max, true);
                drawList.AddQuadFilled(topLeft, topRight, bottomRight, bottomLeft, 0xFF050505);
                drawList.PopClipRect();
            }
        }

        drawList.AddQuad(topLeft, topRight, bottomRight, bottomLeft, 0xAA1F1F1F, 2f);
    }

    private void TickNativeScreenRecovery()
    {
        if (!this.worldScreenEnabled || !this.drawImguiWorldScreen || this.userChose2DFallback)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - this.lastNativeInstallRetryUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        this.lastNativeInstallRetryUtc = now;
        if (!this.presentHookProbe.TryInstall())
        {
            return;
        }

        this.drawImguiWorldScreen = false;
        this.presentHookProbe.NativeTestDrawEnabled = true;
        this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
        this.status = "Switched to the 3D screen. Your character should now block it correctly.";
    }

    private List<ScreenRect> GetWorldScreenVisibleClipRects(
        Vector3 anchor,
        Vector3 right,
        Vector3 up,
        float halfWidth,
        float halfHeight,
        Vector2 topLeft,
        Vector2 topRight,
        Vector2 bottomRight,
        Vector2 bottomLeft)
    {
        var bounds = ScreenRect.FromPoints(topLeft, topRight, bottomRight, bottomLeft).Inflate(2f);
        var visibleRects = new List<ScreenRect> { bounds };
        if (!this.worldScreenActorOcclusion)
        {
            return visibleRects;
        }

        var normal = new Vector3(MathF.Sin(this.worldScreenRotation), 0f, MathF.Cos(this.worldScreenRotation));
        for (var i = 0; i < Plugin.ObjectTable.Length; i++)
        {
            var gameObject = Plugin.ObjectTable[i];
            if (gameObject is null)
            {
                continue;
            }

            var position = gameObject.Position;
            var delta = position - anchor;
            var frontDistance = Vector3.Dot(delta, normal);
            if (frontDistance is < -0.25f or > 12f)
            {
                continue;
            }

            var radius = Math.Clamp(gameObject.HitboxRadius, 0.25f, 2.5f);
            var objectX = Vector3.Dot(delta, right);
            var objectFootY = Vector3.Dot(delta, up);
            var objectHeight = Math.Clamp(radius * 3.2f, 1.4f, 4.0f);
            var objectHeadY = objectFootY + objectHeight;
            if (Math.Abs(objectX) > halfWidth + radius ||
                objectHeadY < -halfHeight ||
                objectFootY > halfHeight)
            {
                continue;
            }

            var footWorld = position;
            var headWorld = position + new Vector3(0f, objectHeight, 0f);
            if (!Plugin.GameGui.WorldToScreen(footWorld, out var footScreen) ||
                !Plugin.GameGui.WorldToScreen(headWorld, out var headScreen))
            {
                continue;
            }

            var heightPixels = Math.Abs(footScreen.Y - headScreen.Y);
            if (heightPixels < 12f)
            {
                continue;
            }

            var widthPixels = Math.Clamp(heightPixels * 0.45f, 24f, 220f) + (this.worldScreenOcclusionPadding * 2f);
            var min = new Vector2(
                Math.Min(footScreen.X, headScreen.X) - (widthPixels * 0.5f),
                Math.Min(footScreen.Y, headScreen.Y) - this.worldScreenOcclusionPadding);
            var max = new Vector2(
                Math.Max(footScreen.X, headScreen.X) + (widthPixels * 0.5f),
                Math.Max(footScreen.Y, headScreen.Y) + this.worldScreenOcclusionPadding);
            var occluder = new ScreenRect(min, max).Intersect(bounds);
            if (occluder.IsEmpty)
            {
                continue;
            }

            visibleRects = SubtractRect(visibleRects, occluder);
            if (visibleRects.Count == 0)
            {
                break;
            }
        }

        return visibleRects;
    }

    private static List<ScreenRect> SubtractRect(List<ScreenRect> source, ScreenRect cutout)
    {
        var result = new List<ScreenRect>();
        foreach (var rect in source)
        {
            if (!rect.Intersects(cutout))
            {
                result.Add(rect);
                continue;
            }

            var intersection = rect.Intersect(cutout);
            if (intersection.Min.Y > rect.Min.Y)
            {
                result.Add(new ScreenRect(rect.Min, new Vector2(rect.Max.X, intersection.Min.Y)));
            }

            if (intersection.Max.Y < rect.Max.Y)
            {
                result.Add(new ScreenRect(new Vector2(rect.Min.X, intersection.Max.Y), rect.Max));
            }

            if (intersection.Min.X > rect.Min.X)
            {
                result.Add(new ScreenRect(
                    new Vector2(rect.Min.X, intersection.Min.Y),
                    new Vector2(intersection.Min.X, intersection.Max.Y)));
            }

            if (intersection.Max.X < rect.Max.X)
            {
                result.Add(new ScreenRect(
                    new Vector2(intersection.Max.X, intersection.Min.Y),
                    new Vector2(rect.Max.X, intersection.Max.Y)));
            }
        }

        return result;
    }

    public void DrawScreenSurface()
    {
        var available = ImGui.GetContentRegionAvail();
        var width = Math.Max(260, available.X);
        var height = Math.Max(160, available.Y);
        var fitWidth = Math.Min(width, height * 16f / 9f);
        var fitHeight = fitWidth * 9f / 16f;
        if (fitHeight > height)
        {
            fitHeight = height;
            fitWidth = fitHeight * 16f / 9f;
        }

        var offset = new Vector2(Math.Max(0, (width - fitWidth) * 0.5f), Math.Max(0, (height - fitHeight) * 0.5f));
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, height));
        ImGui.SetCursorScreenPos(cursor + offset);
        this.DrawScreenSurface(new Vector2(fitWidth, fitHeight), showWaitingText: false);
    }

    private void DrawScreenSurface(Vector2 previewSize, bool showWaitingText)
    {
        this.TryUpdateFrameTexture();

        if (this.frameTexture is not null)
        {
            ImGui.Image(this.frameTexture.Handle, previewSize);
        }
        else
        {
            var cursor = ImGui.GetCursorScreenPos();
            ImGui.Dummy(previewSize);
            var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;
            if (running)
            {
                // Renderer is up, first frame not here yet: show the loading animation.
                this.DrawLoadingSurface(cursor, previewSize);
            }
            else
            {
                var drawList = ImGui.GetWindowDrawList();
                drawList.AddRectFilled(cursor, cursor + previewSize, 0xFF050505, 2f);
                drawList.AddRect(cursor, cursor + previewSize, 0xAA1F1F1F, 2f);
                if (showWaitingText)
                {
                    ImGui.SetCursorScreenPos(cursor + new Vector2(12, 12));
                    ImGui.TextWrapped("Nothing is playing yet.");
                }
            }

            ImGui.SetCursorScreenPos(cursor + new Vector2(0, previewSize.Y + 4));
        }
    }

    private void TryUpdateFrameTexture()
    {
        if (this.frameTextureTask is { IsCompleted: true })
        {
            try
            {
                var nextTexture = this.frameTextureTask.GetAwaiter().GetResult();
                this.frameTexture?.Dispose();
                this.frameTexture = nextTexture;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Could not load captured renderer frame.");
            }
            finally
            {
                this.frameTextureTask = null;
            }
        }

        if (this.frameTextureTask is not null)
        {
            return;
        }

        var framePath = this.GetCaptureFramePath();
        if (!File.Exists(framePath))
        {
            return;
        }

        DateTime writeTime;
        byte[] bytes;
        try
        {
            writeTime = File.GetLastWriteTimeUtc(framePath);
            if (writeTime <= this.lastLoadedFrameWriteUtc)
            {
                return;
            }

            bytes = File.ReadAllBytes(framePath);
        }
        catch (IOException)
        {
            return;
        }

        this.lastLoadedFrameWriteUtc = writeTime;
        this.frameTextureTask = Plugin.TextureProvider.CreateFromImageAsync(bytes, $"VideoSyncFrame-{writeTime.Ticks}", CancellationToken.None);
    }

    private void StopInWindowPreview()
    {
        this.StopRendererProcess();
        this.frameTexture?.Dispose();
        this.frameTexture = null;
        this.frameTextureTask = null;
        this.sharedTextureHandle = 0;
        this.lastSharedInfoWriteUtc = DateTime.MinValue;
        this.presentHookProbe.SetNativeTexture(0);
        this.presentHookProbe.SetNativeSharedTexture(0);
        this.SurfaceWindow.IsOpen = false;
    }

    private void TickRendererHealth()
    {
        if (this.rendererProcess is null || !this.rendererProcess.HasExited)
        {
            return;
        }

        this.rendererProcess.Dispose();
        this.rendererProcess = null;
        this.sharedTextureHandle = 0;
        this.lastSharedInfoWriteUtc = DateTime.MinValue;
        this.presentHookProbe.SetNativeSharedTexture(0);
        this.presentHookProbe.SetNativeTexture(0);
        this.status = "The video browser stopped. Start or join a room again.";
    }

    private void StopRendererProcess()
    {
        var process = this.rendererProcess;
        if (process is null)
        {
            return;
        }

        this.rendererProcess = null;
        _ = Task.Run(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1000))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Could not stop renderer bridge process cleanly.");
            }
            finally
            {
                process.Dispose();
            }
        });
    }

    /// <summary>
    /// Reads the sidecar file that OverlayPlayer writes when it starts streaming its
    /// browser frames into a shared D3D11 texture. The handle in that file can be
    /// opened directly on the game's device for real-time video.
    /// </summary>
    private void TryUpdateSharedTexture()
    {
        var infoPath = this.GetSharedInfoPath();
        try
        {
            if (!File.Exists(infoPath))
            {
                this.sharedTextureHandle = 0;
                this.lastSharedInfoWriteUtc = DateTime.MinValue;
                return;
            }

            var writeTime = File.GetLastWriteTimeUtc(infoPath);
            if (writeTime <= this.lastSharedInfoWriteUtc)
            {
                return;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(infoPath));
            if (document.RootElement.TryGetProperty("frames", out var framesElement) &&
                framesElement.TryGetInt64(out var frames) &&
                frames <= 0)
            {
                this.sharedTextureHandle = 0;
                this.presentHookProbe.SetNativeSharedTexture(0);
                return;
            }

            var handle = document.RootElement.GetProperty("handle").GetInt64();
            this.sharedTextureHandle = (nint)handle;
            this.lastSharedInfoWriteUtc = writeTime;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            // The file is being rewritten; try again next frame.
        }
    }

    private string GetCaptureFramePath()
    {
        return Path.Combine(this.pluginDirectory, "videosync-preview.jpg");
    }

    private string GetSharedInfoPath()
    {
        return Path.Combine(this.pluginDirectory, "videosync-preview.shared.json");
    }

    private string GetControlPath()
    {
        return Path.Combine(this.pluginDirectory, "videosync-preview.control.json");
    }

    private string GetStatusPath()
    {
        return Path.Combine(this.pluginDirectory, "videosync-preview.status.json");
    }

    private string GetAudioPath()
    {
        return Path.Combine(this.pluginDirectory, "videosync-preview.audio.json");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale frame is harmless; the next successful capture overwrites it.
        }
    }

    private static string BuildWatchUrl(SyncPayload payload)
    {
        var targetSeconds = Math.Max(0, (int)Math.Floor(payload.GetCurrentVideoSeconds()));
        return $"https://www.youtube.com/watch?v={Uri.EscapeDataString(payload.VideoId)}&t={targetSeconds}s&autoplay=1";
    }

    private static bool TryDecode(string code, out SyncPayload payload, out string error)
    {
        payload = default;
        error = string.Empty;

        try
        {
            payload = SyncCode.Decode(code);
            if (string.IsNullOrWhiteSpace(payload.VideoId))
            {
                error = "That code decoded, but it did not contain a video id.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not decode that sync code: {ex.Message}";
            return false;
        }
    }

    private static string FormatSummary(SyncPayload payload)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(payload.StartUnixSeconds).ToLocalTime();
        var target = TimeSpan.FromSeconds(Math.Max(0, payload.GetCurrentVideoSeconds()));
        var options = payload.Options is { } opt
            ? $"\nFullscreen: {(opt.VideoFullscreen ? "yes" : "no")}\nAdblock: {(opt.AdBlock ? "yes" : "no")}"
            : string.Empty;
        var placement = payload.Screen is { } screen
            ? $"\nScreen: {(screen.Enabled ? "shown" : "hidden")} at {screen.X:0.0}, {screen.Y:0.0}, {screen.Z:0.0}"
            : "\nScreen: receiver default";
        return $"Video: {payload.VideoId}\nStart: {start:g}\nOffset: {FormatTimestamp(payload.OffsetSeconds)}\nCurrent target: {FormatTimestamp(target.TotalSeconds)}{options}{placement}";
    }

    private static bool TryExtractYouTubeId(string input, out string videoId)
    {
        videoId = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        input = input.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            videoId = input;
            return LooksLikeYouTubeId(videoId);
        }

        var host = uri.Host.ToLowerInvariant();
        if (host is "youtu.be" or "www.youtu.be")
        {
            videoId = uri.AbsolutePath.Trim('/');
            return LooksLikeYouTubeId(videoId);
        }

        if (!host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var queryVideoId = GetQueryValue(uri.Query, "v");
        if (LooksLikeYouTubeId(queryVideoId))
        {
            videoId = queryVideoId;
            return true;
        }

        var path = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length >= 2 && (path[0] is "shorts" or "embed" or "live") && LooksLikeYouTubeId(path[1]))
        {
            videoId = path[1];
            return true;
        }

        return false;
    }

    private static string GetQueryValue(string query, string key)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && Uri.UnescapeDataString(pieces[0]) == key)
            {
                return Uri.UnescapeDataString(pieces[1].Replace("+", " "));
            }
        }

        return string.Empty;
    }

    private static bool LooksLikeYouTubeId(string value)
    {
        if (value.Length != 11)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseTimestamp(string input, out double seconds)
    {
        seconds = 0;
        input = input.Trim();

        if (double.TryParse(input, out seconds))
        {
            return seconds >= 0;
        }

        var parts = input.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var multiplier = 1;
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (!double.TryParse(parts[i], out var value) || value < 0)
            {
                return false;
            }

            seconds += value * multiplier;
            multiplier *= 60;
        }

        return true;
    }

    private static string FormatTimestamp(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private static float RadiansToDegrees(float radians)
    {
        return radians * 180f / MathF.PI;
    }

    private static float DegreesToRadians(float degrees)
    {
        return degrees * MathF.PI / 180f;
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private readonly record struct SnowSyncBurst(DateTime DueUtc, string CommandPrefix, string Code);

    private readonly record struct SnowSyncshell(string Gid, int ShellNumber, bool Enabled);

    private readonly record struct ScreenRect(Vector2 Min, Vector2 Max)
    {
        public bool IsEmpty => this.Max.X <= this.Min.X || this.Max.Y <= this.Min.Y;

        public static ScreenRect FromPoints(params Vector2[] points)
        {
            var min = points[0];
            var max = points[0];
            foreach (var point in points)
            {
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }

            return new ScreenRect(min, max);
        }

        public ScreenRect Inflate(float amount)
        {
            var delta = new Vector2(amount, amount);
            return new ScreenRect(this.Min - delta, this.Max + delta);
        }

        public bool Intersects(ScreenRect other)
        {
            return this.Min.X < other.Max.X &&
                   this.Max.X > other.Min.X &&
                   this.Min.Y < other.Max.Y &&
                   this.Max.Y > other.Min.Y;
        }

        public ScreenRect Intersect(ScreenRect other)
        {
            return new ScreenRect(Vector2.Max(this.Min, other.Min), Vector2.Min(this.Max, other.Max));
        }
    }
}
