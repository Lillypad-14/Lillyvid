using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace VideoSyncPrototype.OverlayPlayer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var captureFramePath = args.Length >= 3 && args[0] == "--capture" ? args[2] : null;
        var url = captureFramePath is not null ? args[1] : args.Length > 0 ? args[0] : "https://www.youtube.com";
        long? adapterLuid = null;
        var adBlockEnabled = true;
        string? watch2GetherShareUrl = null;
        var captureSize = new Size(1280, 720);
        var captureMode = 0;
        var foregroundCapture = false;
        for (var i = 3; i < args.Length - 1; i++)
        {
            if (args[i] == "--capture-mode" && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMode))
            {
                captureMode = Math.Clamp(parsedMode, 0, 2);
                i++;
                continue;
            }

            if (args[i] == "--foreground-capture")
            {
                foregroundCapture = string.Equals(args[i + 1], "enabled", StringComparison.OrdinalIgnoreCase) ||
                                    args[i + 1] == "1" ||
                                    string.Equals(args[i + 1], "true", StringComparison.OrdinalIgnoreCase);
                i++;
                continue;
            }

            if (args[i] == "--adapter-luid" && long.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLuid))
            {
                adapterLuid = parsedLuid;
                i++;
                continue;
            }

            if (args[i] == "--capture-size" && TryParseSize(args[i + 1], out var parsedSize))
            {
                captureSize = parsedSize;
                i++;
                continue;
            }

            if (args[i] == "--adblock")
            {
                adBlockEnabled = !string.Equals(args[i + 1], "disabled", StringComparison.OrdinalIgnoreCase) &&
                                 args[i + 1] != "0" &&
                                 !string.Equals(args[i + 1], "false", StringComparison.OrdinalIgnoreCase);
                i++;
            }

            if (args[i] == "--w2g-share")
            {
                watch2GetherShareUrl = args[i + 1];
                i++;
            }

        }

        Application.Run(new PlayerForm(url, captureFramePath, adapterLuid, adBlockEnabled, watch2GetherShareUrl, captureSize, captureMode, foregroundCapture));
    }

    // Parses a "WIDTHxHEIGHT" capture size (e.g. "1920x1080"), clamped to a sane range so
    // a bad argument can never spawn a zero-size or absurdly large capture window.
    private static bool TryParseSize(string value, out Size size)
    {
        size = new Size(1280, 720);
        var parts = value.Split('x', 'X');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
        {
            return false;
        }

        size = new Size(Math.Clamp(width, 320, 3840), Math.Clamp(height, 180, 2160));
        return true;
    }
}

internal sealed class PlayerForm : Form
{
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndBottom = new(1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;
    private const int DwmwaCloak = 13;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    // The pixel size the browser renders and WGC captures at. Set once at launch from
    // the plugin's Resolution setting (720p by default); higher = sharper in-world screen.
    private readonly Size desktopCaptureSize;

    // BrowserArguments plus the launch-time --window-size matching the capture size.
    private readonly string browserArguments;

    // Frame pool buffer count from the capture mode (Default 3, higher modes 5): more slack
    // means fewer dropped frames when a CopyResource is slow, which shows up as higher fps.
    private readonly int captureBufferCount;

    // Experimental: cloak the capture window and keep it top-most so DWM composites it at
    // full refresh (defeats occlusion throttling) while staying invisible over the game.
    private readonly bool foregroundCapture;
    // Desktop UA, applied on every init path. Watch2Gether and YouTube fall back to
    // their stripped mobile layout (a black player behind a "switch to desktop" link)
    // whenever they think they're on a phone, so we present a plain desktop browser.
    private const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0";

    private const string BrowserArguments =
        "--autoplay-policy=no-user-gesture-required " +
        // Touchscreen laptops make WebView2 advertise touch, which flips w2g/YouTube to
        // their mobile site; declare a normal desktop mouse so they serve the desktop
        // player. Blink enums: pointer 1=none, 2=coarse/touch, 4=fine/mouse; hover
        // 1=none, 2=hover. (4/4/2/2 = mouse. 1 here means NO pointer at all, which
        // breaks the players' layout into a black screen — do not "simplify" these.)
        "--touch-events=disabled " +
        "--blink-settings=primaryPointerType=4,availablePointerTypes=4,primaryHoverType=2,availableHoverTypes=2 " +
        "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling " +
        "--disable-backgrounding-occluded-windows " +
        "--disable-renderer-backgrounding " +
        "--disable-background-timer-throttling " +
        "--disable-background-media-suspend";

    private readonly WebView2 webView = new();
    private readonly TextBox addressBar = new();
    // Accent palette mirrors the in-game plugin's warm gold so the mini-browser reads as
    // part of the same product rather than a raw WinForms window.
    private static readonly Color Accent = Color.FromArgb(222, 179, 97);
    private static readonly Color AccentHover = Color.FromArgb(236, 199, 121);
    private static readonly Color AccentInk = Color.FromArgb(38, 28, 10);
    private static readonly Color ToolbarBg = Color.FromArgb(18, 18, 20);
    private static readonly Color HintBg = Color.FromArgb(34, 30, 20);

    private readonly Panel toolbar = new();
    private readonly Label hintBar = new();
    private readonly Button doneButton = new();
    private readonly Button topMostButton = new();
    private readonly Button backButton = new();
    private readonly Button forwardButton = new();
    private readonly CheckBox adBlockCheckBox = new();
    private readonly Label adBlockStatusLabel = new();
    private readonly ToolTip toolbarTips = new();
    private readonly string? captureFramePath;
    private readonly System.Windows.Forms.Timer captureTimer = new();
    private readonly System.Windows.Forms.Timer controlTimer = new();
    private readonly System.Windows.Forms.Timer statusTimer = new();
    private readonly System.Windows.Forms.Timer youtubeCleanupTimer = new();
    private readonly System.Windows.Forms.Timer loadWatchdogTimer = new();
    private readonly System.Windows.Forms.Timer watch2GetherAutomationTimer = new();
    private readonly string? controlPath;
    private readonly string? statusPath;
    private readonly string? audioPath;
    private SharedFrameStreamer? frameStreamer;
    private bool captureInProgress;
    private bool statusInProgress;
    private long lastControlSeq = -1;
    private DateTime lastControlWriteUtc;
    private DateTime lastAudioWriteUtc;
    private DateTime lastAudioApplyUtc;
    private bool adBlockEnabled = true;
    private bool uBlockLoaded;
    private bool browserWindowShown;
    private bool videoFullscreenMode;
    private DateTime navigationStartedUtc = DateTime.UtcNow;
    private DateTime lastVideoProgressUtc = DateTime.UtcNow;
    private DateTime lastLoadRecoveryUtc = DateTime.MinValue;
    private DateTime lastStartupPlayKickUtc = DateTime.MinValue;
    private double lastWatchdogVideoTime = -1;
    private int loadRecoveryAttempts;
    private int startupPlayKicks;
    private int w2gRecoveryAttempts;
    private string? w2gWatchdogUrl;
    private readonly HashSet<string> refreshedWatch2GetherRooms = [];
    private readonly string? watch2GetherShareUrl;
    private bool watch2GetherCreateClicked;
    private bool watch2GetherShareSubmitted;
    private bool watch2GetherShareAttempted;
    private bool watch2GetherInviteClosed;
    private int watch2GetherAudioSyncTicks;
    private double lastRoomVolume = 1.0;
    private double lastRoomPan;
    private bool lastRoomMuted;

    private readonly long? adapterLuid;

    private static readonly string[] BlockedHostParts =
    [
        "doubleclick.net",
        "googlesyndication.com",
        "googleadservices.com",
        "google-analytics.com",
        "googletagmanager.com",
        "adservice.google.",
        "adsystem.com",
        "adnxs.com",
        "adsrvr.org",
        "amazon-adsystem.com",
        "scorecardresearch.com",
        "taboola.com",
        "outbrain.com",
        "s.youtube.com",
        "pagead2.googlesyndication.com",
        "securepubads.g.doubleclick.net",
    ];

    private static readonly string[] BlockedUrlParts =
    [
        "/pagead/",
        "/pagead2/",
        "/ptracking",
        "/api/stats/ads",
        "/get_midroll_info",
        "adformat=",
        "adunit",
        "ad_break",
        "googleads",
        "doubleclick",
        "ad_type",
        "adurl",
        "ad3_module",
        "player_ias",
        "youtubei/v1/log_event",
    ];

    public PlayerForm(string initialUrl, string? captureFramePath, long? adapterLuid, bool adBlockEnabled, string? watch2GetherShareUrl, Size captureSize, int captureMode, bool foregroundCapture)
    {
        this.foregroundCapture = foregroundCapture;
        this.captureFramePath = captureFramePath;
        this.adapterLuid = adapterLuid;
        this.adBlockEnabled = adBlockEnabled;
        this.watch2GetherShareUrl = watch2GetherShareUrl;
        // Windows Graphics Capture can only composite a window up to the physical monitor
        // size; a larger window (e.g. 1440p/4K on a 1080p screen) runs off the screen edges
        // and those regions capture as black. Scale the request down to fit the monitor,
        // preserving aspect ratio, so a too-high pick degrades gracefully instead of going
        // black. The plugin's debug readout shows the resulting size.
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var fit = Math.Min(1.0,
            Math.Min((double)screen.Width / captureSize.Width, (double)screen.Height / captureSize.Height));
        this.desktopCaptureSize = new Size(
            Math.Max(2, (int)Math.Round(captureSize.Width * fit) & ~1),
            Math.Max(2, (int)Math.Round(captureSize.Height * fit) & ~1));

        // Capture mode: 0 Default (3 buffers), 1 Smooth (5 buffers). The vsync-uncap path was
        // removed — it broke WebView2's presentation into the surface WGC captures.
        this.captureBufferCount = captureMode == 0 ? 3 : 5;

        // Match Chromium's initial window size to the capture size so the page lays out at
        // the target resolution from the first paint (avoids a 720p->target reflow flash).
        this.browserArguments = BrowserArguments +
            $" --window-size={this.desktopCaptureSize.Width},{this.desktopCaptureSize.Height}";
        if (captureFramePath is not null)
        {
            this.controlPath = Path.ChangeExtension(captureFramePath, ".control.json");
            this.statusPath = Path.ChangeExtension(captureFramePath, ".status.json");
            this.audioPath = Path.ChangeExtension(captureFramePath, ".audio.json");
        }

        // Allow autoplay/WebAudio without a user gesture, and keep Chromium rendering
        // at full rate even though the window sits off-screen behind the game —
        // otherwise occlusion detection throttles the video to a choppy crawl.
        this.Text = captureFramePath is null ? "Video Sync Player" : "Video Sync Renderer Bridge";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Size = new Size(960, 600);
        this.MinimumSize = new Size(420, 260);
        this.TopMost = true;
        this.KeyPreview = true;

        this.toolbar.Dock = DockStyle.Top;
        this.toolbar.Height = 44;
        this.toolbar.Padding = new Padding(8, 6, 8, 6);
        this.toolbar.BackColor = ToolbarBg;
        this.toolbar.MouseDown += (_, e) => this.BeginWindowDrag(e);

        // Left: browser navigation. Small square icon buttons so the address bar keeps the
        // room to breathe. Back/forward start disabled and light up as history builds.
        this.backButton.Text = "‹";
        this.backButton.Dock = DockStyle.Left;
        this.backButton.Width = 34;
        this.backButton.Enabled = false;
        StyleToolbarButton(this.backButton);
        this.backButton.Font = new Font(this.backButton.Font.FontFamily, 13f, FontStyle.Bold);
        this.backButton.Click += (_, _) =>
        {
            if (this.webView.CoreWebView2?.CanGoBack == true)
            {
                this.webView.CoreWebView2.GoBack();
            }
        };

        this.forwardButton.Text = "›";
        this.forwardButton.Dock = DockStyle.Left;
        this.forwardButton.Width = 34;
        this.forwardButton.Enabled = false;
        StyleToolbarButton(this.forwardButton);
        this.forwardButton.Font = new Font(this.forwardButton.Font.FontFamily, 13f, FontStyle.Bold);
        this.forwardButton.Click += (_, _) =>
        {
            if (this.webView.CoreWebView2?.CanGoForward == true)
            {
                this.webView.CoreWebView2.GoForward();
            }
        };

        var reloadButton = new Button { Text = "↻", Dock = DockStyle.Left, Width = 34 };
        StyleToolbarButton(reloadButton);
        reloadButton.Font = new Font(reloadButton.Font.FontFamily, 12.5f, FontStyle.Bold);
        reloadButton.Click += (_, _) => this.webView.CoreWebView2?.Reload();

        var navSpacer = new Panel { Dock = DockStyle.Left, Width = 8, BackColor = ToolbarBg };

        this.addressBar.Dock = DockStyle.Fill;
        this.addressBar.Text = initialUrl;
        this.addressBar.PlaceholderText = "Enter a URL or search";
        this.addressBar.BackColor = Color.FromArgb(30, 30, 34);
        this.addressBar.ForeColor = Color.White;
        this.addressBar.BorderStyle = BorderStyle.FixedSingle;
        this.addressBar.Font = new Font("Segoe UI", 9.5f);
        this.addressBar.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                this.Navigate(this.addressBar.Text);
                e.SuppressKeyPress = true;
            }
        };

        // A single-line TextBox keeps its own height when docked, so on its own it clings to
        // the top of the toolbar. Nesting it in a padded host vertically centers it and gives
        // it a little breathing room from the buttons on either side.
        var addressHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(2, 6, 2, 6),
            BackColor = ToolbarBg,
        };
        addressHost.Controls.Add(this.addressBar);

        // Right: the one button people came for — a big, gold "Done" that sends the window
        // back off-screen and returns focus to the game.
        this.doneButton.Text = "✓  Done";
        this.doneButton.Dock = DockStyle.Right;
        this.doneButton.Width = 96;
        StyleAccentButton(this.doneButton);
        this.doneButton.Click += (_, _) => this.HideBrowserWindow();

        this.topMostButton.Text = "Top";
        this.topMostButton.Dock = DockStyle.Right;
        this.topMostButton.Width = 52;
        StyleToolbarButton(this.topMostButton);
        this.topMostButton.Click += (_, _) =>
        {
            this.TopMost = !this.TopMost;
            this.topMostButton.Text = this.TopMost ? "Top" : "Free";
        };

        this.adBlockCheckBox.Text = "Block ads";
        this.adBlockCheckBox.Dock = DockStyle.Right;
        this.adBlockCheckBox.Width = 84;
        this.adBlockCheckBox.BackColor = ToolbarBg;
        this.adBlockCheckBox.ForeColor = Color.Gainsboro;
        this.adBlockCheckBox.FlatStyle = FlatStyle.Flat;
        this.adBlockCheckBox.Checked = this.adBlockEnabled;
        this.adBlockCheckBox.CheckedChanged += (_, _) => this.adBlockEnabled = this.adBlockCheckBox.Checked;

        // Slim instruction strip under the toolbar — tells the user exactly what to do and
        // how to get back. Only visible while the window is on screen.
        this.hintBar.Dock = DockStyle.Top;
        this.hintBar.Height = 24;
        this.hintBar.BackColor = HintBg;
        this.hintBar.ForeColor = Accent;
        this.hintBar.Font = new Font("Segoe UI", 9f);
        this.hintBar.TextAlign = ContentAlignment.MiddleLeft;
        this.hintBar.Padding = new Padding(10, 0, 0, 0);
        this.hintBar.Text = "Click through any ad, consent, or sign-in below — then press Done to return to the game.";
        this.hintBar.Visible = false;
        this.hintBar.MouseDown += (_, e) => this.BeginWindowDrag(e);

        // Kept as a field for adblock-mode reporting, but no longer cluttering the toolbar.
        this.adBlockStatusLabel.Text = "basic";

        this.toolbarTips.SetToolTip(this.backButton, "Back");
        this.toolbarTips.SetToolTip(this.forwardButton, "Forward");
        this.toolbarTips.SetToolTip(reloadButton, "Reload this page");
        this.toolbarTips.SetToolTip(this.topMostButton, "Keep this window above the game");
        this.toolbarTips.SetToolTip(this.doneButton, "Send this window back to the game");

        // A thin spacer keeps the address bar from butting straight against "Block ads".
        var rightSpacer = new Panel { Dock = DockStyle.Right, Width = 8, BackColor = ToolbarBg };

        // Add order matters: the Fill control goes in first, then docked controls claim
        // their edges (the last one added to an edge sits outermost). Result, left→right:
        //   [‹] [›] [↻]  [ address bar .......... ]  [Block ads] [Top] [✓ Done]
        this.toolbar.Controls.Add(addressHost);
        this.toolbar.Controls.Add(rightSpacer);
        this.toolbar.Controls.Add(this.adBlockCheckBox);
        this.toolbar.Controls.Add(this.topMostButton);
        this.toolbar.Controls.Add(this.doneButton);
        this.toolbar.Controls.Add(navSpacer);
        this.toolbar.Controls.Add(reloadButton);
        this.toolbar.Controls.Add(this.forwardButton);
        this.toolbar.Controls.Add(this.backButton);

        this.webView.Dock = DockStyle.Fill;
        this.webView.CoreWebView2InitializationCompleted += this.OnCoreWebView2InitializationCompleted;
        this.webView.NavigationStarting += (_, _) => this.ResetLoadWatchdog();
        this.webView.NavigationCompleted += async (_, _) =>
        {
            this.addressBar.Text = this.webView.Source?.ToString() ?? string.Empty;
            this.backButton.Enabled = this.webView.CoreWebView2?.CanGoBack == true;
            this.forwardButton.Enabled = this.webView.CoreWebView2?.CanGoForward == true;
            this.ResetLoadWatchdog();
            await this.ApplyPlayerChromeAsync();
            await this.AutomateWatch2GetherAsync();

            // Landed on a room: re-assert unmute/full-volume on the YouTube player for the
            // next ~15s (see TickWatch2GetherAudioAsync) to beat its delayed initialization.
            if (this.IsOnWatch2GetherRoom())
            {
                this.watch2GetherAudioSyncTicks = 20;
                await this.RefreshWatch2GetherRoomOnceAsync();
            }

            await this.ApplyCaptureInteractionAsync(this.captureFramePath is not null && !this.browserWindowShown);
        };

        // Order: webView (Fill) first, then the hint strip, then the toolbar on top — so
        // top-to-bottom the window reads toolbar / hint / page.
        this.Controls.Add(this.webView);
        this.Controls.Add(this.hintBar);
        this.Controls.Add(this.toolbar);

        this.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                // In-world capture mode: Escape means "send this back" (same as Done), never
                // "kill the player" — closing would stop the video everyone is watching.
                if (this.captureFramePath is not null && this.browserWindowShown)
                {
                    this.HideBrowserWindow();
                }
                else if (this.captureFramePath is null)
                {
                    this.Close();
                }

                e.SuppressKeyPress = true;
            }
        };

        if (this.captureFramePath is not null)
        {
            // Screen-capture mode: a borderless 16:9 window that stays on screen (WGC
            // stops delivering frames for fully off-screen windows) but pinned to the
            // bottom of the z-order behind the game, excluded from the taskbar and
            // Alt-Tab, and never activated - so it is effectively invisible in play.
            this.FormBorderStyle = FormBorderStyle.None;
            this.ClientSize = this.desktopCaptureSize;
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(0, 0);
            this.ShowInTaskbar = false;
            this.TopMost = false;
            this.toolbar.Visible = false;

            Directory.CreateDirectory(Path.GetDirectoryName(this.captureFramePath) ?? AppContext.BaseDirectory);
            TryDeleteFile(this.controlPath);
            TryDeleteFile(this.statusPath);
            TryDeleteFile(this.audioPath);

            this.captureTimer.Interval = 2000;
            this.captureTimer.Tick += async (_, _) => await this.CaptureFrameAsync();
            this.controlTimer.Interval = 100;
            this.controlTimer.Tick += async (_, _) => await this.PollControlAsync();
            this.statusTimer.Interval = 750;
            this.statusTimer.Tick += async (_, _) =>
            {
                await this.PublishStatusAsync();
                await this.TickWatch2GetherAudioAsync();
            };
        }

        this.youtubeCleanupTimer.Interval = 500;
        this.youtubeCleanupTimer.Tick += async (_, _) => await this.CleanupYouTubeAdsAsync();
        this.loadWatchdogTimer.Interval = 2000;
        this.loadWatchdogTimer.Tick += async (_, _) => await this.CheckLoadWatchdogAsync();
        this.watch2GetherAutomationTimer.Interval = 1000;
        this.watch2GetherAutomationTimer.Tick += async (_, _) => await this.AutomateWatch2GetherAsync();
        _ = this.InitializeWebViewAsync(initialUrl);
    }

    private void ResetLoadWatchdog()
    {
        var now = DateTime.UtcNow;
        this.navigationStartedUtc = now;
        this.lastVideoProgressUtc = now;
        this.lastLoadRecoveryUtc = DateTime.MinValue;
        this.lastStartupPlayKickUtc = DateTime.MinValue;
        this.lastWatchdogVideoTime = -1;
        this.loadRecoveryAttempts = 0;
        this.startupPlayKicks = 0;
    }

    private async Task InitializeWebViewAsync(string initialUrl)
    {
        // Browser cache/cookies/service-workers live in the user's profile, NOT next to
        // the exe. If we let WebView2 default the profile, it drops a multi-hundred-MB
        // "OverlayPlayer.exe.WebView2" folder right inside the plugin directory, bloating
        // the shipped plugin. CreationProperties pins the profile here for EVERY init
        // path — including the catch-block fallback below, which otherwise silently
        // recreates the default in-place profile.
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoSyncPrototype",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);
        this.webView.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = userDataFolder,
            AdditionalBrowserArguments = this.browserArguments,
        };

        try
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = this.browserArguments,
                AreBrowserExtensionsEnabled = true,
            };
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
            await this.webView.EnsureCoreWebView2Async(environment);
        }
        catch
        {
            // Extensions/env creation can fail on some machines. Fall back to a default
            // environment so we still get a CoreWebView2 to configure — the old catch
            // navigated with WebView2's default UA and no desktop forcing, which is how
            // the mobile black-screen slipped through.
            try
            {
                await this.webView.EnsureCoreWebView2Async();
            }
            catch
            {
                this.webView.Source = new Uri(ForceDesktopSite(initialUrl));
                return;
            }
        }

        // One place, after either path: pin the desktop UA before the only navigation
        // so the site can never decide we're a phone.
        if (this.webView.CoreWebView2 is not null)
        {
            this.webView.CoreWebView2.Settings.UserAgent = DesktopUserAgent;
            this.webView.CoreWebView2.Navigate(ForceDesktopSite(initialUrl));
        }
        else
        {
            this.webView.Source = new Uri(ForceDesktopSite(initialUrl));
        }
    }

    private async void OnCoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess || this.webView.CoreWebView2 is null)
        {
            return;
        }

        // Never let page content spawn a separate desktop window. Watch2Gether share
        // dialogs, "open in app" links, and the odd ad all call window.open(); without
        // this handler WebView2 pops a real top-level window onto the user's desktop,
        // which is exactly the stray-tab problem we're fixing. Browser security won't
        // let a script close a window it didn't open, so the clean fix is to refuse to
        // create the popup in the first place. e.Handled = true swallows the request and
        // keeps the in-world screen as the only surface. (We deliberately do NOT redirect
        // the target into this view — for a movie screen we never want to leave the room.)
        this.webView.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
        };

        // Ignore programmatic window.close() from page scripts so a stray call can't tear
        // down our renderer bridge window mid-playback.
        this.webView.CoreWebView2.WindowCloseRequested += (_, _) => { };

        this.ConfigureAdBlocking();
        await this.InstallYouTubeCleanupScriptAsync();
        await this.InstallWatch2GetherProbeAsync();
        await this.InstallTextInputShortcutGuardAsync();
        this.youtubeCleanupTimer.Start();
        this.loadWatchdogTimer.Start();
        if (this.watch2GetherShareUrl is not null)
        {
            this.watch2GetherAutomationTimer.Start();
        }

        if (this.captureFramePath is not null)
        {
            _ = this.ApplyCaptureInteractionAsync(captureOnly: true);

            if (this.frameStreamer is null)
            {
                this.captureTimer.Start();
            }

            this.controlTimer.Start();
            this.statusTimer.Start();
        }

        _ = this.TryLoadUBlockOriginAsync();
    }

    private void ConfigureAdBlocking()
    {
        var core = this.webView.CoreWebView2;
        if (core is null)
        {
            return;
        }

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!this.adBlockEnabled || !ShouldBlockRequest(e.Request.Uri))
            {
                return;
            }

            e.Response = core.Environment.CreateWebResourceResponse(
                null,
                204,
                "No Content",
                "Access-Control-Allow-Origin: *");
        };
    }

    private async Task InstallYouTubeCleanupScriptAsync()
    {
        if (this.webView.CoreWebView2 is null)
        {
            return;
        }

        await this.webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeCleanupScript);
    }

    // A Watch2Gether room plays its YouTube video inside a cross-origin iframe we can't
    // read directly, but YouTube's iframe API posts "infoDelivery" messages up to the
    // w2g page (same origin as us). Recording the latest player state + timestamp lets
    // the watchdog tell a healthy room from a black/stuck one.
    private async Task InstallWatch2GetherProbeAsync()
    {
        if (this.webView.CoreWebView2 is null)
        {
            return;
        }

        await this.webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(Watch2GetherProbeScript);
    }

    private async Task InstallTextInputShortcutGuardAsync()
    {
        if (this.webView.CoreWebView2 is null)
        {
            return;
        }

        await this.webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("""
            (() => {
                if (window.__videoSyncTextInputGuard) return;
                window.__videoSyncTextInputGuard = true;

                function editable(el) {
                    if (!el) return false;
                    var tag = (el.tagName || '').toLowerCase();
                    return tag === 'input' || tag === 'textarea' || tag === 'select' || !!el.isContentEditable;
                }

                function stopShortcutsAfterInput(e) {
                    if (!editable(e.target) && !editable(document.activeElement)) return;
                    e.stopPropagation();
                }

                document.addEventListener('keydown', stopShortcutsAfterInput, false);
                document.addEventListener('keypress', stopShortcutsAfterInput, false);
                document.addEventListener('keyup', stopShortcutsAfterInput, false);
            })();
            """);
    }

    private async Task TryLoadUBlockOriginAsync()
    {
        if (this.webView.CoreWebView2 is null)
        {
            return;
        }

        var extensionPath = FindUBlockOriginExtensionPath();
        if (extensionPath is null)
        {
            this.adBlockStatusLabel.Text = "basic";
            return;
        }

        try
        {
            var loadPath = PrepareExtensionLoadDirectory(extensionPath);
            var extensionTask = this.webView.CoreWebView2.Profile.AddBrowserExtensionAsync(loadPath);
            if (await Task.WhenAny(extensionTask, Task.Delay(2000)) != extensionTask)
            {
                this.adBlockStatusLabel.Text = "basic";
                return;
            }

            var extension = await extensionTask;
            await extension.EnableAsync(true);
            this.uBlockLoaded = true;
            this.adBlockStatusLabel.Text = "uBO";
        }
        catch
        {
            this.uBlockLoaded = false;
            this.adBlockStatusLabel.Text = "basic";
        }
    }

    private static string PrepareExtensionLoadDirectory(string sourcePath)
    {
        if (sourcePath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoSyncPrototype",
            "WebView2Extensions",
            "uBlockOrigin");

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        CopyDirectory(sourcePath, target);
        return target;
    }

    private static void CopyDirectory(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(targetPath);
        foreach (var file in Directory.EnumerateFiles(sourcePath))
        {
            File.Copy(file, Path.Combine(targetPath, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourcePath))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "_metadata", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyDirectory(directory, Path.Combine(targetPath, name));
        }
    }

    private static string? FindUBlockOriginExtensionPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            Path.Combine(baseDirectory, "Extensions", "uBlockOrigin"),
            Path.Combine(baseDirectory, "Extensions", "ublock"),
            Path.Combine(baseDirectory, "uBlockOrigin"),
        })
        {
            if (IsUsableExtensionDirectory(candidate))
            {
                return candidate;
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in new[]
        {
            Path.Combine(localAppData, "Google", "Chrome", "User Data"),
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"),
        })
        {
            var found = FindInstalledUBlockOrigin(root);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? FindInstalledUBlockOrigin(string userDataRoot)
    {
        if (!Directory.Exists(userDataRoot))
        {
            return null;
        }

        foreach (var profile in Directory.EnumerateDirectories(userDataRoot))
        {
            var extensionRoot = Path.Combine(profile, "Extensions");
            if (!Directory.Exists(extensionRoot))
            {
                continue;
            }

            foreach (var extensionDirectory in Directory.EnumerateDirectories(extensionRoot))
            {
                foreach (var versionDirectory in Directory.EnumerateDirectories(extensionDirectory).OrderByDescending(Path.GetFileName))
                {
                    if (IsUBlockOriginDirectory(versionDirectory))
                    {
                        return versionDirectory;
                    }
                }
            }
        }

        return null;
    }

    private static bool IsUsableExtensionDirectory(string path)
    {
        return Directory.Exists(path) && File.Exists(Path.Combine(path, "manifest.json"));
    }

    private static bool IsUBlockOriginDirectory(string path)
    {
        var manifestPath = Path.Combine(path, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject();
            var name = manifest?["name"]?.GetValue<string>() ?? string.Empty;
            var shortName = manifest?["short_name"]?.GetValue<string>() ?? string.Empty;
            var normalized = (name + " " + shortName).ToLowerInvariant();
            return normalized.Contains("ublock", StringComparison.Ordinal) ||
                   normalized.Contains("µblock", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    // Windows.Graphics.Capture only accepts "Alt-Tab style" windows: tool windows,
    // owned windows, and WS_EX_NOACTIVATE windows all fail CreateForWindow. The
    // capture window therefore stays a normal (borderless) window that is simply
    // shown without activation and pinned to the bottom of the z-order.
    protected override bool ShowWithoutActivation => this.captureFramePath is not null && !this.browserWindowShown;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (this.captureFramePath is not null)
        {
            this.BuryCaptureWindow();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (this.captureFramePath is not null)
        {
            this.BuryCaptureWindow();
            var infoPath = Path.ChangeExtension(this.captureFramePath, ".shared.json");
            this.frameStreamer = SharedFrameStreamer.TryStart(this.Handle, infoPath, this.adapterLuid, this.captureBufferCount);
            if (this.frameStreamer is not null)
            {
                this.captureTimer.Stop();
            }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.youtubeCleanupTimer.Stop();
        this.loadWatchdogTimer.Stop();
        this.frameStreamer?.Dispose();
        this.frameStreamer = null;
        TryDeleteFile(this.controlPath);
        TryDeleteFile(this.statusPath);
        TryDeleteFile(this.audioPath);
        base.OnFormClosed(e);
    }

    private static bool ShouldBlockRequest(string requestUri)
    {
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        foreach (var blockedHost in BlockedHostParts)
        {
            if (host.Contains(blockedHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var text = requestUri.ToLowerInvariant();
        if (host.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("oad=", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("ctier=", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("adformat=", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("adunit", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (var blockedPart in BlockedUrlParts)
        {
            if (text.Contains(blockedPart, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task CleanupYouTubeAdsAsync()
    {
        if (!this.adBlockEnabled || this.webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await this.webView.CoreWebView2.ExecuteScriptAsync(
                "(window.__videoSyncCleanupAds&&window.__videoSyncCleanupAds());" +
                "(window.__videoSyncApplyVideoFill&&window.__videoSyncApplyVideoFill());");
        }
        catch
        {
            // Best effort; navigation can briefly make script execution fail.
        }
    }

    private async Task ApplyPlayerChromeAsync()
    {
        if (this.webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await this.webView.CoreWebView2.ExecuteScriptAsync(
                "window.__videoSyncApplyVideoFill&&window.__videoSyncApplyVideoFill();");
        }
        catch
        {
            // Best effort; WebView may still be navigating.
        }
    }

    private async Task ApplyCaptureInteractionAsync(bool captureOnly)
    {
        if (this.webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await this.webView.CoreWebView2.ExecuteScriptAsync(
                $"window.__videoSyncSetCaptureOnly&&window.__videoSyncSetCaptureOnly({(captureOnly ? "true" : "false")});");
        }
        catch
        {
            // Best effort; the command may race a navigation.
        }
    }

    private async Task AutomateWatch2GetherAsync()
    {
        if (this.watch2GetherShareUrl is null || this.webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            if (this.webView.Source is not { } source ||
                !source.Host.Equals("w2g.tv", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var inWatch2GetherRoom = IsWatch2GetherRoomPage(source);
            if (!inWatch2GetherRoom &&
                !this.watch2GetherCreateClicked)
            {
                var clicked = await this.webView.CoreWebView2.ExecuteScriptAsync("""
                    (function() {
                        var buttons = Array.from(document.querySelectorAll('button,[role=button],a'));
                        var create = buttons.find(function(el) {
                            var text = (el.innerText || el.textContent || '').trim().toLowerCase();
                            var data = el.getAttribute('data-w2g') || '';
                            return data.indexOf('createRoom') >= 0 || text.indexOf('create your room') >= 0 || text.indexOf('create room') >= 0;
                        });
                        if (!create) return false;
                        create.click();
                        return true;
                    })()
                    """);
                this.watch2GetherCreateClicked = clicked.Contains("true", StringComparison.OrdinalIgnoreCase);
                return;
            }

            if (!inWatch2GetherRoom)
            {
                return;
            }

            if (this.browserWindowShown && await this.IsPageEditingTextAsync())
            {
                return;
            }

            await this.CloseWatch2GetherInviteModalAggressivelyAsync();

            if (this.watch2GetherShareSubmitted)
            {
                return;
            }

            if (this.watch2GetherShareAttempted && this.browserWindowShown)
            {
                return;
            }

            var shareJson = JsonSerializer.Serialize(this.watch2GetherShareUrl);
            var submitted = await this.webView.CoreWebView2.ExecuteScriptAsync($$"""
                (function(shareUrl) {
                    function visible(el) {
                        var r = el.getBoundingClientRect();
                        var s = window.getComputedStyle(el);
                        return r.width > 5 && r.height > 5 && s.visibility !== 'hidden' && s.display !== 'none';
                    }
                    function setValue(el, value) {
                        el.focus();
                        try {
                            var setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
                            setter.call(el, '');
                            el.dispatchEvent(new InputEvent('input', {bubbles:true, inputType:'deleteContentBackward', data:null}));
                            setter.call(el, value);
                        } catch (e) {
                            el.value = value;
                        }
                        try {
                            document.execCommand('selectAll', false, null);
                            document.execCommand('insertText', false, value);
                        } catch (e) { }
                        el.dispatchEvent(new InputEvent('beforeinput', {bubbles:true, cancelable:true, inputType:'insertText', data:value}));
                        el.dispatchEvent(new InputEvent('input', {bubbles:true, inputType:'insertText', data:value}));
                        el.dispatchEvent(new Event('change', {bubbles:true}));
                    }
                    if (document.querySelector('video')) return true;
                    if (document.querySelector('[data-w2g*="playClick"] a[href]:not([href="#"])')) return true;
                    var search = document.querySelector('#w2g-pl-search');
                    if (!search || !visible(search)) {
                        var toggles = Array.from(document.querySelectorAll('[data-w2g]')).filter(visible);
                        var toggle = toggles.find(function(el) {
                            return (el.getAttribute('data-w2g') || '').indexOf('showSearch') >= 0;
                        });
                        if (toggle) toggle.click();
                        search = document.querySelector('#w2g-pl-search');
                    }
                    if (!search) return false;
                    setValue(search, shareUrl);
                    window.setTimeout(function() {
                        var clickables = Array.from(document.querySelectorAll('button,[role=button],a,[data-w2g]')).filter(visible);
                        var action = clickables.find(function(el) {
                            var text = (el.innerText || el.textContent || el.title || '').trim().toLowerCase();
                            var data = el.getAttribute('data-w2g') || '';
                            return data.indexOf('playClick') >= 0 || data.indexOf('playItem') >= 0 ||
                                text === 'add' || text === 'play' || text === 'search' ||
                                text.indexOf('add') >= 0 || text.indexOf('play') >= 0;
                        });
                        if (action) action.click();
                    }, 1200);
                    return false;
                })({{shareJson}})
                """);
            this.watch2GetherShareAttempted = true;
            this.watch2GetherShareSubmitted = submitted.Contains("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // The W2G app is a moving target; retry on the next timer tick.
        }
    }

    private static bool IsWatch2GetherRoomPage(Uri source)
    {
        if (!source.Host.Equals("w2g.tv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return source.AbsolutePath.StartsWith("/rooms/", StringComparison.OrdinalIgnoreCase) ||
               source.AbsolutePath.Contains("/room/", StringComparison.OrdinalIgnoreCase) ||
               source.Query.Contains("access_key=", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshWatch2GetherRoomOnceAsync()
    {
        if (this.webView.CoreWebView2 is null ||
            this.webView.Source is not { } source ||
            !IsWatch2GetherRoomPage(source))
        {
            return;
        }

        var roomKey = source.AbsoluteUri;
        if (!this.refreshedWatch2GetherRooms.Add(roomKey))
        {
            return;
        }

        await Task.Delay(1500);
        if (this.webView.CoreWebView2 is null ||
            this.webView.Source is not { } current ||
            !string.Equals(current.AbsoluteUri, roomKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        this.webView.CoreWebView2.Reload();
    }

    private async Task CloseWatch2GetherInviteModalAsync()
    {
        if (this.watch2GetherInviteClosed || this.webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var closed = await this.webView.CoreWebView2.ExecuteScriptAsync("""
                (function() {
                    function visible(el) {
                        var r = el.getBoundingClientRect();
                        var s = window.getComputedStyle(el);
                        return r.width > 5 && r.height > 5 && s.visibility !== 'hidden' && s.display !== 'none';
                    }
                    var buttons = Array.from(document.querySelectorAll('button,[role=button],a')).filter(visible);
                    var close = buttons.find(function(el) {
                        var text = (el.innerText || el.textContent || el.getAttribute('aria-label') || '').trim().toLowerCase();
                        return text === 'close' || text === '×' || text === 'x';
                    });
                    if (close) {
                        close.click();
                        return true;
                    }
                    return false;
                })()
                """);
            this.watch2GetherInviteClosed = closed.Contains("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Retry later.
        }
    }

    private async Task CloseWatch2GetherInviteModalAggressivelyAsync()
    {
        if (this.watch2GetherInviteClosed || this.webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var closed = await this.webView.CoreWebView2.ExecuteScriptAsync("""
                (function() {
                    function visible(el) {
                        var r = el.getBoundingClientRect();
                        var s = window.getComputedStyle(el);
                        return r.width > 5 && r.height > 5 && s.visibility !== 'hidden' && s.display !== 'none';
                    }
                    var candidates = Array.from(document.querySelectorAll('button,[role=button],a,input[type=button],input[type=submit],div,span')).filter(visible);
                    var close = candidates.find(function(el) {
                        var text = (el.innerText || el.textContent || el.getAttribute('aria-label') || el.value || '').trim().toLowerCase();
                        return text === 'close' || text === 'x';
                    });
                    if (close) {
                        close.click();
                        return true;
                    }
                    var modal = Array.from(document.querySelectorAll('div')).filter(visible).find(function(el) {
                        var text = (el.innerText || '').toLowerCase();
                        return text.indexOf('invite friends') >= 0 && text.indexOf('copy and share') >= 0;
                    });
                    if (modal) {
                        var r = modal.getBoundingClientRect();
                        var target = document.elementFromPoint(r.right - 38, r.bottom - 34);
                        if (target) {
                            target.click();
                            return true;
                        }
                    }
                    return false;
                })()
                """);
            this.watch2GetherInviteClosed = closed.Contains("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Retry later.
        }
    }

    private async Task CheckLoadWatchdogAsync()
    {
        if (this.webView.CoreWebView2 is null || this.webView.Source is not { } source)
        {
            return;
        }

        var now = DateTime.UtcNow;

        // Watch2Gether owns room loading/playback. Do not reload or poke the page here:
        // doing so causes visible refreshes, notification sounds, and focus loss.
        if (IsWatch2GetherRoomPage(source))
        {
            return;
        }

        if (!IsYouTubeHost(source.Host))
        {
            return;
        }

        if (now - this.navigationStartedUtc < TimeSpan.FromSeconds(4) ||
            now - this.lastLoadRecoveryUtc < TimeSpan.FromSeconds(6))
        {
            return;
        }

        try
        {
            var result = await this.webView.CoreWebView2.ExecuteScriptAsync("""
                (function() {
                    var player = document.querySelector('.html5-video-player');
                    var video = document.querySelector('video');
                    return {
                        hasVideo: !!video,
                        ad: !!(player && player.classList.contains('ad-showing')),
                        time: video ? (video.currentTime || 0) : 0,
                        readyState: video ? (video.readyState || 0) : 0,
                        networkState: video ? (video.networkState || 0) : 0,
                        paused: video ? !!video.paused : true,
                        ended: video ? !!video.ended : false,
                        title: document.title || ''
                    };
                })()
                """);

            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var hasVideo = root.TryGetProperty("hasVideo", out var hasVideoElement) && hasVideoElement.GetBoolean();
            var adShowing = root.TryGetProperty("ad", out var adElement) && adElement.GetBoolean();
            var videoTime = root.TryGetProperty("time", out var timeElement) ? timeElement.GetDouble() : 0;
            var readyState = root.TryGetProperty("readyState", out var readyStateElement) ? readyStateElement.GetInt32() : 0;
            var paused = root.TryGetProperty("paused", out var pausedElement) && pausedElement.GetBoolean();
            var ended = root.TryGetProperty("ended", out var endedElement) && endedElement.GetBoolean();

            if (hasVideo &&
                !ended &&
                (paused || readyState < 3) &&
                now - this.lastStartupPlayKickUtc >= TimeSpan.FromSeconds(3) &&
                this.startupPlayKicks < 6)
            {
                await this.KickPlaybackAsync();
                return;
            }

            if (ended || adShowing || readyState >= 3 || Math.Abs(videoTime - this.lastWatchdogVideoTime) > 0.25)
            {
                this.lastVideoProgressUtc = now;
                this.lastWatchdogVideoTime = videoTime;
                if (readyState >= 3 || videoTime > 1)
                {
                    this.loadRecoveryAttempts = 0;
                }

                return;
            }

            if (hasVideo && !paused && now - this.lastVideoProgressUtc < TimeSpan.FromSeconds(16))
            {
                return;
            }

            if (now - this.lastVideoProgressUtc < TimeSpan.FromSeconds(14))
            {
                return;
            }

            await this.RecoverStuckLoadAsync(hasVideo);
        }
        catch
        {
            // Navigation can race script evaluation. Try again on the next tick.
        }
    }

    /// <summary>
    /// Recovers a Watch2Gether room that loaded but sits on a black screen. Reloading
    /// alone can't fix a player that never STARTED (every reload lands in the same
    /// state), so the sequence is: two play kicks (postMessage playVideo into the
    /// player iframe — a no-op when it's already playing), then a reload, then repeat
    /// once, then stop. Attempts are tracked per room URL so our own reloads don't
    /// reset the count, and everything stands down while the player reports playback.
    /// </summary>
    private async Task RecoverBlackWatch2GetherRoomAsync(Uri source, DateTime now)
    {
        var url = source.GetLeftPart(UriPartial.Path);
        if (!string.Equals(url, this.w2gWatchdogUrl, StringComparison.OrdinalIgnoreCase))
        {
            this.w2gWatchdogUrl = url;
            this.w2gRecoveryAttempts = 0;
        }

        if (this.w2gRecoveryAttempts >= 6 ||
            now - this.navigationStartedUtc < TimeSpan.FromSeconds(8) ||
            now - this.lastLoadRecoveryUtc < TimeSpan.FromSeconds(6))
        {
            return;
        }

        if (await this.IsWatch2GetherPlayingAsync())
        {
            this.w2gRecoveryAttempts = 0;
            return;
        }

        this.lastLoadRecoveryUtc = now;
        var attempt = this.w2gRecoveryAttempts++;
        if (attempt is 2 or 5)
        {
            this.adBlockStatusLabel.Text = "reload";
            this.webView.CoreWebView2?.Reload();
            return;
        }

        this.adBlockStatusLabel.Text = "kick";
        await this.SendWatch2GetherPlayKickAsync();
    }

    /// <summary>
    /// Tells the room's player to start: posts the YouTube IFrame-API playVideo command
    /// into every iframe (the room's player is cross-origin, but postMessage crosses
    /// that boundary) and calls play() on any top-level media element for direct
    /// HTML5 sources. Both are no-ops when playback is already running.
    /// </summary>
    private async Task SendWatch2GetherPlayKickAsync()
    {
        if (this.webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await this.webView.CoreWebView2.ExecuteScriptAsync("""
                (function() {
                    var cmd = JSON.stringify({event: 'command', func: 'playVideo', args: []});
                    document.querySelectorAll('iframe').forEach(function(frame) {
                        try { frame.contentWindow.postMessage(cmd, '*'); } catch (_) {}
                    });
                    document.querySelectorAll('video, audio').forEach(function(el) {
                        try { var p = el.play(); if (p && p.catch) p.catch(function() {}); } catch (_) {}
                    });
                })()
                """);
        }
        catch
        {
            // Navigation can race this; the watchdog retries on a later tick.
        }
    }

    private async Task<bool> IsWatch2GetherPlayingAsync()
    {
        if (this.webView.CoreWebView2 is null)
        {
            return false;
        }

        try
        {
            var result = await this.webView.CoreWebView2.ExecuteScriptAsync(
                "(function(){return {s:(window.__vsYtState|0),age:(Date.now()-(window.__vsYtStamp||0))};})()");
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var state = root.TryGetProperty("s", out var stateElement) ? stateElement.GetInt32() : -1;
            var age = root.TryGetProperty("age", out var ageElement) ? ageElement.GetDouble() : double.MaxValue;

            // YouTube player state 1 == playing, with a fresh time report (< 5s old).
            return state == 1 && age < 5000;
        }
        catch
        {
            return false;
        }
    }

    private async Task RecoverStuckLoadAsync(bool hasVideo)
    {
        this.lastLoadRecoveryUtc = DateTime.UtcNow;
        this.loadRecoveryAttempts++;
        this.adBlockStatusLabel.Text = "retry";

        if (this.loadRecoveryAttempts == 1 && hasVideo && this.webView.CoreWebView2 is not null)
        {
            await this.KickPlaybackAsync();
            return;
        }

        if (this.loadRecoveryAttempts == 3 && this.adBlockEnabled)
        {
            this.SetAdBlockEnabled(false);
            this.adBlockStatusLabel.Text = "relax";
        }

        this.webView.CoreWebView2?.Reload();
        this.lastVideoProgressUtc = DateTime.UtcNow;
    }

    private async Task<bool> IsPageEditingTextAsync()
    {
        if (this.webView.CoreWebView2 is null)
        {
            return false;
        }

        try
        {
            var result = await this.webView.CoreWebView2.ExecuteScriptAsync("""
                (function() {
                    var el = document.activeElement;
                    if (!el) return false;
                    var tag = (el.tagName || '').toLowerCase();
                    return tag === 'input' || tag === 'textarea' || tag === 'select' || !!el.isContentEditable;
                })()
                """);
            return result.Contains("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task KickPlaybackAsync()
    {
        if (this.webView.CoreWebView2 is null)
        {
            return;
        }

        this.lastStartupPlayKickUtc = DateTime.UtcNow;
        this.startupPlayKicks++;
        this.adBlockStatusLabel.Text = "kick";
        try
        {
            await this.webView.CoreWebView2.ExecuteScriptAsync(
                "window.__videoSyncKickPlayback&&window.__videoSyncKickPlayback();");
        }
        catch
        {
            // Navigation may race this; the watchdog will try again.
        }
    }

    private static bool IsYouTubeHost(string host)
    {
        return host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    // Runs on every document (top w2g page included). Captures the embedded YouTube
    // player's state (1 = playing) and last reported time so the watchdog can detect a
    // room that loaded but never actually started rendering the video.
    private const string Watch2GetherProbeScript = """
        (function() {
            if (window.__vsProbeInstalled) { return; }
            window.__vsProbeInstalled = true;
            window.__vsYtState = -1;
            window.__vsYtStamp = 0;
            window.addEventListener('message', function(e) {
                try {
                    if (typeof e.data !== 'string' || e.data.indexOf('infoDelivery') < 0) { return; }
                    var info = (JSON.parse(e.data) || {}).info;
                    if (!info) { return; }
                    if (typeof info.playerState !== 'undefined') { window.__vsYtState = info.playerState; }
                    if (typeof info.currentTime !== 'undefined') { window.__vsYtStamp = Date.now(); }
                } catch (_) {}
            });
        })();
        """;

    private const string YouTubeCleanupScript = """
        (function() {
            function setCaptureOnly(enabled) {
                try {
                    var id = '__videoSyncCaptureOnlyStyle';
                    var oldStyle = document.getElementById(id);
                    if (!enabled) {
                        if (oldStyle) oldStyle.remove();
                        window.__videoSyncCaptureOnly = false;
                        return;
                    }

                    if (!oldStyle) {
                        var style = document.createElement('style');
                        style.id = id;
                        style.textContent = [
                            'html,body,ytd-app,#movie_player,.html5-video-player,video{cursor:none!important;}',
                            'body:not(.__videoSyncInteractive) *{pointer-events:none!important;}',
                            '.ytp-chrome-top,.ytp-chrome-bottom,.ytp-gradient-top,.ytp-gradient-bottom,.ytp-tooltip,.ytp-popup,.ytp-ce-element,.ytp-cards-button,.ytp-cards-teaser,.ytp-paid-content-overlay{opacity:0!important;visibility:hidden!important;pointer-events:none!important;}',
                            '.html5-video-player.ytp-autohide .ytp-chrome-top,.html5-video-player.ytp-autohide .ytp-chrome-bottom{display:none!important;}'
                        ].join('\n');
                        document.documentElement.appendChild(style);
                    }

                    document.body && document.body.classList.remove('__videoSyncInteractive');
                    window.__videoSyncCaptureOnly = true;
                } catch (e) {
                }
            }

            function applyVideoFill() {
                try {
                    if (!location.hostname.includes('youtube.com') && !location.hostname.includes('youtu.be')) return;
                    var oldStyle = document.getElementById('__videoSyncFillStyle');
                    if (oldStyle) oldStyle.remove();

                    if (location.pathname.startsWith('/embed/')) return;

                    var theaterButton = document.querySelector('.ytp-size-button');
                    var player = document.querySelector('.html5-video-player');
                    var watch = document.querySelector('ytd-watch-flexy');
                    if (theaterButton && player && watch && !watch.hasAttribute('theater') && !window.__videoSyncTheaterClicked) {
                        window.__videoSyncTheaterClicked = true;
                        theaterButton.click();
                    }
                } catch (e) {
                }
            }

            function cleanupAds() {
                try {
                    var selectors = [
                        '.ytp-ad-module',
                        '.ytp-ad-overlay-container',
                        '.ytp-ad-player-overlay',
                        '.ytp-ad-text-overlay',
                        '.ytp-ad-image-overlay',
                        '.video-ads',
                        'ytd-ad-slot-renderer',
                        'ytd-display-ad-renderer',
                        'ytd-promoted-sparkles-web-renderer',
                        'ytd-companion-slot-renderer',
                        'ytd-action-companion-ad-renderer',
                        'ytd-player-legacy-desktop-watch-ads-renderer'
                    ];
                    for (var i = 0; i < selectors.length; i++) {
                        document.querySelectorAll(selectors[i]).forEach(function(node) {
                            node.remove();
                        });
                    }

                    document.querySelectorAll('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button').forEach(function(button) {
                        button.click();
                    });

                    var player = document.querySelector('.html5-video-player');
                    var video = document.querySelector('video');
                    if (player && video && player.classList.contains('ad-showing')) {
                        video.currentTime = Number.isFinite(video.duration) ? video.duration : 999999;
                        video.playbackRate = 16;
                        video.play().catch(function() {});
                    }
                } catch (e) {
                }
            }

            function kickPlayback() {
                try {
                    cleanupAds();
                    applyVideoFill();
                    var player = document.querySelector('.html5-video-player');
                    var video = document.querySelector('video');
                    if (!video) return false;

                    if (player && player.classList.contains('ad-showing')) {
                        video.currentTime = Number.isFinite(video.duration) ? video.duration : 999999;
                        video.playbackRate = 16;
                        video.play().catch(function() {});
                        return true;
                    }

                    video.playbackRate = Math.max(0.25, Math.min(4, video.playbackRate || 1));
                    if (video.paused || video.readyState < 3) {
                        var playButton = document.querySelector('.ytp-play-button');
                        if (playButton && video.paused) playButton.click();
                        video.play().catch(function() {});
                    }

                    return true;
                } catch (e) {
                    return false;
                }
            }

            window.__videoSyncCleanupAds = cleanupAds;
            window.__videoSyncApplyVideoFill = applyVideoFill;
            window.__videoSyncSetCaptureOnly = setCaptureOnly;
            window.__videoSyncKickPlayback = kickPlayback;
            cleanupAds();
            applyVideoFill();
            setTimeout(kickPlayback, 1200);
            setTimeout(kickPlayback, 3000);
            setInterval(cleanupAds, 750);
            setTimeout(applyVideoFill, 1500);
        })();
        """;

    private const string VideoFullscreenScript = """
        (function() {
            function setCssFullscreen(enabled) {
                var id = '__videoSyncVideoFullscreenStyle';
                var oldStyle = document.getElementById(id);
                if (!enabled) {
                    if (oldStyle) oldStyle.remove();
                    window.__videoSyncCssFullscreen = false;
                    window.dispatchEvent(new Event('resize'));
                    return;
                }

                if (!oldStyle) {
                    var style = document.createElement('style');
                    style.id = id;
                    style.textContent = [
                        'html,body{margin:0!important;padding:0!important;width:100%!important;height:100%!important;overflow:hidden!important;background:#000!important;}',
                        'ytd-app,#page-manager,ytd-watch-flexy,#columns,#primary,#primary-inner,#player,#player-container-outer,#player-container-inner,#player-container,#movie_player,.html5-video-player{position:fixed!important;inset:0!important;width:100vw!important;height:100vh!important;max-width:none!important;max-height:none!important;z-index:2147483647!important;background:#000!important;}',
                        '#w2g-app,#w2g-main,.w2g-app,.w2g-room,.w2g-player,.w2g-video,.w2g-video-container,.room-player,.player-container,[class*=player],[class*=Player],[class*=video],[class*=Video]{max-width:none!important;max-height:none!important;}',
                        'iframe[src*=youtube],iframe[src*=youtu],iframe[src*=vimeo],iframe[src*=twitch],iframe[src*=dailymotion],iframe[src*=soundcloud]{position:fixed!important;inset:0!important;width:100vw!important;height:100vh!important;max-width:none!important;max-height:none!important;z-index:2147483647!important;background:#000!important;}',
                        'body:has(iframe[src*=youtube]) > *:not(iframe), body:has(iframe[src*=youtu]) > *:not(iframe){pointer-events:none;}',
                        '#secondary,#masthead-container,ytd-masthead,#below,#comments,#related,.ytp-chrome-top,.ytp-gradient-top{display:none!important;}',
                        'video{width:100%!important;height:100%!important;object-fit:contain!important;}'
                    ].join('\n');
                    document.documentElement.appendChild(style);
                }

                window.__videoSyncCssFullscreen = true;
                window.dispatchEvent(new Event('resize'));
            }

            function visible(el) {
                if (!el) return false;
                var r = el.getBoundingClientRect();
                var s = getComputedStyle(el);
                return r.width > 12 && r.height > 12 &&
                    s.display !== 'none' &&
                    s.visibility !== 'hidden' &&
                    Number(s.opacity || 1) > 0.05;
            }

            function clickLikelyFullscreenButton() {
                var selectors = [
                    '.ytp-fullscreen-button',
                    '[aria-label*=Fullscreen i]',
                    '[aria-label*="full screen" i]',
                    '[title*=Fullscreen i]',
                    '[title*="full screen" i]',
                    '[class*=fullscreen i]',
                    '[class*=full-screen i]',
                    '[data-testid*=fullscreen i]',
                    '[data-w2g*=fullscreen i]',
                    '[data-w2g*="fullScreen" i]',
                    'button,[role=button],a,div,span'
                ];
                var seen = new Set();
                var candidates = [];
                selectors.forEach(function(selector) {
                    try {
                        document.querySelectorAll(selector).forEach(function(el) {
                            if (seen.has(el) || !visible(el)) return;
                            seen.add(el);
                            var label = [
                                el.getAttribute('aria-label') || '',
                                el.getAttribute('title') || '',
                                el.getAttribute('data-testid') || '',
                                el.getAttribute('data-w2g') || '',
                                el.className || '',
                                el.innerText || '',
                                el.textContent || ''
                            ].join(' ').toLowerCase();
                            if (label.indexOf('fullscreen') < 0 &&
                                label.indexOf('full screen') < 0 &&
                                label.indexOf('full-screen') < 0 &&
                                label.indexOf('maximize') < 0 &&
                                label.indexOf('open_in_full') < 0) {
                                return;
                            }
                            var r = el.getBoundingClientRect();
                            candidates.push({ el: el, score: (r.bottom * 2) + r.right });
                        });
                    } catch (e) {
                    }
                });

                candidates.sort(function(a, b) { return b.score - a.score; });
                for (var i = 0; i < candidates.length; i++) {
                    try {
                        candidates[i].el.click();
                        return true;
                    } catch (e) {
                    }
                }
                return false;
            }

            var currentlyOn = !!(document.fullscreenElement || window.__videoSyncCssFullscreen);
            var desired = typeof window.__videoSyncDesiredVideoFullscreen === 'boolean'
                ? window.__videoSyncDesiredVideoFullscreen
                : !currentlyOn;
            window.__videoSyncDesiredVideoFullscreen = undefined;

            if (!desired) {
                try {
                    if (document.fullscreenElement && document.exitFullscreen) {
                        document.exitFullscreen().catch(function() {});
                    }
                } catch (e) {
                }

                setCssFullscreen(false);
                return 'off';
            }

            if (currentlyOn) {
                setCssFullscreen(true);
                return 'on';
            }

            if (window.__videoSyncCleanupAds) window.__videoSyncCleanupAds();
            if (window.__videoSyncApplyVideoFill) window.__videoSyncApplyVideoFill();

            clickLikelyFullscreenButton();

            var target =
                document.querySelector('iframe[src*=youtube]') ||
                document.querySelector('iframe[src*=youtu]') ||
                document.querySelector('iframe[src*=vimeo]') ||
                document.querySelector('iframe[src*=twitch]') ||
                document.querySelector('.html5-video-player') ||
                document.querySelector('#movie_player') ||
                document.querySelector('video');

            try {
                if (target && target.requestFullscreen) {
                    target.requestFullscreen({ navigationUI: 'hide' }).catch(function() {});
                }
            } catch (e) {
            }

            try {
                if (!document.fullscreenElement) {
                    clickLikelyFullscreenButton();
                }
            } catch (e) {
            }

            setTimeout(function() {
                if (!document.fullscreenElement) setCssFullscreen(true);
            }, 250);

            return 'on';
        })();
        """;

    /// <summary>
    /// Applies playback commands and spatial-audio state written by the plugin.
    /// </summary>
    private async Task PollControlAsync()
    {
        if (this.webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            if (this.controlPath is not null && File.Exists(this.controlPath))
            {
                var writeTime = File.GetLastWriteTimeUtc(this.controlPath);
                if (writeTime != this.lastControlWriteUtc)
                {
                    this.lastControlWriteUtc = writeTime;
                    using var document = JsonDocument.Parse(File.ReadAllText(this.controlPath));
                    var seq = document.RootElement.GetProperty("seq").GetInt64();
                    if (seq != this.lastControlSeq)
                    {
                        this.lastControlSeq = seq;
                        var command = document.RootElement.GetProperty("cmd").GetString() ?? string.Empty;
                        if (command == "navigate" &&
                            document.RootElement.TryGetProperty("url", out var urlElement) &&
                            urlElement.ValueKind == JsonValueKind.String)
                        {
                            this.Navigate(urlElement.GetString() ?? string.Empty);
                        }
                        else if (command == "scrollto" &&
                            document.RootElement.TryGetProperty("x", out var xElement) &&
                            document.RootElement.TryGetProperty("y", out var yElement))
                        {
                            await this.ScrollToAsync(xElement.GetDouble(), yElement.GetDouble());
                        }
                        else if (command == "syncmedia")
                        {
                            var time = document.RootElement.TryGetProperty("time", out var timeElement)
                                ? timeElement.GetDouble()
                                : 0;
                            var paused = document.RootElement.TryGetProperty("paused", out var pausedElement) &&
                                         pausedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                         pausedElement.GetBoolean();
                            var rate = document.RootElement.TryGetProperty("rate", out var rateElement)
                                ? rateElement.GetDouble()
                                : 1;
                            var muted = document.RootElement.TryGetProperty("muted", out var mutedElement) &&
                                        mutedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                        mutedElement.GetBoolean();
                            var fullscreen = document.RootElement.TryGetProperty("fullscreen", out var fullscreenElement) &&
                                             fullscreenElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                             fullscreenElement.GetBoolean();
                            await this.ApplyMediaStateAsync(time, paused, rate, muted, fullscreen);
                        }
                        else
                        {
                            var value = document.RootElement.TryGetProperty("value", out var valueElement)
                                ? valueElement.GetDouble()
                                : 0;
                            await this.ExecutePlaybackCommandAsync(command, value);
                        }
                    }
                }
            }

            if (this.audioPath is not null && File.Exists(this.audioPath))
            {
                var writeTime = File.GetLastWriteTimeUtc(this.audioPath);
                if (writeTime != this.lastAudioWriteUtc ||
                    DateTime.UtcNow - this.lastAudioApplyUtc >= TimeSpan.FromSeconds(1))
                {
                    this.lastAudioWriteUtc = writeTime;
                    this.lastAudioApplyUtc = DateTime.UtcNow;
                    using var document = JsonDocument.Parse(File.ReadAllText(this.audioPath));
                    var volume = Math.Clamp(document.RootElement.GetProperty("volume").GetDouble(), 0, 1);
                    var pan = Math.Clamp(document.RootElement.GetProperty("pan").GetDouble(), -1, 1);
                    var muted = document.RootElement.GetProperty("muted").GetBoolean();
                    await this.ApplyAudioAsync(volume, pan, muted);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
        {
            // A half-written file; retry on the next tick.
        }
    }

    private async Task ApplyMediaStateAsync(double time, bool paused, double rate, bool muted, bool fullscreen)
    {
        var timeText = Math.Max(0, time).ToString(CultureInfo.InvariantCulture);
        var rateText = Math.Clamp(rate, 0.25, 4).ToString(CultureInfo.InvariantCulture);
        var mutedText = muted ? "true" : "false";
        var pausedText = paused ? "true" : "false";
        var fullscreenText = fullscreen ? "true" : "false";
        await this.webView.CoreWebView2.ExecuteScriptAsync(
            "(function(){" +
            $"var target={timeText},rate={rateText},muted={mutedText},paused={pausedText},fullscreen={fullscreenText};" +
            "var v=document.querySelector('video');if(!v)return;" +
            "if(Number.isFinite(target)&&Math.abs((v.currentTime||0)-target)>0.35)v.currentTime=target;" +
            "if(Number.isFinite(rate))v.playbackRate=Math.max(0.25,Math.min(4,rate));" +
            "v.muted=!!muted;" +
            "if(paused){if(!v.paused)v.pause();}else{if(v.paused)v.play().catch(function(){});}" +
            "window.__videoSyncDesiredVideoFullscreen=!!fullscreen;" +
            "if(window.__videoSyncApplyVideoFill)window.__videoSyncApplyVideoFill();" +
            "})()");

        if (fullscreen != this.videoFullscreenMode)
        {
            var result = await this.webView.CoreWebView2.ExecuteScriptAsync(VideoFullscreenScript);
            this.videoFullscreenMode = result.Contains("on", StringComparison.OrdinalIgnoreCase);
            await this.ApplyCaptureInteractionAsync(this.captureFramePath is not null && !this.browserWindowShown);
        }
    }

    private async Task ExecutePlaybackCommandAsync(string command, double value)
    {
        if (command == "show")
        {
            this.ShowBrowserWindow();
            return;
        }

        if (command == "hide")
        {
            this.HideBrowserWindow();
            return;
        }

        if (command == "adblock")
        {
            this.SetAdBlockEnabled(value >= 0.5);
            return;
        }

        if (command == "applyoptions")
        {
            var flags = (int)Math.Round(value);
            this.SetAdBlockEnabled((flags & 1) != 0);
            if ((flags & 4) != 0)
            {
                this.HideBrowserWindow();
            }
            else
            {
                this.ShowBrowserWindow();
            }

            await this.webView.CoreWebView2.ExecuteScriptAsync(
                $"window.__videoSyncDesiredVideoFullscreen={((flags & 2) != 0 ? "true" : "false")};");
            var result = await this.webView.CoreWebView2.ExecuteScriptAsync(VideoFullscreenScript);
            this.videoFullscreenMode = result.Contains("on", StringComparison.OrdinalIgnoreCase);
            await this.ApplyCaptureInteractionAsync(this.captureFramePath is not null && !this.browserWindowShown);
            return;
        }

        if (command == "videofullscreen")
        {
            var result = await this.webView.CoreWebView2.ExecuteScriptAsync(VideoFullscreenScript);
            this.videoFullscreenMode = result.Contains("on", StringComparison.OrdinalIgnoreCase);
            await this.ApplyCaptureInteractionAsync(this.captureFramePath is not null && !this.browserWindowShown);
            return;
        }

        if (command == "setvideofullscreen")
        {
            await this.webView.CoreWebView2.ExecuteScriptAsync(
                $"window.__videoSyncDesiredVideoFullscreen={(value >= 0.5 ? "true" : "false")};");
            var result = await this.webView.CoreWebView2.ExecuteScriptAsync(VideoFullscreenScript);
            this.videoFullscreenMode = result.Contains("on", StringComparison.OrdinalIgnoreCase);
            await this.ApplyCaptureInteractionAsync(this.captureFramePath is not null && !this.browserWindowShown);
            return;
        }

        // On a Watch2Gether room, playback belongs to w2g's synced player, not to a raw
        // <video> element (a YouTube room plays inside a cross-origin iframe we can't reach,
        // and w2g re-asserts state on anything we force). w2g's OWN keyboard shortcuts DO
        // drive the synced player, so we replay them: Space toggles play/pause and the arrow
        // keys skip 10s — and those changes propagate to everyone in the room.
        var valueText = value.ToString(CultureInfo.InvariantCulture);
        var script = command switch
        {
            "play" => "(function(){var v=document.querySelector('video');if(v)v.play();})()",
            "pause" => "(function(){var v=document.querySelector('video');if(v)v.pause();})()",
            "seek" => $"(function(){{var v=document.querySelector('video');if(v)v.currentTime={valueText};}})()",
            "syncplay" => $"(function(){{var t={valueText};if(window.__videoSyncCleanupAds)window.__videoSyncCleanupAds();if(window.__videoSyncApplyVideoFill)window.__videoSyncApplyVideoFill();var p=document.querySelector('.html5-video-player');var v=document.querySelector('video');if(!v)return;if(p&&p.classList.contains('ad-showing')){{v.currentTime=Number.isFinite(v.duration)?v.duration:999999;return;}}v.playbackRate=1;if(Math.abs((v.currentTime||0)-t)>0.20)v.currentTime=t;if(v.paused)v.play().catch(function(){{}});}})()",
            "syncpause" => $"(function(){{var t={valueText};if(window.__videoSyncCleanupAds)window.__videoSyncCleanupAds();if(window.__videoSyncApplyVideoFill)window.__videoSyncApplyVideoFill();var p=document.querySelector('.html5-video-player');var v=document.querySelector('video');if(!v)return;if(p&&p.classList.contains('ad-showing')){{v.currentTime=Number.isFinite(v.duration)?v.duration:999999;return;}}v.playbackRate=1;if(Math.abs((v.currentTime||0)-t)>0.20)v.currentTime=t;if(!v.paused)v.pause();}})()",
            "syncsoftplay" => $"(function(){{var t={valueText};if(window.__videoSyncCleanupAds)window.__videoSyncCleanupAds();var p=document.querySelector('.html5-video-player');var v=document.querySelector('video');if(!v)return;if(p&&p.classList.contains('ad-showing')){{v.currentTime=Number.isFinite(v.duration)?v.duration:999999;return;}}var drift=t-(v.currentTime||0);if(Math.abs(drift)>1.25){{v.currentTime=t;v.playbackRate=1;}}else{{v.playbackRate=Math.max(0.92,Math.min(1.08,1+(drift*0.08)));window.clearTimeout(window.__videoSyncRateReset);window.__videoSyncRateReset=window.setTimeout(function(){{if(v)v.playbackRate=1;}},2500);}}if(v.paused)v.play().catch(function(){{}});}})()",
            "nudge" => $"(function(){{var v=document.querySelector('video');if(v)v.currentTime=Math.max(0,v.currentTime+({valueText}));}})()",
            _ => null,
        };

        if (script is not null)
        {
            await this.webView.CoreWebView2.ExecuteScriptAsync(script);
        }
    }

    private bool IsOnWatch2GetherRoom()
    {
        return this.webView.Source is { } source &&
               source.Host.Equals("w2g.tv", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Makes a room actually audible by driving Watch2Gether's OWN volume control to
    /// <paramref name="volume"/> (0-1) — the same slider you'd drag by hand. w2g launches at a
    /// low/zero volume (a long-standing "max volume on launch" issue), which is the real reason
    /// rooms are silent; the YouTube video itself lives in a cross-origin iframe we can't touch,
    /// but w2g's <c>#volume_slider</c> is same-origin and w2g relays it to whatever player it's
    /// using. We drive the slider directly (using the native value setter + input/change events,
    /// so w2g's React handler fires), unmute any top-level media, and also poke the YouTube
    /// IFrame API as a belt-and-suspenders fallback.
    /// </summary>
    private async Task SyncWatch2GetherPlayerAudioAsync(double volume)
    {
        this.lastRoomVolume = Math.Clamp(volume, 0, 1);
        await Task.CompletedTask;
    }

    private async Task TickWatch2GetherAudioAsync()
    {
        // The player (and w2g's volume slider) initialize a moment AFTER the page loads, so a
        // single sync on navigation can fire too early. Re-assert it for a short window after
        // landing, using the most recent desired volume (or full, before audio.json arrives).
        if (this.watch2GetherAudioSyncTicks <= 0 || !this.IsOnWatch2GetherRoom())
        {
            return;
        }

        this.watch2GetherAudioSyncTicks--;
        try
        {
            this.webView.CoreWebView2.IsMuted = this.lastRoomMuted;
        }
        catch
        {
            // Best effort.
        }

        await Task.Run(() => AudioSessionVolume.Apply(
            this.lastRoomMuted ? 0f : (float)this.lastRoomVolume,
            (float)this.lastRoomPan,
            this.lastRoomMuted));
        await this.SyncWatch2GetherPlayerAudioAsync(this.lastRoomVolume);
    }

    private void SetAdBlockEnabled(bool enabled)
    {
        this.adBlockEnabled = enabled;
        if (this.adBlockCheckBox.Checked != enabled)
        {
            this.adBlockCheckBox.Checked = enabled;
        }

        if (enabled)
        {
            _ = this.CleanupYouTubeAdsAsync();
        }
    }

    /// <summary>
    /// Routes the video through a WebAudio StereoPanner so the plugin can drive
    /// distance-based volume and left/right pan, like PyonPix's spatial audio.
    /// </summary>
    private async Task ApplyAudioAsync(double volume, double pan, bool muted)
    {
        try
        {
            this.webView.CoreWebView2.IsMuted = muted;
        }
        catch
        {
            // Older runtimes may not expose this reliably; the page script still mutes media elements.
        }

        if (this.IsOnWatch2GetherRoom())
        {
            this.lastRoomVolume = Math.Clamp(volume, 0, 1);
            this.lastRoomPan = Math.Clamp(pan, -1, 1);
            this.lastRoomMuted = muted;
            this.watch2GetherAudioSyncTicks = Math.Max(this.watch2GetherAudioSyncTicks, 20);
            await Task.Run(() => AudioSessionVolume.Apply(
                muted ? 0f : (float)this.lastRoomVolume,
                (float)this.lastRoomPan,
                muted));
            // Level via w2g's OWN volume slider (same-origin and reliable — it's what you'd
            // drag by hand), so the plugin's volume slider drives the room directly even if the
            // OS-level path below never matches a session. Mute is handled by IsMuted above.
            // WASAPI is then used ONLY for stereo pan (master left at 1.0 so we don't attenuate
            // the level twice); if it can't find the session, we simply lose pan, not audio.
            if (!muted)
            {
                await this.SyncWatch2GetherPlayerAudioAsync(volume);
            }

            return;
        }

        // Direct "Play just for me" video: reset the OS volume to neutral (so a previous
        // room's pan/level doesn't linger) and let the precise WebAudio panner on the real
        // <video> element do the spatial work.
        await Task.Run(() => AudioSessionVolume.Apply(1f, 0f, false));

        var volumeText = volume.ToString("0.###", CultureInfo.InvariantCulture);
        var panText = pan.ToString("0.###", CultureInfo.InvariantCulture);
        var mutedText = muted ? "true" : "false";
        var script = $$"""
            (function(vol, pan, muted) {
                var media = Array.from(document.querySelectorAll('video,audio'));
                media.forEach(function(v) {
                    try {
                        v.muted = muted;
                        v.volume = muted ? 0 : vol;
                    } catch (e) { }
                });
                var v = media.find(function(el) { return el.tagName && el.tagName.toLowerCase() === 'video'; }) || media[0];
                if (!v) return false;
                try {
                    if (!window.__vsCtx) {
                        window.__vsCtx = new (window.AudioContext || window.webkitAudioContext)();
                        window.__vsPan = window.__vsCtx.createStereoPanner();
                        window.__vsGain = window.__vsCtx.createGain();
                        window.__vsPan.connect(window.__vsGain);
                        window.__vsGain.connect(window.__vsCtx.destination);
                    }
                    if (window.__vsVideo !== v) {
                        try { if (window.__vsSrc) window.__vsSrc.disconnect(); } catch (e) { }
                        window.__vsVideo = v;
                        window.__vsSrc = window.__vsCtx.createMediaElementSource(v);
                        window.__vsSrc.connect(window.__vsPan);
                    }
                    if (window.__vsCtx.state === 'suspended') window.__vsCtx.resume();
                    window.__vsPan.pan.value = pan;
                    if (window.__vsGain) window.__vsGain.gain.value = muted ? 0 : vol;
                } catch (e) { }
                return true;
            })({{volumeText}}, {{panText}}, {{mutedText}})
            """;
        await this.webView.CoreWebView2.ExecuteScriptAsync(script);
    }

    /// <summary>
    /// Scrolls the page to an absolute position. Close targets animate smoothly so followers
    /// see the host's reading motion; far jumps snap instantly instead of animating for ages.
    /// Interrupted smooth scrolls are fine — the next sync update re-targets.
    /// </summary>
    private async Task ScrollToAsync(double x, double y)
    {
        if (this.webView.CoreWebView2 is null)
        {
            return;
        }

        var xText = Math.Max(0, x).ToString(CultureInfo.InvariantCulture);
        var yText = Math.Max(0, y).ToString(CultureInfo.InvariantCulture);
        await this.webView.CoreWebView2.ExecuteScriptAsync(
            $"(function(){{var tx={xText},ty={yText};" +
            "var dx=Math.abs((window.scrollX||0)-tx),dy=Math.abs((window.scrollY||0)-ty);" +
            "if(dx<1&&dy<1)return;" +
            "var smooth=(dx+dy)<2500;" +
            "window.scrollTo({left:tx,top:ty,behavior:smooth?'smooth':'auto'});})()");
    }

    /// <summary>
    /// Publishes current browser URL and playback state for the plugin UI.
    /// </summary>
    private async Task PublishStatusAsync()
    {
        if (this.statusPath is null || this.statusInProgress || this.webView.CoreWebView2 is null)
        {
            return;
        }

        this.statusInProgress = true;
        try
        {
            var result = await this.webView.CoreWebView2.ExecuteScriptAsync(
                "(function(){var v=document.querySelector('video');var path=location.pathname||'';var room=null;" +
                "var vs=document.querySelector('#volume_slider')||document.querySelector('.player-volume input')||document.querySelector('.player-volume>div>input');" +
                "var media=document.querySelectorAll('video,audio');var frames=document.querySelectorAll('iframe');" +
                "var ytn=0;for(var fi=0;fi<frames.length;fi++){var fs=frames[fi].src||'';if(fs.indexOf('youtube')>=0||fs.indexOf('ytimg')>=0)ytn++;}" +
                "var invite=Array.from(document.querySelectorAll('input,textarea,[contenteditable=true]')).map(function(el){return el.value||el.textContent||el.getAttribute('value')||'';}).join(' ');" +
                "invite += ' ' + document.body.innerText + ' ' + document.body.innerHTML;" +
                "var match=invite.match(/https?:\\/\\/w2g\\.tv\\/(?:\\?r=|rooms\\/|en\\/room\\/mobile\\/\\?access_key=)[^\\s\"'<>]+/i);" +
                "invite=match?match[0]:null;" +
                "if(invite){room=invite;}" +
                "else if(location.hostname==='w2g.tv'&&path.indexOf('/rooms/')===0){var parts=path.split('/').filter(Boolean);if(parts.length>=2)room=location.origin+'/'+parts[0]+'/'+parts[1];}" +
                "else if(location.hostname==='w2g.tv'&&(path.indexOf('/room/')>=0||location.search.indexOf('access_key=')>=0)){room=location.href;}" +
                $"return {{url:location.href,title:(document.title||'').slice(0,120),sx:Math.max(0,Math.round(window.scrollX||window.pageXOffset||0)),sy:Math.max(0,Math.round(window.scrollY||window.pageYOffset||0)),vw:Math.max(0,Math.round(window.innerWidth||document.documentElement.clientWidth||0)),vh:Math.max(0,Math.round(window.innerHeight||document.documentElement.clientHeight||0)),dw:Math.max(0,Math.round(Math.max(document.documentElement.scrollWidth||0,document.body?document.body.scrollWidth||0:0))),dh:Math.max(0,Math.round(Math.max(document.documentElement.scrollHeight||0,document.body?document.body.scrollHeight||0:0))),z:(window.visualViewport&&window.visualViewport.scale)||1,fs:!!(document.fullscreenElement||window.__videoSyncCssFullscreen),w2gRoom:room,w2gShareAttempted:{(this.watch2GetherShareAttempted ? "true" : "false")},w2gShareSubmitted:{(this.watch2GetherShareSubmitted ? "true" : "false")},webViewMuted:{(this.webView.CoreWebView2.IsMuted ? "true" : "false")},webViewAudio:{(this.webView.CoreWebView2.IsDocumentPlayingAudio ? "true" : "false")},volSlider:(vs?vs.value:-1),mediaN:media.length,iframeN:frames.length,ytFrame:ytn,vVol:(v?v.volume:-1),vMuted:(v?!!v.muted:false),vRS:(v?v.readyState:-1),rate:(v?(v.playbackRate||1):1),t:v?(v.currentTime||0):0,d:(v&&isFinite(v.duration)?v.duration:0),p:v?!!v.paused:true}};}})()");
            if (!string.IsNullOrEmpty(result) && result.StartsWith('{'))
            {
                var tempPath = this.statusPath + ".tmp";
                await File.WriteAllTextAsync(tempPath, result);
                File.Move(tempPath, this.statusPath, true);
            }
        }
        catch
        {
            // Status is best-effort; the plugin shows the last known state.
        }
        finally
        {
            this.statusInProgress = false;
        }
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private void Navigate(string url)
    {
        url = ForceDesktopSite(url.Trim());
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (LooksLikeHostname(url))
            {
                uri = new Uri("https://" + url);
            }
            else
            {
                uri = new Uri("https://www.google.com/search?q=" + Uri.EscapeDataString(url));
            }
        }

        this.webView.Source = uri;
    }

    /// <summary>
    /// Rewrites a URL to the desktop site for the players we drive, so neither
    /// Watch2Gether nor YouTube can hand us a mobile page (the black-player /
    /// "switch to desktop version" state). Anything else passes through untouched.
    /// </summary>
    private static string ForceDesktopSite(string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var host = uri.Host.ToLowerInvariant();

        // Watch2Gether room links are opaque. Keep the server-provided path/query intact
        // and force desktop through browser identity + viewport instead of guessing a URL.
        if (host == "w2g.tv")
        {
            return uri.ToString();
        }

        // YouTube: pull m.youtube.com back to www, and pin app=desktop so it never
        // downgrades the watch page to the mobile player.
        if (host is "m.youtube.com" or "youtube.com" or "www.youtube.com")
        {
            var builder = new UriBuilder(uri) { Host = "www.youtube.com" };
            if (uri.AbsolutePath.StartsWith("/watch", StringComparison.OrdinalIgnoreCase) &&
                !uri.Query.Contains("app=desktop", StringComparison.OrdinalIgnoreCase))
            {
                var query = uri.Query.TrimStart('?');
                builder.Query = string.IsNullOrEmpty(query) ? "app=desktop" : query + "&app=desktop";
            }

            return builder.Uri.ToString();
        }

        return url;
    }

    private void ShowBrowserWindow()
    {
        this.browserWindowShown = true;

        // If foreground capture cloaked / click-through'd the window, restore it so the user
        // can actually see and interact with the browser while navigating. HideBrowserWindow
        // re-applies both.
        SetCloaked(this.Handle, false);
        SetClickThrough(this.Handle, false);
        _ = this.ApplyCaptureInteractionAsync(captureOnly: false);
        if (this.WindowState == FormWindowState.Minimized)
        {
            this.WindowState = FormWindowState.Normal;
        }

        this.Enabled = true;
        var hideCapturedChrome = this.captureFramePath is not null && this.videoFullscreenMode;
        this.toolbar.Visible = !hideCapturedChrome;
        this.hintBar.Visible = !hideCapturedChrome;

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        this.FormBorderStyle = this.captureFramePath is not null ? FormBorderStyle.None : FormBorderStyle.Sizable;
        // While the user is navigating with the window visible, keep it on-screen: the
        // capture size may exceed the monitor (e.g. 4K), so clamp to the working area here.
        // It returns to the full capture size when buried again (HideBrowserWindow).
        this.ClientSize = new Size(
            Math.Min(this.desktopCaptureSize.Width, area.Width),
            Math.Min(this.desktopCaptureSize.Height, area.Height));
        this.Location = new Point(
            area.Left + ((area.Width - this.Width) / 2),
            area.Top + ((area.Height - this.Height) / 2));
        this.TopMost = true;
        this.topMostButton.Text = "Top";
        this.Show();
        SetWindowPos(
            this.Handle,
            HwndTopMost,
            this.Left,
            this.Top,
            this.Width,
            this.Height,
            0);
        ForceForegroundWindow(this.Handle);
        this.BringToFront();
        this.Activate();
        this.Focus();
        if (this.toolbar.Visible)
        {
            this.addressBar.Focus();
            this.addressBar.SelectAll();
        }
        else
        {
            this.webView.Focus();
        }
    }

    private void HideBrowserWindow()
    {
        this.browserWindowShown = false;
        _ = this.ApplyCaptureInteractionAsync(captureOnly: this.captureFramePath is not null);
        this.TopMost = false;
        if (this.captureFramePath is null)
        {
            this.WindowState = FormWindowState.Minimized;
            return;
        }

        this.toolbar.Visible = false;
        this.hintBar.Visible = false;
        this.ClientSize = this.desktopCaptureSize;
        this.Location = new Point(0, 0);
        this.BuryCaptureWindow();
    }

    private void BuryCaptureWindow()
    {
        if (!this.IsHandleCreated)
        {
            return;
        }

        if (this.foregroundCapture)
        {
            // Experimental path: instead of hiding the window behind the game (where DWM
            // throttles its composition and caps capture fps), keep it top-most so DWM sees
            // it as unoccluded and composites it every refresh, then DWM-cloak it so it is
            // never actually drawn over the game. Cloaking is a DWM attribute, not a window
            // style, so WGC still accepts and captures the window.
            //
            // Belt-and-suspenders: also make it click-through (WS_EX_TRANSPARENT). If cloaking
            // ever failed, a top-most opaque window could otherwise swallow the game's input;
            // click-through guarantees input always reaches the game, so the worst case is just
            // a visible overlay the user can turn off — never a locked game.
            SetClickThrough(this.Handle, true);
            SetCloaked(this.Handle, true);
            SetWindowPos(
                this.Handle,
                HwndTopMost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
            return;
        }

        SetClickThrough(this.Handle, false);
        SetCloaked(this.Handle, false);
        this.TopMost = false;
        SetWindowPos(
            this.Handle,
            HwndBottom,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    // DWM cloaking hides a window from the desktop while DWM keeps compositing its content,
    // which is exactly what a capture source wants: invisible to the player, still delivering
    // frames to Windows.Graphics.Capture at full rate.
    private static void SetCloaked(IntPtr handle, bool cloaked)
    {
        try
        {
            var value = cloaked ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaCloak, ref value, sizeof(int));
        }
        catch
        {
            // dwmapi is present on every supported OS; ignore if it ever fails so capture
            // still runs (just without the experimental composition boost).
        }
    }

    // Toggles WS_EX_TRANSPARENT so mouse input passes straight through the window to whatever
    // is beneath it (the game). Used only in foreground-capture mode as an input safety net.
    private static void SetClickThrough(IntPtr handle, bool enabled)
    {
        try
        {
            var ex = (long)GetWindowLongPtr(handle, GwlExStyle);
            ex = enabled ? (ex | WsExTransparent) : (ex & ~(long)WsExTransparent);
            SetWindowLongPtr(handle, GwlExStyle, (IntPtr)ex);
        }
        catch
        {
            // Non-fatal: without click-through the cloak still hides the window; we just lose
            // the extra input safety net.
        }
    }

    private static void ForceForegroundWindow(IntPtr handle)
    {
        ShowWindow(handle, 9);
        BringWindowToTop(handle);

        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = false;

        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            attached = AttachThreadInput(currentThread, foregroundThread, true);
        }

        try
        {
            SetForegroundWindow(handle);
            SetActiveWindow(handle);
            SetFocus(handle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private static bool LooksLikeHostname(string text)
    {
        return text.Contains(".", StringComparison.Ordinal) &&
               !text.Contains(" ", StringComparison.Ordinal) &&
               !text.Contains("\\", StringComparison.Ordinal) &&
               !text.StartsWith(".", StringComparison.Ordinal) &&
               !text.EndsWith(".", StringComparison.Ordinal);
    }

    private void BeginWindowDrag(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !this.browserWindowShown)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(this.Handle, WmNcLButtonDown, HtCaption, 0);
    }

    private static void StyleToolbarButton(Button button)
    {
        button.BackColor = Color.FromArgb(28, 28, 28);
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(52, 52, 52);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 45, 45);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(64, 64, 64);
    }

    // The gold "Done" button — the visual anchor of the toolbar, matching the plugin's
    // primary in-game button so the two surfaces feel like one product.
    private static void StyleAccentButton(Button button)
    {
        button.BackColor = Accent;
        button.ForeColor = AccentInk;
        button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        button.FlatAppearance.MouseDownBackColor = AccentHover;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    private async Task CaptureFrameAsync()
    {
        // The PNG snapshot is a low-rate fallback for the Dalamud texture path.
        // GPU shared-texture streaming is still the primary path, but keeping this
        // alive prevents the in-world screen from going blank if a shared handle
        // fails to open on the game device.
        if (this.captureFramePath is null || this.captureInProgress || this.webView.CoreWebView2 is null)
        {
            return;
        }

        // JPEG fallback, still used by the in-window preview and as a backup
        // texture source for the native world screen.
        this.captureInProgress = true;
        var tempPath = this.captureFramePath + ".tmp";

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await this.webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
            }

            File.Move(tempPath, this.captureFramePath, true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
        finally
        {
            this.captureInProgress = false;
        }
    }
}

/// <summary>
/// Captures this process's window with Windows.Graphics.Capture and copies every
/// frame into a shared D3D11 texture. The shared handle is published through a JSON
/// sidecar file so the Dalamud plugin can open the texture on the game's device and
/// stream the browser output in real time, PyonPix-style.
/// </summary>
internal sealed class SharedFrameStreamer : IDisposable
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly object sync = new();
    private readonly string infoPath;
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDirect3DDevice direct3DDevice;
    private readonly GraphicsCaptureItem captureItem;
    private readonly Direct3D11CaptureFramePool framePool;
    private readonly GraphicsCaptureSession session;
    private ID3D11Texture2D? sharedTexture;
    private long sharedHandleValue;
    private long frameCount;
    private DateTime lastInfoWriteUtc;
    private uint sharedWidth;
    private uint sharedHeight;
    private SizeInt32 poolSize;
    private bool disposed;

    public static SharedFrameStreamer? TryStart(IntPtr windowHandle, string infoPath, long? adapterLuid, int bufferCount)
    {
        try
        {
            return new SharedFrameStreamer(windowHandle, infoPath, adapterLuid, bufferCount);
        }
        catch (Exception ex)
        {
            // Windows.Graphics.Capture is unavailable (old OS, no GPU, remote session)
            // or the window is not capturable. The PNG fallback keeps working.
            try
            {
                File.WriteAllText(infoPath + ".error.txt", ex.ToString());
            }
            catch
            {
                // Diagnostics only.
            }

            return null;
        }
    }

    private SharedFrameStreamer(IntPtr windowHandle, string infoPath, long? adapterLuid, int bufferCount)
    {
        this.infoPath = infoPath;

        using var adapter = FindAdapter(adapterLuid);
        D3D11.D3D11CreateDevice(
            adapter,
            adapter is null ? DriverType.Hardware : DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            null,
            out this.device).CheckError();
        this.context = this.device.ImmediateContext;

        using (var dxgiDevice = this.device.QueryInterface<IDXGIDevice>())
        {
            var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var abi);
            if (hr < 0)
            {
                throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{(uint)hr:X8}");
            }

            this.direct3DDevice = MarshalInterface<IDirect3DDevice>.FromAbi(abi);
            Marshal.Release(abi);
        }

        this.captureItem = CreateItemForWindow(windowHandle);
        this.poolSize = this.captureItem.Size;

        // Buffer count comes from the capture mode (Default 3, Smooth/High FPS 5): more slack
        // means a slow CopyResource frame doesn't force WGC to drop the next frame, which
        // shows up as a lower captured fps. More buffers mostly just add latency.
        this.framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            this.direct3DDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            Math.Clamp(bufferCount, 2, 6),
            this.poolSize);
        this.framePool.FrameArrived += this.OnFrameArrived;
        this.session = this.framePool.CreateCaptureSession(this.captureItem);
        TrySet(() => this.session.IsCursorCaptureEnabled = false);
        TrySet(() => this.session.IsBorderRequired = false);
        this.session.StartCapture();
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            TrySet(this.session.Dispose);
            TrySet(this.framePool.Dispose);
            this.sharedTexture?.Dispose();
            this.sharedTexture = null;
            this.context.Dispose();
            this.device.Dispose();
            this.direct3DDevice.Dispose();
            try
            {
                if (File.Exists(this.infoPath))
                {
                    File.Delete(this.infoPath);
                }
            }
            catch
            {
                // The plugin also clears the sidecar when the preview stops.
            }
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            using var frameTexture = GetTextureFromSurface(frame.Surface);
            var desc = frameTexture.Description;
            if (this.sharedTexture is null || desc.Width != this.sharedWidth || desc.Height != this.sharedHeight)
            {
                this.RecreateSharedTexture(desc.Width, desc.Height);
            }

            this.context.CopyResource(this.sharedTexture!, frameTexture);
            this.context.Flush();

            // Refresh the sidecar periodically with a frame counter so the plugin (and
            // diagnostics) can tell whether frames are still arriving.
            this.frameCount++;
            var now = DateTime.UtcNow;
            if ((now - this.lastInfoWriteUtc).TotalSeconds >= 2)
            {
                this.lastInfoWriteUtc = now;
                this.WriteSidecar();
            }

            if (frame.ContentSize.Width != this.poolSize.Width || frame.ContentSize.Height != this.poolSize.Height)
            {
                this.poolSize = frame.ContentSize;
                sender.Recreate(this.direct3DDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, this.poolSize);
            }
        }
    }

    private void RecreateSharedTexture(uint width, uint height)
    {
        this.sharedTexture?.Dispose();
        this.sharedTexture = this.device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            MiscFlags = ResourceOptionFlags.Shared,
        });
        this.sharedWidth = width;
        this.sharedHeight = height;

        using var dxgiResource = this.sharedTexture.QueryInterface<IDXGIResource>();
        this.sharedHandleValue = dxgiResource.SharedHandle.ToInt64();
        this.lastInfoWriteUtc = DateTime.UtcNow;
        this.WriteSidecar();
    }

    private static IDXGIAdapter1? FindAdapter(long? adapterLuid)
    {
        if (adapterLuid is null)
        {
            return null;
        }

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0; ; index++)
        {
            var result = factory.EnumAdapters1(index, out var adapter);
            if (result.Failure)
            {
                return null;
            }

            if ((long)adapter.Description1.Luid == adapterLuid.Value)
            {
                return adapter;
            }

            adapter.Dispose();
        }
    }

    private void WriteSidecar()
    {
        try
        {
            var tempPath = this.infoPath + ".tmp";
            File.WriteAllText(
                tempPath,
                $"{{\"handle\":{this.sharedHandleValue},\"width\":{this.sharedWidth},\"height\":{this.sharedHeight},\"frames\":{this.frameCount}}}");
            File.Move(tempPath, this.infoPath, true);
        }
        catch (IOException)
        {
            // The plugin may be reading the file; retry on a later frame.
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr windowHandle)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemGuid;
        var abi = interop.CreateForWindow(windowHandle, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    private static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        var texturePtr = access.GetInterface(ref iid);
        return new ID3D11Texture2D(texturePtr);
    }

    private static void TrySet(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Optional capture-session features are not available on every OS build.
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);

        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }
}
