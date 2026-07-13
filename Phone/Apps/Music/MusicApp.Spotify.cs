using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Apps;
using VideoSyncPrototype.Phone.Core.Localization;
using VideoSyncPrototype.Phone.Core.Spotify;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.Music;

// The Spotify remote, built on FantasyPlayer's SpotifyState (MIT). Spotify's Web API cannot stream
// audio to us — it only reports and drives playback on an already-running Spotify client — so this
// screen is a controller, not a player. Everything it shows comes from polling that client.
internal sealed partial class MusicApp
{
    private const string SpotifyDashboardUrl = "https://developer.spotify.com/dashboard";
    private static readonly Vector4 SpotifyGreen = new(0.11f, 0.73f, 0.33f, 1f);

    private string clientIdDraft = string.Empty;
    private bool focusClientId;

    private SpotifyController? Spotify => playback.Spotify;

    private void OpenSpotify()
    {
        clientIdDraft = Spotify?.ClientId ?? string.Empty;
        router.Push(View.Spotify);
    }

    // The Home entry point. Doubles as a live status line, so the user can see at a glance whether
    // Spotify is connected and what it is playing without opening the screen.
    private void DrawSpotifyCard(float scale)
    {
        var spotify = Spotify;
        if (spotify is null)
        {
            return;
        }

        var height = 58f * scale;
        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var min = origin;
        var max = new Vector2(origin.X + width, origin.Y + height);
        var drawList = ImGui.GetWindowDrawList();
        var hovered = UiInteract.Hover(min, max);
        Squircle.Fill(drawList, min, max, 12f * scale,
            ImGui.GetColorU32(hovered ? Palette.WithAlpha(SpotifyGreen, 0.22f) : Palette.WithAlpha(SpotifyGreen, 0.13f)));
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var iconCenter = new Vector2(min.X + 30f * scale, min.Y + height * 0.5f);
        AppSkin.Icon(iconCenter, FontAwesomeIcon.Music.ToIconString(), SpotifyGreen, 1.5f);

        var track = spotify.Track;
        var subtitle = spotify.Status switch
        {
            SpotifyStatus.Ready when track is not null => $"{track.Title} · {track.Artist}",
            SpotifyStatus.NoDevice => Loc.T(L.Music.SpotifyNoDeviceTitle),
            SpotifyStatus.NotPremium => Loc.T(L.Music.SpotifyPremiumTitle),
            SpotifyStatus.Connecting => Loc.T(L.Music.SpotifyConnecting),
            _ => Loc.T(L.Music.SpotifyHomeSub),
        };

        var textLeft = min.X + 54f * scale;
        var textWidth = max.X - textLeft - 16f * scale;
        Typography.Draw(new Vector2(textLeft, min.Y + 10f * scale), Loc.T(L.Music.Spotify), ui.TitleInk,
            TextStyles.BodyEmphasized);
        Typography.Draw(new Vector2(textLeft, min.Y + 32f * scale),
            Typography.FitText(subtitle, textWidth, TextStyles.Caption1),
            spotify.Status == SpotifyStatus.Ready ? SpotifyGreen : ui.MutedInk, TextStyles.Caption1);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            OpenSpotify();
        }
    }

    private void DrawSpotify(in PhoneContext context)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var content = context.Content;
        DrawTopBar(context, Loc.T(L.Music.Spotify), GoToHome);
        var body = new Rect(new Vector2(content.Min.X, content.Min.Y + TopBarHeight * scale),
            new Vector2(content.Max.X, BodyBottom(content, scale)));

        var spotify = Spotify;
        if (spotify is null)
        {
            return;
        }

        switch (spotify.Status)
        {
            case SpotifyStatus.NeedsClientId:
                DrawSpotifySetup(body, scale, spotify);
                break;
            case SpotifyStatus.Connecting:
                LoadingPulse.Draw(new Vector2(body.Center.X, body.Center.Y - 14f * scale), 13f * scale, SpotifyGreen,
                    ui.MutedInk, Loc.T(L.Music.SpotifyConnecting));
                break;
            case SpotifyStatus.NeedsLogin:
                DrawSpotifyMessage(body, scale, FontAwesomeIcon.Music, Loc.T(L.Music.SpotifyConnect),
                    Loc.T(L.Music.SpotifyHomeSub), Loc.T(L.Music.SpotifyConnect), spotify.Login);
                break;
            case SpotifyStatus.NotPremium:
                DrawSpotifyMessage(body, scale, FontAwesomeIcon.ExclamationTriangle,
                    Loc.T(L.Music.SpotifyPremiumTitle), Loc.T(L.Music.SpotifyPremiumBody),
                    Loc.T(L.Music.SpotifyDisconnect), spotify.Logout);
                break;
            case SpotifyStatus.NoDevice:
                DrawSpotifyMessage(body, scale, FontAwesomeIcon.Desktop, Loc.T(L.Music.SpotifyNoDeviceTitle),
                    Loc.T(L.Music.SpotifyNoDeviceBody), Loc.T(L.Music.SpotifyDisconnect), spotify.Logout);
                break;
            default:
                DrawSpotifyNowPlaying(body, scale, spotify);
                break;
        }
    }

    // First run: Spotify's policy now requires every user to register their own app, so there is no
    // shared client id we can ship. Explain it, deep-link the dashboard, and take the id.
    private void DrawSpotifySetup(Rect body, float scale, SpotifyController spotify)
    {
        var left = body.Min.X + 22f * scale;
        var width = body.Width - 44f * scale;
        var top = body.Min.Y + 18f * scale;

        Typography.Draw(new Vector2(left, top), Loc.T(L.Music.SpotifySetupTitle), ui.TitleInk, TextStyles.Title3);
        var bodyTop = top + 30f * scale;
        Typography.DrawWrappedCentered(ImGui.GetWindowDrawList(),
            new Vector2(body.Center.X, bodyTop + Typography.MeasureWrappedBlock(Loc.T(L.Music.SpotifySetupBody), TextStyles.Subheadline, width).Y * 0.5f),
            Loc.T(L.Music.SpotifySetupBody), ui.MutedInk, TextStyles.Subheadline, width);

        var fieldTop = bodyTop +
                       Typography.MeasureWrappedBlock(Loc.T(L.Music.SpotifySetupBody), TextStyles.Subheadline, width).Y +
                       18f * scale;
        var field = new Rect(new Vector2(left, fieldTop), new Vector2(left + width, fieldTop + 40f * scale));
        if (focusClientId)
        {
            focusClientId = false;
            ImGui.SetKeyboardFocusHere();
        }

        var submitted = SearchField.DrawSubmit(field, "##spotifyClientId", Loc.T(L.Music.SpotifyClientIdHint),
            ref clientIdDraft, SearchFieldSurface, SearchFieldHint, SearchFieldInk, 64, 10f);

        var buttonTop = field.Max.Y + 14f * scale;
        var half = (width - 10f * scale) * 0.5f;
        var dashboard = new Rect(new Vector2(left, buttonTop), new Vector2(left + half, buttonTop + 38f * scale));
        if (SpotifyButton(dashboard, Loc.T(L.Music.SpotifyOpenDashboard), scale, false))
        {
            OpenUrl(SpotifyDashboardUrl);
        }

        var connect = new Rect(new Vector2(dashboard.Max.X + 10f * scale, buttonTop),
            new Vector2(left + width, buttonTop + 38f * scale));
        var valid = !string.IsNullOrWhiteSpace(clientIdDraft);
        if ((SpotifyButton(connect, Loc.T(L.Music.SpotifyConnect), scale, true, valid) || submitted) && valid)
        {
            spotify.SetClientId(clientIdDraft);
            spotify.Login();
        }
    }

    private void DrawSpotifyMessage(Rect body, float scale, FontAwesomeIcon icon, string title, string message,
        string action, Action onAction)
    {
        var center = new Vector2(body.Center.X, body.Center.Y - 40f * scale);
        AppSkin.Icon(center, icon.ToIconString(), SpotifyGreen, 2.0f);
        Typography.DrawCentered(new Vector2(center.X, center.Y + 34f * scale), title, ui.TitleInk, TextStyles.Title3);
        Typography.DrawWrappedCentered(new Vector2(center.X, center.Y + 62f * scale), message, ui.MutedInk,
            TextStyles.Subheadline, body.Width - 60f * scale);

        var buttonWidth = 180f * scale;
        var buttonTop = center.Y + 100f * scale;
        var button = new Rect(new Vector2(center.X - buttonWidth * 0.5f, buttonTop),
            new Vector2(center.X + buttonWidth * 0.5f, buttonTop + 38f * scale));
        if (SpotifyButton(button, action, scale, true))
        {
            onAction();
        }
    }

    private void DrawSpotifyNowPlaying(Rect body, float scale, SpotifyController spotify)
    {
        var track = spotify.Track;
        if (track is null)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();

        // Album art
        var artSize = MathF.Min(body.Width - 96f * scale, 190f * scale);
        var artMin = new Vector2(body.Center.X - artSize * 0.5f, body.Min.Y + 14f * scale);
        var artMax = artMin + new Vector2(artSize, artSize);
        DrawCover(drawList, artMin, artMax, track.ArtUrl, track.Title, 12f * scale);

        var textWidth = body.Width - 48f * scale;
        var titleY = artMax.Y + 16f * scale;
        Typography.DrawCentered(new Vector2(body.Center.X, titleY),
            Typography.FitText(track.Title, textWidth, TextStyles.Title3), ui.TitleInk, TextStyles.Title3);
        Typography.DrawCentered(new Vector2(body.Center.X, titleY + 24f * scale),
            Typography.FitText(track.Artist, textWidth, TextStyles.Subheadline), ui.MutedInk, TextStyles.Subheadline);

        // Progress. Spotify exposes no seek in FantasyPlayer's state, so this is read-only.
        var progressY = titleY + 52f * scale;
        var trackRect = new Rect(new Vector2(body.Min.X + 24f * scale, progressY - 2.5f * scale),
            new Vector2(body.Max.X - 24f * scale, progressY + 2.5f * scale));
        var fraction = track.DurationMs <= 0 ? 0f : Math.Clamp(spotify.ProgressMs / (float)track.DurationMs, 0f, 1f);
        Squircle.Fill(drawList, trackRect.Min, trackRect.Max, trackRect.Height * 0.5f,
            ImGui.GetColorU32(Palette.WithAlpha(ui.TitleInk, 0.18f)));
        var filled = new Vector2(trackRect.Min.X + trackRect.Width * fraction, trackRect.Max.Y);
        if (fraction > 0.001f)
        {
            Squircle.Fill(drawList, trackRect.Min, filled, trackRect.Height * 0.5f, ImGui.GetColorU32(SpotifyGreen));
        }

        Typography.Draw(new Vector2(trackRect.Min.X, progressY + 8f * scale), FormatTime(spotify.ProgressMs / 1000),
            ui.MutedInk, TextStyles.Caption2);
        var remaining = FormatTime(track.DurationMs / 1000);
        Typography.Draw(
            new Vector2(trackRect.Max.X - Typography.Measure(remaining, TextStyles.Caption2).X, progressY + 8f * scale),
            remaining, ui.MutedInk, TextStyles.Caption2);

        // Transport
        var controlY = progressY + 44f * scale;
        var centerX = body.Center.X;
        if (TransportButton.Draw(new Vector2(centerX - 58f * scale, controlY), 17f * scale, TransportAction.Previous,
                SpotifyGreen, ui.TitleInk, 1f, true))
        {
            spotify.Previous();
        }

        if (TransportButton.Draw(new Vector2(centerX, controlY), 22f * scale,
                track.IsPlaying ? TransportAction.Pause : TransportAction.Play, SpotifyGreen, ui.TitleInk, 1f, true))
        {
            // Taking over from local audio, so the two never sound at once.
            playback.StopLocal();
            spotify.TogglePlayPause();
        }

        if (TransportButton.Draw(new Vector2(centerX + 58f * scale, controlY), 17f * scale, TransportAction.Next,
                SpotifyGreen, ui.TitleInk, 1f, true))
        {
            spotify.Next();
        }

        // Shuffle / repeat
        var toggleY = controlY + 44f * scale;
        if (IconToggle(new Vector2(centerX - 40f * scale, toggleY), FontAwesomeIcon.Random, track.Shuffle, scale))
        {
            spotify.ToggleShuffle();
        }

        var repeatOn = !string.Equals(track.RepeatState, "off", StringComparison.Ordinal);
        var repeatIcon = string.Equals(track.RepeatState, "track", StringComparison.Ordinal)
            ? FontAwesomeIcon.Redo
            : FontAwesomeIcon.Sync;
        if (IconToggle(new Vector2(centerX + 40f * scale, toggleY), repeatIcon, repeatOn, scale))
        {
            spotify.CycleRepeat();
        }

        // Device volume
        var volumeY = toggleY + 36f * scale;
        var volumeRect = new Rect(new Vector2(body.Min.X + 34f * scale, volumeY - 2.5f * scale),
            new Vector2(body.Max.X - 34f * scale, volumeY + 2.5f * scale));
        var next = Scrubber.Draw(volumeRect, track.DeviceVolume / 100f, SpotifyGreen,
            Palette.WithAlpha(ui.TitleInk, 0.18f), 1f);
        if (MathF.Abs(next - track.DeviceVolume / 100f) > 0.001f)
        {
            spotify.SetVolume(next);
        }

        if (!string.IsNullOrEmpty(track.DeviceName))
        {
            Typography.DrawCentered(new Vector2(body.Center.X, volumeY + 20f * scale),
                Typography.FitText(string.Format(Loc.T(L.Music.SpotifyPlayingOn), track.DeviceName),
                    body.Width - 40f * scale, TextStyles.Caption1), SpotifyGreen, TextStyles.Caption1);
        }
    }

    private bool IconToggle(Vector2 center, FontAwesomeIcon icon, bool on, float scale)
    {
        var radius = 15f * scale;
        var min = center - new Vector2(radius, radius);
        var max = center + new Vector2(radius, radius);
        var hovered = UiInteract.Hover(min, max);
        if (hovered)
        {
            Squircle.Fill(ImGui.GetWindowDrawList(), min, max, radius, ImGui.GetColorU32(ui.HoverTint));
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        AppSkin.Icon(center, icon.ToIconString(), on ? SpotifyGreen : ui.MutedInk, 0.95f);
        return hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }

    private bool SpotifyButton(Rect rect, string label, float scale, bool filled, bool enabled = true)
    {
        var drawList = ImGui.GetWindowDrawList();
        var hovered = enabled && UiInteract.Hover(rect.Min, rect.Max);
        var rounding = rect.Height * 0.5f;
        var fill = filled
            ? enabled ? hovered ? Palette.Lighten(SpotifyGreen, 0.12f) : SpotifyGreen : Palette.WithAlpha(SpotifyGreen, 0.35f)
            : hovered
                ? Palette.WithAlpha(ui.TitleInk, 0.16f)
                : ui.FieldSurface;
        Squircle.Fill(drawList, rect.Min, rect.Max, rounding, ImGui.GetColorU32(fill));
        var ink = filled ? new Vector4(1f, 1f, 1f, enabled ? 1f : 0.6f) : ui.TitleInk;
        Typography.DrawCentered(rect.Center, Typography.FitText(label, rect.Width - 20f * scale, TextStyles.Callout),
            ink, TextStyles.Callout);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        return hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "Could not open {Url}", url);
        }
    }
}
