using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace VideoSyncPrototype.Windows;

/// <summary>
/// "Screening room" look for the plugin windows: a darkened theater rendered over
/// the user's Dalamud theme — warm marquee gold for trim and primary actions, a
/// cool screen-glow cyan for anything that's live on the in-world screen, and a
/// marquee tick beside every section title.
/// </summary>
internal static class UiTheme
{
    // Marquee gold — theater trim and every primary action.
    public static readonly Vector4 Accent = new(0.85f, 0.66f, 0.34f, 1f);
    public static readonly Vector4 AccentHovered = new(0.93f, 0.76f, 0.45f, 1f);
    public static readonly Vector4 AccentActive = new(0.72f, 0.54f, 0.26f, 1f);
    public static readonly Vector4 InkOnAccent = new(0.12f, 0.09f, 0.04f, 1f);
    public static readonly Vector4 Danger = new(0.78f, 0.32f, 0.30f, 1f);
    public static readonly Vector4 DangerHovered = new(0.86f, 0.40f, 0.37f, 1f);
    public static readonly Vector4 DangerActive = new(0.64f, 0.24f, 0.22f, 1f);

    // Cool screen glow — reserved for "this is live on the screen right now".
    public static readonly Vector4 Live = new(0.44f, 0.80f, 0.86f, 1f);
    public static readonly Vector4 Idle = new(0.52f, 0.50f, 0.47f, 1f);

    // Warm muted ink for secondary copy, so hints recede without going cold gray.
    public static readonly Vector4 Muted = new(0.62f, 0.58f, 0.52f, 1f);
    public static readonly Vector4 CardBg = new(0.09f, 0.085f, 0.075f, 0.55f);
    public static readonly Vector4 CardBorder = new(0.85f, 0.66f, 0.34f, 0.16f);
    public static readonly Vector4 TrackBg = new(0.16f, 0.15f, 0.14f, 1f);
    public static readonly Vector4 TrackBgHovered = new(0.20f, 0.19f, 0.17f, 1f);
    public static readonly Vector4 TrackBgActive = new(0.24f, 0.22f, 0.20f, 1f);

    public readonly struct StyleScope(int styleVarCount, int colorCount) : IDisposable
    {
        public void Dispose()
        {
            if (colorCount > 0)
            {
                ImGui.PopStyleColor(colorCount);
            }

            if (styleVarCount > 0)
            {
                ImGui.PopStyleVar(styleVarCount);
            }
        }
    }

    public static StyleScope PushWindowStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(9f, 9f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(11f, 7f));

        // Standardize the two text tiers that carry the theater mood without
        // overriding the user's base text color: muted warm ink for hints and a
        // marquee-gold selection wash.
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Muted);
        return new StyleScope(6, 1);
    }

    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Text, InkOnAccent);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(4);
        return clicked;
    }

    public static bool DangerButton(string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Danger);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, DangerHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, DangerActive);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    public static bool QuietButton(string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.17f, 0.15f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.23f, 0.20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.30f, 0.27f, 0.22f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.93f, 0.88f, 0.78f, 1f));
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(4);
        return clicked;
    }

    public static bool IconButton(
        FontAwesomeIcon icon,
        string id,
        Vector2 size = default,
        string? tooltip = null,
        bool primary = false,
        bool danger = false)
    {
        var pushedColors = 0;
        if (primary)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
            ImGui.PushStyleColor(ImGuiCol.Text, InkOnAccent);
            pushedColors = 4;
        }
        else if (danger)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Danger);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, DangerHovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, DangerActive);
            pushedColors = 3;
        }

        ImGui.PushFont(UiBuilder.IconFont);
        var clicked = ImGui.Button($"{icon.ToIconString()}##{id}", size);
        ImGui.PopFont();

        if (pushedColors > 0)
        {
            ImGui.PopStyleColor(pushedColors);
        }

        if (tooltip is not null && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        return clicked;
    }

    /// <summary>
    /// Section header set as marquee signage: a short gold tick, then the label in
    /// uppercase. The tick is the plugin's signature structural device — the same
    /// mark introduces every section so the whole UI reads as one theater.
    /// </summary>
    public static void SectionTitle(string text)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var lineHeight = ImGui.GetTextLineHeight();

        drawList.AddRectFilled(
            origin + new Vector2(0f, lineHeight * 0.14f),
            origin + new Vector2(3f, lineHeight * 0.9f),
            ImGui.ColorConvertFloat4ToU32(Accent),
            1.5f);

        ImGui.Indent(11f);
        ImGui.TextColored(Accent, text.ToUpperInvariant());
        ImGui.Unindent(11f);
    }

    public static bool BeginCollapsibleSection(string label, bool defaultOpen = false, bool primary = false, bool forceOpen = false, bool warm = false)
    {
        var flags = ImGuiTreeNodeFlags.Framed |
                    ImGuiTreeNodeFlags.SpanAvailWidth |
                    ImGuiTreeNodeFlags.FramePadding |
                    ImGuiTreeNodeFlags.AllowItemOverlap;
        if (defaultOpen)
        {
            flags |= ImGuiTreeNodeFlags.DefaultOpen;
        }

        if (forceOpen)
        {
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        }

        var header = primary
            ? new Vector4(0.17f, 0.145f, 0.105f, 0.96f)
            : warm
                ? new Vector4(0.18f, 0.145f, 0.09f, 0.92f)
                : new Vector4(0.12f, 0.11f, 0.095f, 0.92f);
        var hovered = primary
            ? new Vector4(0.23f, 0.19f, 0.13f, 1f)
            : warm
                ? new Vector4(0.33f, 0.245f, 0.13f, 0.96f)
                : new Vector4(0.18f, 0.16f, 0.13f, 0.96f);
        var active = primary
            ? new Vector4(0.28f, 0.23f, 0.15f, 1f)
            : warm
                ? new Vector4(0.42f, 0.31f, 0.15f, 1f)
                : new Vector4(0.22f, 0.19f, 0.15f, 1f);
        var text = primary
            ? new Vector4(0.98f, 0.86f, 0.58f, 1f)
            : warm
                ? new Vector4(0.96f, 0.88f, 0.70f, 1f)
                : new Vector4(0.91f, 0.86f, 0.75f, 1f);

        ImGui.PushStyleColor(ImGuiCol.Header, header);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, active);
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        var open = ImGui.TreeNodeEx(label, flags);
        ImGui.PopStyleColor(4);
        return open;
    }

    public static void StatusDot(Vector4 color)
    {
        var lineHeight = ImGui.GetTextLineHeight();
        var pos = ImGui.GetCursorScreenPos();
        var center = pos + new Vector2(lineHeight * 0.45f, lineHeight * 0.55f);
        ImGui.GetWindowDrawList().AddCircleFilled(center, lineHeight * 0.24f, ImGui.ColorConvertFloat4ToU32(color));
        ImGui.Dummy(new Vector2(lineHeight * 0.9f, lineHeight));
    }

    public const float CardPadding = 10f;

    public static bool BeginCard(string id, float height)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBg);
        ImGui.PushStyleColor(ImGuiCol.Border, CardBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(CardPadding, CardPadding));
        return ImGui.BeginChild(id, new Vector2(0f, height), true, ImGuiWindowFlags.AlwaysUseWindowPadding);
    }

    public static void EndCard()
    {
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
    }

    public static void PushSliderAccent()
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg, TrackBg);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, TrackBgHovered);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, TrackBgActive);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, AccentHovered);
    }

    public static void PopSliderAccent()
    {
        ImGui.PopStyleColor(5);
    }
}
