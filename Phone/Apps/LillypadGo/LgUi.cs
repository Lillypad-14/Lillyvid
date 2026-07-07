using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using VideoSyncPrototype.Phone.Apps.Games.Framework;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Lillypad Go's shared UI kit. Every screen in the app composes from these building blocks so the
// look stays consistent and new screens are cheap to add. Prefer adding a component here over
// hand-drawing squircles in a view — that keeps the design language in one place.
//
// Conventions:
//   * All sizes are multiplied by ImGuiHelpers.GlobalScale by the CALLER (pass already-scaled rects),
//     except where a method takes an explicit `scale` for hairline widths.
//   * Accent colours come from the app (AppAccents.For / Elements.Color); components never hard-code
//     brand colour, they take it as a parameter.
//   * Click helpers honour `Interactive`; set it false to freeze input (e.g. during a transition).
internal static class LgUi
{
    // When false, Button/Segmented ignore hover and clicks. Used to disable input mid-transition.
    public static bool Interactive = true;

    // Formats a Poké Dollar amount, e.g. "₽1,240". The ₽ glyph is loaded via FontService's
    // Currency Symbols range.
    public static string Money(int amount) =>
        "₽" + amount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    // Draws a bundled item sprite centred at `center`, falling back to the FontAwesome glyph until
    // the texture streams in (or if it is missing).
    public static void ItemIcon(ImDrawListPtr dl, Vector2 center, float size, ItemDef item)
    {
        if (AssetTextures.TryGet($"items/{item.Id}.png", out var handle, out var aspect))
        {
            var w = aspect >= 1f ? size : size * aspect;
            var h = aspect >= 1f ? size / aspect : size;
            var half = new Vector2(w * 0.5f, h * 0.5f);
            dl.AddImage(handle, center - half, center + half, Vector2.Zero, Vector2.One,
                ImGui.GetColorU32(Vector4.One));
        }
        else
        {
            ProgressRing.CenterIcon(dl, center, item.Icon, ItemTint(item.Category), size * 0.9f);
        }
    }

    // The accent colour used for an item's card/button, by category.
    public static Vector4 ItemTint(ItemCategory category) => category switch
    {
        ItemCategory.Ball => new Vector4(0.90f, 0.36f, 0.34f, 1f),      // Poké Ball red
        ItemCategory.Potion => new Vector4(0.38f, 0.74f, 0.52f, 1f),    // restorative green
        ItemCategory.Revive => new Vector4(0.96f, 0.79f, 0.36f, 1f),    // revival gold
        ItemCategory.StatusHeal => new Vector4(0.46f, 0.68f, 0.92f, 1f),// remedy blue
        _ => new Vector4(0.7f, 0.7f, 0.7f, 1f),
    };

    // ---- Surfaces -------------------------------------------------------------------------------

    // The standard elevated surface: soft drop shadow, vertical gradient fill, hairline edge and a
    // top highlight. Callers may layer an accent stroke on top afterwards. `sunken` renders a flat,
    // recessed slab (for disabled/undiscovered states) with no shadow.
    public static void Card(ImDrawListPtr drawList, Vector2 min, Vector2 max, float radius, float scale,
        bool hovered = false, bool sunken = false)
    {
        var baseColor = sunken ? GamePalette.CellSunken : hovered ? GamePalette.CellHover : GamePalette.Cell;
        if (!sunken)
        {
            Elevation.Draw(drawList, min, max, radius, scale, 6f, 2f, 0.14f);
        }

        Squircle.FillVerticalGradient(drawList, min, max, radius,
            ImGui.GetColorU32(GamePalette.Lighten(baseColor, 0.05f)),
            ImGui.GetColorU32(GamePalette.Darken(baseColor, 0.12f)));
        Squircle.Stroke(drawList, min, max, radius,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, sunken ? 0.03f : 0.06f)), 1f * scale);
        var inset = MathF.Max(radius, 1f);
        drawList.AddLine(new Vector2(min.X + inset, min.Y + 1f * scale), new Vector2(max.X - inset, min.Y + 1f * scale),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, sunken ? 0.04f : 0.09f)), 1f * scale);
    }

    // A page's title bar: gradient depth, accent underline and a soft downward glow.
    public static void Header(Rect content, PhoneTheme theme, Vector4 accent, string title, string? subtitle,
        float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var height = (subtitle is null ? 38f : 58f) * scale;
        var max = new Vector2(content.Max.X, content.Min.Y + height);
        var top = ImGui.GetColorU32(GamePalette.Board with { W = 0.92f });
        var bottom = ImGui.GetColorU32(GamePalette.Board with { W = 0.55f });
        drawList.AddRectFilledMultiColor(content.Min, max, top, top, bottom, bottom);

        var lineY = max.Y;
        drawList.AddRectFilled(new Vector2(content.Min.X, lineY - 1.5f * scale), new Vector2(content.Max.X, lineY),
            ImGui.GetColorU32(accent with { W = 0.7f }));
        var glowTop = ImGui.GetColorU32(accent with { W = 0.16f });
        var glowClear = ImGui.GetColorU32(accent with { W = 0f });
        drawList.AddRectFilledMultiColor(new Vector2(content.Min.X, lineY),
            new Vector2(content.Max.X, lineY + 9f * scale), glowTop, glowTop, glowClear, glowClear);

        Typography.DrawCentered(new Vector2(content.Center.X, content.Min.Y + 20f * scale), title,
            theme.TextStrong, TextStyles.Title2);
        if (subtitle is not null)
        {
            Typography.DrawCentered(new Vector2(content.Center.X, content.Min.Y + 43f * scale), subtitle,
                theme.TextMuted, TextStyles.Caption1);
        }
    }

    // ---- Controls -------------------------------------------------------------------------------

    // Collapsible section row with a disclosure arrow and optional right-aligned summary.
    public static bool Disclosure(Rect rect, string title, string? trailing, bool expanded, Vector4 accent,
        PhoneTheme theme, float scale, bool emphasized = false)
    {
        var hovered = Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        var drawList = ImGui.GetWindowDrawList();
        Card(drawList, rect.Min, rect.Max, 9f * scale, scale, hovered, false);
        if (emphasized)
        {
            drawList.AddRectFilled(rect.Min, new Vector2(rect.Min.X + 4f * scale, rect.Max.Y),
                ImGui.GetColorU32(accent with { W = 0.86f }), 3f * scale);
        }

        Typography.DrawCentered(new Vector2(rect.Min.X + 17f * scale, rect.Center.Y), expanded ? "v" : ">",
            expanded ? accent : theme.TextMuted, TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(rect.Min.X + 31f * scale, rect.Center.Y - 8f * scale), title,
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        if (!string.IsNullOrEmpty(trailing))
        {
            var size = Typography.Measure(trailing, TextStyles.Caption1);
            Typography.Draw(new Vector2(rect.Max.X - size.X - 10f * scale, rect.Center.Y - 7f * scale), trailing,
                theme.TextMuted, TextStyles.Caption1);
        }

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        return hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }

    // Primary tap target: gradient fill, hover glow, top highlight, stable press feedback and an
    // optional second line. Disabled buttons render as a recessed slab. Returns true on release.
    public static bool Button(Rect r, string label, Vector4 fill, PhoneTheme theme, bool enabled,
        string? sub = null)
    {
        var drawList = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var hovered = enabled && Interactive && ImGui.IsMouseHoveringRect(r.Min, r.Max);
        var pressed = hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var center = r.Center;
        var half = (r.Max - r.Min) * 0.5f;
        var min = r.Min;
        var max = r.Max;
        var radius = MathF.Min(12f * scale, half.Y);

        Vector4 ink;
        if (!enabled)
        {
            Squircle.Fill(drawList, min, max, radius, ImGui.GetColorU32(GamePalette.CellSunken));
            Squircle.Stroke(drawList, min, max, radius, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.04f)), 1f * scale);
            ink = theme.TextMuted;
        }
        else
        {
            // Hover reads as a lift: a soft contained shadow plus a brighter edge, instead of a
            // colored glow blob spilling past the corners. Press cancels the lift and darkens.
            if (hovered && !pressed)
            {
                Elevation.Draw(drawList, min, max, radius, scale, 9f, 3f, 0.22f);
            }

            var fillTop = ImGui.GetColorU32(GamePalette.Lighten(fill, pressed ? 0.04f : hovered ? 0.15f : 0.12f));
            var fillBottom = ImGui.GetColorU32(GamePalette.Darken(fill, pressed ? 0.20f : hovered ? 0.08f : 0.12f));
            Squircle.FillVerticalGradient(drawList, min, max, radius, fillTop, fillBottom);
            Squircle.Stroke(drawList, min, max, radius,
                ImGui.GetColorU32(GamePalette.Lighten(fill, 0.35f) with { W = hovered ? 0.85f : 0.5f }), 1f * scale);
            drawList.AddLine(new Vector2(min.X + radius, min.Y + 1f * scale),
                new Vector2(max.X - radius, min.Y + 1f * scale),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, hovered ? 0.34f : 0.24f)), 1f * scale);
            ink = GamePalette.InkOn(fill);
        }

        if (sub is null)
        {
            Typography.DrawCentered(center, label, ink, TextStyles.Headline);
        }
        else
        {
            Typography.DrawCentered(new Vector2(center.X, center.Y - 7f * scale), label, ink, TextStyles.Subheadline);
            Typography.DrawCentered(new Vector2(center.X, center.Y + 9f * scale), sub, ink with { W = 0.8f },
                TextStyles.Caption2);
        }

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        return hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
    }

    // An inset segmented control (iOS-style) with a sliding accent indicator. `indicator` holds the
    // animated position between frames — pass a field by ref. Returns the newly clicked index, or -1.
    public static int Segmented(Rect bounds, string[] labels, int selected, Vector4 accent, PhoneTheme theme,
        float scale, ref float indicator)
    {
        var drawList = ImGui.GetWindowDrawList();
        var radius = MathF.Min(11f * scale, bounds.Height * 0.5f);
        Squircle.Fill(drawList, bounds.Min, bounds.Max, radius, ImGui.GetColorU32(GamePalette.CellSunken));
        Squircle.Stroke(drawList, bounds.Min, bounds.Max, radius, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.35f)),
            1f * scale);

        var inset = 3f * scale;
        var segWidth = (bounds.Width - inset * 2f) / labels.Length;
        if (indicator < 0f)
        {
            indicator = selected;
        }

        var dt = MathF.Min(ImGui.GetIO().DeltaTime, 0.1f);
        indicator += (selected - indicator) * MathF.Min(1f, dt * 16f);

        var pillMin = new Vector2(bounds.Min.X + inset + indicator * segWidth, bounds.Min.Y + inset);
        var pillMax = new Vector2(pillMin.X + segWidth, bounds.Max.Y - inset);
        var pillRadius = MathF.Min(9f * scale, (pillMax.Y - pillMin.Y) * 0.5f);
        Elevation.Draw(drawList, pillMin, pillMax, pillRadius, scale, 5f, 2f, 0.28f);
        Squircle.FillVerticalGradient(drawList, pillMin, pillMax, pillRadius,
            ImGui.GetColorU32(GamePalette.Lighten(accent, 0.16f)), ImGui.GetColorU32(GamePalette.Darken(accent, 0.08f)));
        drawList.AddLine(new Vector2(pillMin.X + pillRadius, pillMin.Y + 1f * scale),
            new Vector2(pillMax.X - pillRadius, pillMin.Y + 1f * scale),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.28f)), 1f * scale);

        var clicked = -1;
        for (var i = 0; i < labels.Length; i++)
        {
            var min = new Vector2(bounds.Min.X + inset + i * segWidth, bounds.Min.Y + inset);
            var max = new Vector2(min.X + segWidth, bounds.Max.Y - inset);
            var isSelected = i == selected;
            var hovered = Interactive && ImGui.IsMouseHoveringRect(min, max);
            var proximity = Math.Clamp(1f - MathF.Abs(indicator - i), 0f, 1f);
            var labelColor = isSelected ? GamePalette.InkOn(accent)
                : hovered ? theme.TextStrong
                : Vector4.Lerp(theme.TextMuted, GamePalette.InkOn(accent), proximity * 0.6f);
            Typography.DrawCentered((min + max) * 0.5f, labels[i], labelColor, TextStyles.SubheadlineEmphasized);
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    clicked = i;
                }
            }
        }

        return clicked;
    }

    // A styled single-line text field that matches the card language (recessed fill, hairline edge)
    // instead of stock ImGui chrome. Returns true when the user presses Enter.
    public static bool Input(Rect rect, string id, ref string text, int maxLength, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var radius = MathF.Min(10f * scale, rect.Height * 0.5f);
        var focused = ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        Squircle.Fill(drawList, rect.Min, rect.Max, radius, ImGui.GetColorU32(GamePalette.CellSunken));
        Squircle.Stroke(drawList, rect.Min, rect.Max, radius,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, focused ? 0.12f : 0.06f)), 1f * scale);

        var padding = 10f * scale;
        var framePadY = MathF.Max(1f, (rect.Height - ImGui.GetTextLineHeight()) * 0.5f);
        ImGui.SetCursorScreenPos(rect.Min);
        ImGui.SetNextItemWidth(rect.Width);
        bool submitted;
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(padding, framePadY)))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0f, 0f, 0f, 0f)))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0f)))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, new Vector4(0f, 0f, 0f, 0f)))
        using (ImRaii.PushColor(ImGuiCol.Text, theme.TextStrong))
        {
            submitted = ImGui.InputText(id, ref text, maxLength, ImGuiInputTextFlags.EnterReturnsTrue);
        }

        return submitted;
    }

    // ---- Feedback -------------------------------------------------------------------------------

    // A friendly placeholder for an empty list: a muted icon over a single line of text.
    public static void EmptyState(Vector2 center, FontAwesomeIcon icon, string message, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        ProgressRing.CenterIcon(drawList, new Vector2(center.X, center.Y - 16f * scale), icon,
            theme.TextMuted with { W = theme.TextMuted.W * 0.5f }, 34f * scale);
        Typography.DrawCentered(new Vector2(center.X, center.Y + 22f * scale), message, theme.TextMuted,
            TextStyles.Subheadline);
    }

    // ---- Bars -----------------------------------------------------------------------------------

    // A creature's HP bar: recessed track plus a gradient fill that shifts green→amber→red, with a
    // subtle gloss line on taller bars.
    public static void HpBar(ImDrawListPtr drawList, Vector2 min, Vector2 max, float fraction)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var radius = (max.Y - min.Y) * 0.5f;
        Squircle.Fill(drawList, min, max, radius, ImGui.GetColorU32(new Vector4(0.05f, 0.06f, 0.08f, 0.92f)));
        Squircle.Stroke(drawList, min, max, radius, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.4f)), 1f * scale);
        fraction = Math.Clamp(fraction, 0f, 1f);
        if (fraction <= 0f)
        {
            return;
        }

        var col = fraction > 0.5f ? new Vector4(0.36f, 0.84f, 0.46f, 1f)
            : fraction > 0.2f ? new Vector4(0.98f, 0.80f, 0.34f, 1f)
            : new Vector4(0.95f, 0.38f, 0.36f, 1f);
        var fillMax = new Vector2(min.X + (max.X - min.X) * fraction, max.Y);
        Squircle.FillVerticalGradient(drawList, min, fillMax, radius,
            ImGui.GetColorU32(GamePalette.Lighten(col, 0.22f)), ImGui.GetColorU32(GamePalette.Darken(col, 0.14f)));
        if (max.Y - min.Y >= 7f * scale && fillMax.X - min.X > radius * 2f)
        {
            var glossY = min.Y + (max.Y - min.Y) * 0.3f;
            drawList.AddLine(new Vector2(min.X + radius, glossY), new Vector2(fillMax.X - radius, glossY),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.22f)), 1f * scale);
        }
    }

    // A generic progress meter (XP, timers) in an arbitrary accent colour.
    public static void Meter(ImDrawListPtr drawList, Vector2 min, Vector2 max, float fraction, Vector4 color)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var radius = MathF.Max(1f, (max.Y - min.Y) * 0.5f);
        Squircle.Fill(drawList, min, max, radius, ImGui.GetColorU32(new Vector4(0.05f, 0.06f, 0.08f, 0.92f)));
        Squircle.Stroke(drawList, min, max, radius, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.35f)), 1f * scale);
        fraction = Math.Clamp(fraction, 0f, 1f);
        if (fraction <= 0f)
        {
            return;
        }

        var fillMax = new Vector2(min.X + (max.X - min.X) * fraction, max.Y);
        Squircle.FillVerticalGradient(drawList, min, fillMax, radius,
            ImGui.GetColorU32(GamePalette.Lighten(color, 0.24f)), ImGui.GetColorU32(GamePalette.Darken(color, 0.1f)));
    }

    // Passive vertical scroll indicator for custom clipped lists.
    public static void Scrollbar(Rect track, float offset, float maxOffset, float viewportFraction, Vector4 accent,
        float scale)
    {
        if (maxOffset <= 0f)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        Squircle.Fill(drawList, track.Min, track.Max, track.Width * 0.5f,
            ImGui.GetColorU32(GamePalette.CellSunken with { W = 0.72f }));
        var thumbHeight = MathF.Max(22f * scale, track.Height * Math.Clamp(viewportFraction, 0.08f, 1f));
        var travel = track.Height - thumbHeight;
        var progress = Math.Clamp(offset / maxOffset, 0f, 1f);
        var thumbMin = new Vector2(track.Min.X, track.Min.Y + travel * progress);
        var thumbMax = new Vector2(track.Max.X, thumbMin.Y + thumbHeight);
        Squircle.Fill(drawList, thumbMin, thumbMax, track.Width * 0.5f,
            ImGui.GetColorU32(accent with { W = 0.72f }));
    }

    // A small typed element tag (Fire, Ice, ...) or a custom label in the element's colour.
    public static void Chip(ImDrawListPtr drawList, Vector2 topLeft, Element element, float scale,
        string? label = null)
    {
        var text = label ?? Elements.Name(element);
        var size = Typography.Measure(text, TextStyles.Caption2);
        var min = topLeft;
        var max = topLeft + new Vector2(size.X + 14f * scale, 16f * scale);
        var radius = (max.Y - min.Y) * 0.5f;
        var color = Elements.Color(element);
        Squircle.FillVerticalGradient(drawList, min, max, radius,
            ImGui.GetColorU32(color with { W = 0.30f }), ImGui.GetColorU32(color with { W = 0.18f }));
        Squircle.Stroke(drawList, min, max, radius, ImGui.GetColorU32(color with { W = 0.6f }), 1f * scale);
        Typography.DrawCentered((min + max) * 0.5f, text, GamePalette.Lighten(color, 0.35f), TextStyles.Caption2);
    }

    public static float TypeChips(ImDrawListPtr drawList, Vector2 topLeft, Element primary, Element? secondary,
        float scale)
    {
        Chip(drawList, topLeft, primary, scale);
        var width = Typography.Measure(Elements.Name(primary), TextStyles.Caption2).X + 14f * scale;
        if (!secondary.HasValue)
        {
            return width;
        }

        var gap = 4f * scale;
        Chip(drawList, new Vector2(topLeft.X + width + gap, topLeft.Y), secondary.Value, scale);
        return width + gap + Typography.Measure(Elements.Name(secondary.Value), TextStyles.Caption2).X + 14f * scale;
    }
}
