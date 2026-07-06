using System.Numerics;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Apps;
using VideoSyncPrototype.Phone.Core.Onboarding;
using VideoSyncPrototype.Phone.Core.Theme;
using Dalamud.Bindings.ImGui;

namespace VideoSyncPrototype.Phone.Windows.Components;

internal static class SceneChrome
{
    public static Rect ScreenFrom(Rect content, PhoneTheme theme, float scale)
    {
        var min = new Vector2(content.Min.X - theme.SidePadding * scale, content.Min.Y - theme.TopZoneHeight * scale);
        var max = new Vector2(content.Max.X + theme.SidePadding * scale,
            content.Max.Y + theme.BottomZoneHeight * scale);
        return new Rect(min, max);
    }

    public static void BackChevron(Rect content, INavigator navigation, Vector4 ink, float scale)
    {
        var rowCenterY = content.Min.Y + 20f * scale;
        var hitMin = new Vector2(content.Min.X, content.Min.Y);
        var hitMax = new Vector2(content.Min.X + 46f * scale, content.Min.Y + 40f * scale);
        UiAnchors.Report("chrome.back", new Rect(hitMin, hitMax));
        var hovered = ImGui.IsMouseHoveringRect(hitMin, hitMax);
        var center = new Vector2(content.Min.X + 15f * scale, rowCenterY);
        if (BackButton.Draw("chrome.back", center, 15f * scale, ink, hovered, scale, shadow: true))
        {
            navigation.Back();
        }
    }
}
