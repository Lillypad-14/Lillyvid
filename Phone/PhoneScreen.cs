using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using VideoSyncPrototype.Phone.Apps.Games;
using VideoSyncPrototype.Phone.Apps.LillypadGo;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Animation;
using VideoSyncPrototype.Phone.Core.Apps;
using VideoSyncPrototype.Phone.Core.Games;
using VideoSyncPrototype.Phone.Core.Localization;
using VideoSyncPrototype.Phone.Core.Onboarding;
using VideoSyncPrototype.Phone.Core.Shell;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone;

// Trimmed phone shell for Lillypad Toolkit's "Games" tab. Faithfully reproduces
// Aetherphone's (https://github.com/XeldarAlz/FFXIV-Aetherphone, AGPL-3.0-or-later)
// device chrome, wallpaper, status bar, home screen and slide navigation, hosting the
// Games app and its 16 mini-games. Aetherphone's networked shell surfaces (calls,
// messages, notifications, control center, onboarding) are intentionally omitted: they
// require Aetherphone's own backend and cannot run standalone.
internal sealed class PhoneScreen : IDisposable
{
    private readonly ThemeProvider themes;
    private readonly IReadOnlyList<IPhoneApp> apps;
    private readonly NavigationStack navigation;
    private readonly HomeScreen home;

    public PhoneScreen(Configuration config)
    {
        themes = new ThemeProvider(config);
        apps = new IPhoneApp[] { new GamesApp(new GameStatsStore(config)), new LillypadGoApp() };
        navigation = new NavigationStack(apps);
        home = new HomeScreen(apps);
    }

    // When true the host window should refuse to move, so click-and-drag inside the phone
    // (home screen, games) doesn't drag the whole Lillypad window around.
    public bool LockActive => Plugin.Cfg.LockPosition;

    // When true the host window drops all its chrome (title bar, tabs, background) and shows
    // only the phone, like a floating device.
    public bool StandaloneActive => Plugin.Cfg.StandaloneMode;

    public void Draw(Rect device)
    {
        using (Plugin.Fonts.Push(1f))
        {
            var delta = MathF.Min(ImGui.GetIO().DeltaTime, TransitionTiming.MaxFrameSeconds);
            Plugin.Wallpapers.StepDayNight(delta);
            Plugin.Device.SyncTarget();
            var theme = themes.Chrome;
            var screen = DeviceChrome.DrawBody(device, theme, true);
            navigation.Advance(delta);
            UiAnchors.BeginFrame(false);
            DrawContent(screen, theme);
            DrawChrome(screen, theme);
            DeviceChrome.DrawBrightnessVeil(screen, theme, 1f);
        }
    }

    private void DrawContent(Rect screen, PhoneTheme theme)
    {
        if (navigation.IsTransitioning)
        {
            DrawTransition(screen, theme);
        }
        else if (navigation.AtHome)
        {
            PaintHome(screen, theme);
        }
        else
        {
            using (ImRaii.PushId(navigation.Current!.Id))
            {
                PaintApp(screen, theme, navigation.Current!);
            }
        }
    }

    private void DrawChrome(Rect screen, PhoneTheme theme)
    {
        StatusBar.Draw(screen, theme);
        DrawHomeIndicator(screen, theme);
        DrawStandaloneToggle(screen, theme);
        DrawPositionLock(screen, theme);
    }

    private void DrawStandaloneToggle(Rect screen, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var radius = 10f * scale;
        var center = new Vector2(screen.Max.X - 58f * scale, screen.Max.Y - 15.5f * scale);
        var icon = Plugin.Cfg.StandaloneMode ? FontAwesomeIcon.Compress : FontAwesomeIcon.Expand;
        if (LockButton.Draw(center, radius, icon, Plugin.Cfg.StandaloneMode, theme))
        {
            Plugin.Cfg.StandaloneMode = !Plugin.Cfg.StandaloneMode;
            Plugin.Cfg.Save();
        }

        if (ImGui.IsMouseHoveringRect(center - new Vector2(radius), center + new Vector2(radius)))
        {
            ImGui.SetTooltip(Plugin.Cfg.StandaloneMode ? "Show the full window" : "Show only the phone");
        }
    }

    private void DrawPositionLock(Rect screen, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var radius = 10f * scale;
        var center = new Vector2(screen.Max.X - 34f * scale, screen.Max.Y - 15.5f * scale);
        var icon = Plugin.Cfg.LockPosition ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen;
        if (LockButton.Draw(center, radius, icon, Plugin.Cfg.LockPosition, theme))
        {
            Plugin.Cfg.LockPosition = !Plugin.Cfg.LockPosition;
            Plugin.Cfg.Save();
        }

        if (ImGui.IsMouseHoveringRect(center - new Vector2(radius), center + new Vector2(radius)))
        {
            ImGui.SetTooltip(Loc.T(Plugin.Cfg.LockPosition ? L.Plugin.UnlockPositionHint : L.Plugin.LockPositionHint));
        }
    }

    private void DrawTransition(Rect screen, PhoneTheme theme)
    {
        var cover = navigation.MotionProgress;
        var height = screen.Height;
        var over = navigation.MotionOver;
        var under = navigation.MotionUnder;
        var overOffset = new Vector2(0f, (1f - cover) * height);
        var underDim = cover * TransitionTiming.ShellDimMax;
        LayerPainter underPaint =
            under is null ? target => PaintHome(target, theme) : target => PaintApp(target, theme, under);
        LayerPainter overPaint = target => PaintApp(target, theme, over);
        var underLayer =
            new SceneCompositor.Layer(under?.Id ?? "home", Vector2.Zero, underDim, underPaint, default, true);
        var overLayer = new SceneCompositor.Layer(over.Id, overOffset, 0f, overPaint, default, true);
        SceneCompositor.Composite(screen, underLayer, overLayer);
    }

    private void PaintHome(Rect screen, PhoneTheme theme)
    {
        DeviceChrome.DrawWallpaper(screen, theme);
        DeviceChrome.DrawHomeScrim(screen, theme);
        home.Draw(ContentRect(screen, theme), theme, navigation);
    }

    private void PaintApp(Rect screen, PhoneTheme theme, IPhoneApp app)
    {
        var content = themes.Current;
        if (!app.WantsTransparentScreen)
        {
            DeviceChrome.FillScreen(screen, theme, content.AppBackground);
        }

        app.Draw(new PhoneContext(ContentRect(screen, theme), content, navigation));
    }

    private static Rect ContentRect(Rect screen, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var min = new Vector2(screen.Min.X + theme.SidePadding * scale, screen.Min.Y + theme.TopZoneHeight * scale);
        var max = new Vector2(screen.Max.X - theme.SidePadding * scale, screen.Max.Y - theme.BottomZoneHeight * scale);
        return new Rect(min, max);
    }

    private void DrawHomeIndicator(Rect screen, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = 112f * scale;
        var height = 5f * scale;
        var center = new Vector2(screen.Center.X, screen.Max.Y - 14f * scale);
        var min = new Vector2(center.X - width * 0.5f, center.Y - height * 0.5f);
        var max = new Vector2(center.X + width * 0.5f, center.Y + height * 0.5f);
        var hitMin = new Vector2(min.X - 24f * scale, min.Y - 16f * scale);
        var hitMax = new Vector2(max.X + 24f * scale, max.Y + 16f * scale);
        var actionable = !navigation.AtHome && !navigation.IsTransitioning && ImGui.IsMouseHoveringRect(hitMin, hitMax);
        var color = actionable ? theme.TextStrong : Palette.WithAlpha(theme.TextStrong, 0.55f);
        ImGui.GetWindowDrawList().AddRectFilled(min, max, ImGui.GetColorU32(color), height * 0.5f);
        if (!actionable)
        {
            return;
        }

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            navigation.GoHome();
        }
    }

    public void Dispose()
    {
        for (var index = 0; index < apps.Count; index++)
        {
            apps[index].Dispose();
        }
    }
}
