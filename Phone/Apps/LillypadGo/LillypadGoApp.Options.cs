using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using VideoSyncPrototype.Phone.Apps.Games.Framework;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal sealed partial class LillypadGoApp
{
    private const float MinBattleEffectScale = 0.75f;
    private const float DefaultBattleEffectScale = 1.25f;
    private const float MaxBattleEffectScale = 2f;

    private void DrawOptions(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        LgUi.Header(content, theme, Accent, "Options", "Tune Lillypad Go behavior and visuals.", scale);

        var trackingMin = new Vector2(content.Min.X + 14f * scale, content.Min.Y + 68f * scale);
        var trackingMax = new Vector2(content.Max.X - 14f * scale, trackingMin.Y + 86f * scale);
        LgUi.Card(drawList, trackingMin, trackingMax, 14f * scale, scale);

        Typography.Draw(new Vector2(trackingMin.X + 14f * scale, trackingMin.Y + 12f * scale), "Background tracking",
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(trackingMin.X + 14f * scale, trackingMin.Y + 34f * scale),
            "Find wild encounters while the phone or app is closed.", theme.TextMuted, TextStyles.Caption1);
        var tracking = State.BackgroundTrackingEnabled;
        var checkboxRect = new Rect(new Vector2(trackingMin.X + 14f * scale, trackingMax.Y - 34f * scale),
            new Vector2(trackingMax.X - 14f * scale, trackingMax.Y - 10f * scale));
        if (DrawCheckboxRow(checkboxRect, theme, scale, ref tracking, "Run Lillypad Go in the background"))
        {
            State.BackgroundTrackingEnabled = tracking;
            State.Save();
        }

        var cardMin = new Vector2(content.Min.X + 14f * scale, trackingMax.Y + 12f * scale);
        var cardMax = new Vector2(content.Max.X - 14f * scale, cardMin.Y + 138f * scale);
        LgUi.Card(drawList, cardMin, cardMax, 14f * scale, scale);

        Typography.Draw(new Vector2(cardMin.X + 14f * scale, cardMin.Y + 12f * scale), "Battle effects",
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(cardMin.X + 14f * scale, cardMin.Y + 34f * scale),
            "Controls the size of attack particles and impact visuals.", theme.TextMuted, TextStyles.Caption1);

        var value = Math.Clamp(State.BattleEffectScale, MinBattleEffectScale, MaxBattleEffectScale);
        var sliderRect = new Rect(new Vector2(cardMin.X + 14f * scale, cardMin.Y + 68f * scale),
            new Vector2(cardMax.X - 14f * scale, cardMin.Y + 98f * scale));
        if (DrawEffectScaleSlider(sliderRect, theme, scale, ref value))
        {
            State.BattleEffectScale = value;
        }

        if (effectScaleSliderActive && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            effectScaleSliderActive = false;
            State.Save();
        }

        var valueText = $"{MathF.Round(State.BattleEffectScale * 100f)}%";
        var valueSize = Typography.Measure(valueText, TextStyles.Caption1);
        Typography.Draw(new Vector2(cardMax.X - 14f * scale - valueSize.X, cardMin.Y + 12f * scale), valueText,
            Accent, TextStyles.Caption1);

        var resetRect = new Rect(new Vector2(cardMin.X + 14f * scale, cardMax.Y - 32f * scale),
            new Vector2(cardMin.X + 118f * scale, cardMax.Y - 8f * scale));
        if (LgUi.Button(resetRect, "Reset", GamePalette.Cell, theme,
                MathF.Abs(State.BattleEffectScale - DefaultBattleEffectScale) > 0.001f))
        {
            State.BattleEffectScale = DefaultBattleEffectScale;
            State.Save();
        }

        DrawNavigation(content, theme, scale);
    }

    private bool DrawCheckboxRow(Rect rect, PhoneTheme theme, float scale, ref bool value, string label)
    {
        var drawList = ImGui.GetWindowDrawList();
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        var boxSize = 22f * scale;
        var boxMin = new Vector2(rect.Min.X, rect.Center.Y - boxSize * 0.5f);
        var boxMax = boxMin + new Vector2(boxSize, boxSize);
        var radius = 6f * scale;
        var fill = value ? Accent : GamePalette.CellSunken;

        Squircle.FillVerticalGradient(drawList, boxMin, boxMax, radius,
            ImGui.GetColorU32(GamePalette.Lighten(fill, hovered ? 0.16f : 0.08f)),
            ImGui.GetColorU32(GamePalette.Darken(fill, hovered ? 0.08f : 0.14f)));
        Squircle.Stroke(drawList, boxMin, boxMax, radius,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, hovered ? 0.22f : 0.11f)), 1f * scale);

        if (value)
        {
            var ink = GamePalette.InkOn(Accent);
            drawList.AddLine(new Vector2(boxMin.X + 5f * scale, boxMin.Y + 12f * scale),
                new Vector2(boxMin.X + 9f * scale, boxMin.Y + 16f * scale), ImGui.GetColorU32(ink), 2f * scale);
            drawList.AddLine(new Vector2(boxMin.X + 9f * scale, boxMin.Y + 16f * scale),
                new Vector2(boxMin.X + 17f * scale, boxMin.Y + 7f * scale), ImGui.GetColorU32(ink), 2f * scale);
        }

        Typography.Draw(new Vector2(boxMax.X + 10f * scale, rect.Center.Y - 8f * scale), label,
            hovered ? theme.TextStrong : theme.TextMuted, TextStyles.Subheadline);

        ImGui.SetCursorScreenPos(rect.Min);
        ImGui.InvisibleButton("##lillypad-go-background-tracking", rect.Max - rect.Min);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (!hovered || !ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            return false;
        }

        value = !value;
        return true;
    }

    private bool DrawEffectScaleSlider(Rect rect, PhoneTheme theme, float scale, ref float value)
    {
        var drawList = ImGui.GetWindowDrawList();
        var trackMin = new Vector2(rect.Min.X, rect.Center.Y - 4f * scale);
        var trackMax = new Vector2(rect.Max.X, rect.Center.Y + 4f * scale);
        var radius = 4f * scale;
        var t = Math.Clamp((value - MinBattleEffectScale) / (MaxBattleEffectScale - MinBattleEffectScale), 0f, 1f);
        var knob = new Vector2(trackMin.X + (trackMax.X - trackMin.X) * t, rect.Center.Y);

        Squircle.Fill(drawList, trackMin, trackMax, radius, ImGui.GetColorU32(GamePalette.CellSunken));
        Squircle.Fill(drawList, trackMin, new Vector2(knob.X, trackMax.Y), radius,
            ImGui.GetColorU32(Accent with { W = 0.92f }));
        drawList.AddCircleFilled(knob, 11f * scale, ImGui.GetColorU32(GamePalette.Lighten(Accent, 0.22f)));
        drawList.AddCircle(knob, 11f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.45f)), 24, 1f * scale);

        Typography.Draw(new Vector2(rect.Min.X, rect.Max.Y - 4f * scale), "75%", theme.TextMuted, TextStyles.Caption2);
        var maxLabel = "200%";
        var maxSize = Typography.Measure(maxLabel, TextStyles.Caption2);
        Typography.Draw(new Vector2(rect.Max.X - maxSize.X, rect.Max.Y - 4f * scale), maxLabel, theme.TextMuted,
            TextStyles.Caption2);

        ImGui.SetCursorScreenPos(rect.Min);
        ImGui.InvisibleButton("##battle-effect-scale", rect.Max - rect.Min);
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            effectScaleSliderActive = true;
        }

        if (!effectScaleSliderActive || !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            return false;
        }

        var mouseX = ImGui.GetMousePos().X;
        var nextT = Math.Clamp((mouseX - trackMin.X) / MathF.Max(1f, trackMax.X - trackMin.X), 0f, 1f);
        var next = MinBattleEffectScale + nextT * (MaxBattleEffectScale - MinBattleEffectScale);
        if (MathF.Abs(next - value) <= 0.001f)
        {
            return false;
        }

        value = next;
        return true;
    }
}
