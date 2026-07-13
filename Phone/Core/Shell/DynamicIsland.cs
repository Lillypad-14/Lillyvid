using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using VideoSyncPrototype.Phone.Core.Analytics;
using VideoSyncPrototype.Phone.Core.Animation;
using VideoSyncPrototype.Phone.Core.Apps;
using VideoSyncPrototype.Phone.Core.Playback;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Core.Shell;

// The now-playing pill that grows out of the status-bar island, ported from Aetherphone
// (AGPL-3.0-or-later). It keeps playback visible and controllable after you leave the Music app:
// compact it shows an art disc + equalizer; on hover it expands into title/subtitle, transport
// buttons and a volume scrubber. Clicking it opens Music.
//
// Aetherphone's island also drives in-call state (CallHub). Telephony is not part of this fork —
// it needs Aetherphone's backend — so only the music activity is reproduced here. The expansion
// is suppressed while Music itself is the foreground app, since the app already shows a mini
// player and Now Playing sheet.
internal sealed class DynamicIsland
{
    private const ImGuiWindowFlags IslandFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                                                 ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs;

    private const float PresenceSmoothTime = 0.14f;
    private const float ExpandSmoothTime = 0.16f;
    private const float CompactPadX = 22f;
    private const float CompactPadY = 5f;
    private const float MusicExpandedHeight = 120f;
    private const float MusicExpandedHalfWidth = 142f;
    private const float ControlThreshold = 0.6f;

    private static readonly Vector4 MusicAccent = AppAccents.For("music");
    private static readonly Vector4 Ink = new(0.98f, 0.98f, 0.99f, 1f);

    private readonly PlaybackHub playback;
    private Spring presence;
    private Spring expand;
    private float clock;
    private Rect lastBounds;

    public DynamicIsland(PlaybackHub playback)
    {
        this.playback = playback;
    }

    // True while the pill is on screen and under the cursor, so the shell can stop the click from
    // falling through to whatever is behind it (home screen icons, the foreground app).
    public bool CapturesPointer(Rect screen)
    {
        if (presence.Value < 0.05f)
        {
            return false;
        }

        return ImGui.IsMouseHoveringRect(lastBounds.Min, lastBounds.Max);
    }

    public void Draw(Rect screen, PhoneTheme theme, INavigator navigation, string? foregroundAppId)
    {
        var active = playback.IsActive;
        var delta = MathF.Min(ImGui.GetIO().DeltaTime, TransitionTiming.MaxFrameSeconds);
        clock += delta;
        presence.Step(active ? 1f : 0f, PresenceSmoothTime, delta);
        var presenceValue = Math.Clamp(presence.Value, 0f, 1f);
        if (!active && presenceValue < 0.02f)
        {
            expand.SnapTo(0f);
            lastBounds = StatusBar.BaseIsland(screen);
            return;
        }

        ImGui.SetCursorScreenPos(screen.Min);
        using (ImRaii.Child("##dynamicIsland", screen.Size, false, IslandFlags))
        {
            DrawContent(screen, theme, navigation, presenceValue, delta, foregroundAppId);
        }
    }

    private void DrawContent(Rect screen, PhoneTheme theme, INavigator navigation, float presenceValue, float delta,
        string? foregroundAppId)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var rest = StatusBar.BaseIsland(screen);
        var compact = Expand(rest, CompactPadX * scale, CompactPadY * scale);
        var expanded = ExpandedBounds(screen, rest, scale);
        var morphed = LerpRect(rest, compact, presenceValue);
        var hoverBounds = LerpRect(morphed, expanded, Easing.SmoothStep(Math.Clamp(expand.Value, 0f, 1f)));
        var hovered = ImGui.IsMouseHoveringRect(hoverBounds.Min, hoverBounds.Max);
        var suppressExpand = string.Equals(foregroundAppId, "music", StringComparison.Ordinal);
        expand.Step(hovered && presenceValue > 0.6f && !suppressExpand ? 1f : 0f, ExpandSmoothTime, delta);
        var expandEased = Easing.SmoothStep(Math.Clamp(expand.Value, 0f, 1f));
        var bounds = LerpRect(morphed, expanded, expandEased);
        lastBounds = bounds;
        var compactAlpha = Math.Clamp(presenceValue * 1.6f - 0.6f, 0f, 1f) * (1f - expandEased);
        var drawList = ImGui.GetWindowDrawList();
        var rounding = float.Lerp(bounds.Height * 0.5f, 28f * scale, expandEased);
        if (expandEased > 0.02f)
        {
            Elevation.Draw(drawList, bounds.Min, bounds.Max, rounding, scale, 5f + 6f * expandEased, 3f,
                0.24f * expandEased);
        }

        drawList.AddRectFilled(bounds.Min, bounds.Max, ImGui.GetColorU32(theme.BezelOuter), rounding);
        drawList.AddRect(bounds.Min, bounds.Max,
            ImGui.GetColorU32(Palette.WithAlpha(MusicAccent, (0.16f + 0.44f * expandEased) * presenceValue)), rounding,
            ImDrawFlags.RoundCornersAll, 1.5f * scale);

        DrawMusicCompact(drawList, bounds, scale, compactAlpha);
        var consumed = DrawMusicExpanded(drawList, bounds, scale, theme, expandEased);
        if (consumed || !hovered)
        {
            return;
        }

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            navigation.Open("music", AppOpenSource.Island);
        }
    }

    private void DrawMusicCompact(ImDrawListPtr drawList, Rect bounds, float scale, float alpha)
    {
        if (alpha <= 0.01f)
        {
            return;
        }

        var discRadius = bounds.Height * 0.34f;
        var discCenter = new Vector2(bounds.Min.X + 9f * scale + discRadius, bounds.Center.Y);
        ArtGradient.DrawDisc(drawList, discCenter, discRadius, ArtGradient.FromName(playback.Title), alpha);
        var eqCenter = new Vector2(bounds.Max.X - 13f * scale, bounds.Center.Y);
        Equalizer.Draw(drawList, eqCenter, scale, bounds.Height * 0.5f, clock, MusicAccent, alpha, playback.IsPlaying);
    }

    private bool DrawMusicExpanded(ImDrawListPtr drawList, Rect bounds, float scale, PhoneTheme theme, float alpha)
    {
        if (alpha <= 0.05f)
        {
            return false;
        }

        var left = bounds.Min.X;
        var top = bounds.Min.Y;
        var centerX = bounds.Center.X;
        var discRadius = 19f * scale;
        var discCenter = new Vector2(left + 18f * scale + discRadius, top + 30f * scale);
        ArtGradient.DrawDisc(drawList, discCenter, discRadius, ArtGradient.FromName(playback.Title), alpha);
        var textLeft = discCenter.X + discRadius + 12f * scale;
        Typography.Draw(new Vector2(textLeft, top + 18f * scale),
            Typography.FitText(playback.Title, bounds.Max.X - 16f * scale - textLeft, 1.0f, FontWeight.SemiBold),
            Palette.WithAlpha(theme.TextStrong, alpha), 1.0f, FontWeight.SemiBold);
        Typography.Draw(new Vector2(textLeft, top + 40f * scale),
            Typography.FitText(playback.Subtitle, bounds.Max.X - 16f * scale - textLeft, 0.8f, FontWeight.Regular),
            Palette.WithAlpha(MusicAccent, 0.9f * alpha), 0.8f);

        var active = alpha > ControlThreshold;
        var controlY = top + 66f * scale;
        var consumed = false;
        if (playback.HasQueue)
        {
            if (TransportButton.Draw(new Vector2(centerX - 46f * scale, controlY), 16f * scale,
                    TransportAction.Previous, MusicAccent, Ink, alpha, active))
            {
                playback.Previous();
                consumed = true;
            }

            if (TransportButton.Draw(new Vector2(centerX + 46f * scale, controlY), 16f * scale, TransportAction.Next,
                    MusicAccent, Ink, alpha, active))
            {
                playback.Next();
                consumed = true;
            }
        }

        if (TransportButton.Draw(new Vector2(centerX, controlY), 18f * scale,
                playback.IsPlaying ? TransportAction.Pause : TransportAction.Play, MusicAccent, Ink, alpha, active))
        {
            playback.TogglePlayPause();
            consumed = true;
        }

        if (active)
        {
            var trackY = top + 99f * scale;
            var track = new Rect(new Vector2(left + 22f * scale, trackY - 2.5f * scale),
                new Vector2(bounds.Max.X - 22f * scale, trackY + 2.5f * scale));
            playback.Volume = Scrubber.Draw(track, playback.Volume, MusicAccent,
                Palette.WithAlpha(theme.TextStrong, 0.18f), alpha);
            if (Scrubber.IsHovered(track))
            {
                consumed = true;
            }
        }

        return consumed;
    }

    private static Rect ExpandedBounds(Rect screen, Rect rest, float scale)
    {
        var halfWidth = MathF.Min(screen.Width * 0.5f - 14f * scale, MusicExpandedHalfWidth * scale);
        var height = MusicExpandedHeight * scale;
        var centerX = screen.Center.X;
        var top = rest.Min.Y - 2f * scale;
        return new Rect(new Vector2(centerX - halfWidth, top), new Vector2(centerX + halfWidth, top + height));
    }

    private static Rect Expand(Rect rect, float padX, float padY) =>
        new(rect.Min - new Vector2(padX, padY), rect.Max + new Vector2(padX, padY));

    private static Rect LerpRect(Rect from, Rect to, float amount) =>
        new(Vector2.Lerp(from.Min, to.Min, amount), Vector2.Lerp(from.Max, to.Max, amount));
}
