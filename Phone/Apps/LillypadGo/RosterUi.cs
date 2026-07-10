using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using VideoSyncPrototype.Phone.Apps.Games.Framework;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// One tab of RosterUi.CategoryTabs: a roster sprite icon above a short label, with a tinted
// FontAwesome glyph standing in while the sprite streams (or when no sprite asset exists).
internal readonly record struct CategoryTab(string Label, string? Sprite, FontAwesomeIcon Fallback,
    Vector4 FallbackTint);

// The chunky "monster battler" UI kit introduced with the Roster & Box overhaul: navy headers,
// cream panels, thick-bordered glossy cards and sprite-based icons. Sprites are cropped from the
// Team Assets sheet into Assets/pokemon/roster/ and streamed through AssetTextures; every drawer
// here degrades gracefully (plain fills/text) until its texture is ready.
//
// Components (per the design sheet): HeaderBar, PartyCard, BoxCard, EmptyPartySlot, EmptyBoxSlot,
// IconButton, BlueButton, GreenTab, SearchBar and the sprite BottomNavBar (LillypadGoApp.Shared).
internal static class RosterUi
{
    // ---- Palette (sampled from Ideas/Team Example.png) -------------------------------------------
    public static readonly Vector4 NavyTop = new(0.13f, 0.31f, 0.48f, 1f);        // header gradient top
    public static readonly Vector4 NavyBottom = new(0.08f, 0.22f, 0.37f, 1f);     // header gradient bottom
    public static readonly Vector4 NavyEdge = new(0.05f, 0.13f, 0.22f, 1f);       // dark outlines
    public static readonly Vector4 NavyInset = new(0.03f, 0.15f, 0.27f, 1f);      // count pill / search bar
    public static readonly Vector4 NavyLine = new(0.24f, 0.42f, 0.62f, 1f);       // light hairlines on navy
    public static readonly Vector4 Cream = new(0.96f, 0.91f, 0.84f, 1f);          // main panel
    public static readonly Vector4 CreamShade = new(0.89f, 0.83f, 0.73f, 1f);     // panel bottom shade
    public static readonly Vector4 TileCream = new(0.95f, 0.89f, 0.80f, 1f);      // empty box tile
    public static readonly Vector4 TileEdge = new(0.82f, 0.74f, 0.61f, 1f);       // tile border
    public static readonly Vector4 TanTop = new(0.96f, 0.91f, 0.82f, 1f);         // pokemon card top
    public static readonly Vector4 TanBottom = new(0.85f, 0.78f, 0.65f, 1f);      // pokemon card bottom
    public static readonly Vector4 TanEdge = new(0.66f, 0.59f, 0.46f, 1f);        // pokemon card border
    public static readonly Vector4 BlueCardTop = new(0.83f, 0.93f, 0.99f, 1f);    // lead card top
    public static readonly Vector4 BlueCardBottom = new(0.46f, 0.62f, 0.77f, 1f); // lead card bottom
    public static readonly Vector4 Green = new(0.23f, 0.65f, 0.41f, 1f);          // party/selected green
    public static readonly Vector4 GreenBright = new(0.36f, 0.85f, 0.55f, 1f);    // highlight border green
    public static readonly Vector4 Blue = new(0.17f, 0.42f, 0.70f, 1f);           // primary blue buttons
    public static readonly Vector4 GrayTop = new(0.89f, 0.90f, 0.91f, 1f);        // empty party card top
    public static readonly Vector4 GrayBottom = new(0.72f, 0.74f, 0.76f, 1f);     // empty party card bottom
    public static readonly Vector4 GrayEdge = new(0.63f, 0.65f, 0.67f, 1f);       // empty party border
    public static readonly Vector4 InkNavy = new(0.14f, 0.26f, 0.40f, 1f);        // dark text on light cards
    public static readonly Vector4 InkTan = new(0.32f, 0.28f, 0.21f, 1f);         // dark text on tan cards
    public static readonly Vector4 CountGreen = new(0.35f, 0.87f, 0.57f, 1f);     // "Party" label green
    public static readonly Vector4 CountBlue = new(0.56f, 0.79f, 0.94f, 1f);      // "Stored" label blue

    // ---- Palette additions (UI Update pass: Bag/Market/Arena/Detail/Relearner) -------------------
    public static readonly Vector4 CardTop = new(0.12f, 0.23f, 0.40f, 1f);        // navy item card top
    public static readonly Vector4 CardBottom = new(0.07f, 0.15f, 0.28f, 1f);     // navy item card bottom
    public static readonly Vector4 CardEdge = new(0.30f, 0.46f, 0.64f, 1f);       // navy item card border
    public static readonly Vector4 CardInk = new(1f, 1f, 1f, 0.97f);              // primary text on navy cards
    public static readonly Vector4 CardMuted = new(0.72f, 0.80f, 0.90f, 0.92f);   // secondary text on navy cards
    public static readonly Vector4 Purple = new(0.47f, 0.38f, 0.86f, 1f);         // price / TRAIN buttons
    public static readonly Vector4 Red = new(0.80f, 0.27f, 0.22f, 1f);            // RELEASE / danger buttons
    public static readonly Vector4 Gold = new(0.95f, 0.80f, 0.34f, 1f);           // coins

    // ---- Sprites ----------------------------------------------------------------------------------

    // Draws a roster sprite centred at `center`, fitted into `size` while keeping its aspect.
    // Returns false (drawing nothing) while the texture is still streaming in.
    public static bool Sprite(ImDrawListPtr dl, string name, Vector2 center, float size, Vector4? tint = null)
    {
        if (!AssetTextures.TryGet($"roster/{name}.png", out var handle, out var aspect))
        {
            return false;
        }

        var w = aspect >= 1f ? size : size * aspect;
        var h = aspect >= 1f ? size / aspect : size;
        var half = new Vector2(w * 0.5f, h * 0.5f);
        dl.AddImage(handle, center - half, center + half, Vector2.Zero, Vector2.One,
            ImGui.GetColorU32(tint ?? Vector4.One));
        return true;
    }

    // Stretches a roster sprite over an exact rect (used for slot tiles whose aspect matches).
    public static bool SpriteRect(ImDrawListPtr dl, string name, Vector2 min, Vector2 max, Vector4? tint = null)
    {
        if (!AssetTextures.TryGet($"roster/{name}.png", out var handle, out _))
        {
            return false;
        }

        dl.AddImage(handle, min, max, Vector2.Zero, Vector2.One, ImGui.GetColorU32(tint ?? Vector4.One));
        return true;
    }

    // ---- Text -------------------------------------------------------------------------------------

    // Trims `text` with an ellipsis until it fits `maxWidth`. Used to keep button labels and other
    // data-driven strings (nicknames, move names) inside their chrome instead of spilling over it.
    public static string Ellipsize(string text, float maxWidth, in TextStyle style)
    {
        if (maxWidth <= 0f || Typography.Measure(text, style).X <= maxWidth)
        {
            return text;
        }

        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length].TrimEnd() + "...";
            if (Typography.Measure(candidate, style).X <= maxWidth)
            {
                return candidate;
            }
        }

        return "...";
    }

    // Chunky game text: a dark outline ring under a bright fill, like the mockup's card names.
    public static void TextOutlined(Vector2 center, string text, Vector4 fill, Vector4 outline, in TextStyle style,
        float scale)
    {
        var o = 1.2f * scale;
        Span<Vector2> offsets = stackalloc Vector2[8]
        {
            new(-o, 0f), new(o, 0f), new(0f, -o), new(0f, o), new(-o, -o), new(o, -o), new(-o, o), new(o, o),
        };
        foreach (var off in offsets)
        {
            Typography.DrawCentered(center + off, text, outline, style);
        }

        Typography.DrawCentered(center, text, fill, style);
    }

    // ---- Surfaces ---------------------------------------------------------------------------------

    // A thick-bordered, glossy vertical-gradient card: soft shadow, dark outer edge, bright fill and
    // a top shine line. The building block for every card/tile on the roster screen.
    public static void ChunkyCard(ImDrawListPtr dl, Vector2 min, Vector2 max, float radius, float scale,
        Vector4 top, Vector4 bottom, Vector4 border, bool hovered = false)
    {
        dl.AddRectFilled(min + new Vector2(0f, 2.5f * scale), max + new Vector2(0f, 2.5f * scale),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.16f)), radius);
        if (hovered)
        {
            top = GamePalette.Lighten(top, 0.05f);
            bottom = GamePalette.Lighten(bottom, 0.05f);
        }

        Squircle.FillVerticalGradient(dl, min, max, radius, ImGui.GetColorU32(top), ImGui.GetColorU32(bottom));
        Squircle.Stroke(dl, min, max, radius, ImGui.GetColorU32(border), 2f * scale);
        dl.AddLine(new Vector2(min.X + radius, min.Y + 2f * scale), new Vector2(max.X - radius, min.Y + 2f * scale),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.45f)), 1.2f * scale);
    }

    // The pale Poké Ball watermark (alpha mask lifted from the asset sheet), tintable per surface.
    public static void Watermark(ImDrawListPtr dl, Vector2 center, float size, Vector4 tint)
    {
        if (!Sprite(dl, "watermark_white", center, size, tint))
        {
            // fallback ring while the mask streams in
            dl.AddCircle(center, size * 0.42f, ImGui.GetColorU32(tint with { W = tint.W * 0.6f }), 24, size * 0.09f);
        }
    }

    // ---- Buttons ----------------------------------------------------------------------------------

    // A glossy blue capsule button ("SORT DEX", "BOXES"): white bold label, optional leading sprite.
    // Returns true on click-release.
    public static bool BlueButton(Rect r, string label, float scale, bool enabled, string? iconSprite = null) =>
        ColorButton(r, label, Blue, scale, enabled, iconSprite);

    // The generic chunky capsule button behind BlueButton, in an arbitrary brand colour (green
    // MOVES, red RELEASE, purple prices/TRAIN...). Disabled buttons fall back to a gray slab.
    // `sub` adds a smaller second line under the label. Returns true on click-release.
    public static bool ColorButton(Rect r, string label, Vector4 color, float scale, bool enabled,
        string? iconSprite = null, string? sub = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var hovered = enabled && LgUi.Interactive && ImGui.IsMouseHoveringRect(r.Min, r.Max);
        var pressed = hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var radius = MathF.Min(11f * scale, r.Height * 0.5f);
        var fill = enabled ? color : GamePalette.Darken(color, 0.25f) with { X = 0.28f, Y = 0.33f, Z = 0.40f };
        var top = GamePalette.Lighten(fill, pressed ? 0.02f : hovered ? 0.16f : 0.10f);
        var bottom = GamePalette.Darken(fill, pressed ? 0.22f : 0.10f);

        dl.AddRectFilled(r.Min + new Vector2(0f, 2f * scale), r.Max + new Vector2(0f, 2f * scale),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.20f)), radius);
        Squircle.FillVerticalGradient(dl, r.Min, r.Max, radius, ImGui.GetColorU32(top), ImGui.GetColorU32(bottom));
        Squircle.Stroke(dl, r.Min, r.Max, radius, ImGui.GetColorU32(NavyEdge), 1.6f * scale);
        dl.AddLine(new Vector2(r.Min.X + radius, r.Min.Y + 2f * scale),
            new Vector2(r.Max.X - radius, r.Min.Y + 2f * scale),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f)), 1.2f * scale);

        var textColor = enabled ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(1f, 1f, 1f, 0.45f);
        var center = r.Center;
        if (sub is not null)
        {
            center.Y -= 6f * scale;
        }

        // Pick a smaller label style before ellipsizing so narrow controls remain readable.
        var iconSize = iconSprite is null ? 0f : MathF.Min(r.Height * 0.62f, 17f * scale);
        var iconGap = iconSprite is null ? 0f : 5f * scale;
        var labelWidth = r.Width - 14f * scale - iconSize - iconGap;
        var labelStyle = TextStyles.SubheadlineEmphasized;
        if (Typography.Measure(label, labelStyle).X > labelWidth)
        {
            labelStyle = TextStyles.Caption1;
        }

        if (Typography.Measure(label, labelStyle).X > labelWidth)
        {
            labelStyle = TextStyles.Caption2;
        }

        label = Ellipsize(label, labelWidth, labelStyle);
        if (iconSprite is not null)
        {
            var textW = Typography.Measure(label, labelStyle).X;
            var totalW = iconSize + iconGap + textW;
            var iconCenter = new Vector2(center.X - totalW * 0.5f + iconSize * 0.5f, center.Y);
            Sprite(dl, iconSprite, iconCenter, iconSize, textColor);
            center.X += (iconSize + iconGap) * 0.5f;
        }

        Typography.DrawCentered(center, label, textColor, labelStyle);
        if (sub is not null)
        {
            Typography.DrawCentered(new Vector2(r.Center.X, center.Y + 13f * scale),
                Ellipsize(sub, r.Width - 12f * scale, TextStyles.Caption2),
                textColor with { W = textColor.W * 0.82f }, TextStyles.Caption2);
        }

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        return hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
    }

    // A square sprite button (the box page arrows). Falls back to a blue chevron slab.
    public static bool IconButton(Rect r, string sprite, string fallback, float scale, bool enabled)
    {
        var dl = ImGui.GetWindowDrawList();
        var hovered = enabled && LgUi.Interactive && ImGui.IsMouseHoveringRect(r.Min, r.Max);
        var tint = enabled ? Vector4.One : new Vector4(1f, 1f, 1f, 0.4f);
        if (!SpriteRect(dl, sprite, r.Min, r.Max, tint))
        {
            Squircle.FillVerticalGradient(dl, r.Min, r.Max, 7f * scale,
                ImGui.GetColorU32(GamePalette.Lighten(Blue, 0.1f) with { W = tint.W }),
                ImGui.GetColorU32(GamePalette.Darken(Blue, 0.12f) with { W = tint.W }));
            Typography.DrawCentered(r.Center, fallback, new Vector4(1f, 1f, 1f, tint.W),
                TextStyles.SubheadlineEmphasized);
        }

        if (hovered)
        {
            Squircle.Fill(dl, r.Min, r.Max, 7f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.14f)));
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        return hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
    }

    // ---- Composite pieces ---------------------------------------------------------------------

    // The green glossy "PARTY" column header tab.
    public static void GreenTab(ImDrawListPtr dl, Rect r, string label, float scale)
    {
        var radius = 8f * scale;
        ChunkyCard(dl, r.Min, r.Max, radius, scale,
            GamePalette.Lighten(Green, 0.16f), GamePalette.Darken(Green, 0.06f), GamePalette.Darken(Green, 0.30f));
        Watermark(dl, new Vector2(r.Max.X - 13f * scale, r.Center.Y), 13f * scale, new Vector4(1f, 1f, 1f, 0.55f));
        TextOutlined(r.Center, label, new Vector4(1f, 1f, 1f, 1f), GamePalette.Darken(Green, 0.35f),
            TextStyles.SubheadlineEmphasized, scale);
    }

    // The cream "BOX 1/1" plate between the page arrows.
    public static void BoxPlate(ImDrawListPtr dl, Rect r, string label, float scale)
    {
        var radius = 7f * scale;
        Squircle.FillVerticalGradient(dl, r.Min, r.Max, radius,
            ImGui.GetColorU32(Cream), ImGui.GetColorU32(CreamShade));
        Squircle.Stroke(dl, r.Min, r.Max, radius, ImGui.GetColorU32(NavyEdge with { W = 0.85f }), 1.6f * scale);
        Typography.DrawCentered(r.Center, label, InkNavy, TextStyles.SubheadlineEmphasized);
    }

    // The navy search bar chrome + a transparent hint input laid over it. Returns true on Enter.
    public static bool SearchBar(Rect r, string id, string hint, ref string text, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var radius = MathF.Min(10f * scale, r.Height * 0.5f);
        dl.AddRectFilled(r.Min + new Vector2(0f, 2f * scale), r.Max + new Vector2(0f, 2f * scale),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.18f)), radius);
        Squircle.FillVerticalGradient(dl, r.Min, r.Max, radius,
            ImGui.GetColorU32(GamePalette.Darken(NavyInset, 0.04f)), ImGui.GetColorU32(NavyInset));
        Squircle.Stroke(dl, r.Min, r.Max, radius, ImGui.GetColorU32(NavyEdge), 1.6f * scale);
        Squircle.Stroke(dl, r.Min + new Vector2(1.6f, 1.6f) * scale, r.Max - new Vector2(1.6f, 1.6f) * scale,
            radius - 1.6f * scale, ImGui.GetColorU32(NavyLine with { W = 0.35f }), 1f * scale);

        // magnifier glyph
        var iconCenter = new Vector2(r.Min.X + 13f * scale, r.Center.Y);
        var white = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.92f));
        dl.AddCircle(iconCenter - new Vector2(1f, 1f) * scale, 4f * scale, white, 16, 1.8f * scale);
        dl.AddLine(iconCenter + new Vector2(2f, 2f) * scale, iconCenter + new Vector2(5.4f, 5.4f) * scale, white,
            2f * scale);

        var fieldMin = new Vector2(r.Min.X + 24f * scale, r.Min.Y);
        var framePadY = MathF.Max(1f, (r.Height - ImGui.GetTextLineHeight()) * 0.5f);
        ImGui.SetCursorScreenPos(fieldMin);
        ImGui.SetNextItemWidth(r.Max.X - fieldMin.X - 6f * scale);
        bool submitted;
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(4f * scale, framePadY)))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0f, 0f, 0f, 0f)))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0f)))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, new Vector4(0f, 0f, 0f, 0f)))
        using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 0.95f)))
        using (ImRaii.PushColor(ImGuiCol.TextDisabled, new Vector4(0.62f, 0.72f, 0.83f, 0.7f)))
        {
            submitted = ImGui.InputTextWithHint(id, hint, ref text, 16, ImGuiInputTextFlags.EnterReturnsTrue);
        }

        return submitted;
    }

    // ---- Screen chrome (UI Update pass) ---------------------------------------------------------

    // The standard navy screen header: gradient band with faded Poké Ball watermarks, an outlined
    // title (with an optional sprite badge on its left) and an optional inset subtitle pill made of
    // coloured segments. Returns the header's bottom edge. Pair with CreamPanel below it.
    public static float ScreenHeader(Rect content, string title, string? iconSprite,
        (string Text, Vector4 Color)[]? subtitle, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var height = (subtitle is null ? 46f : 64f) * scale;
        var bottom = content.Min.Y + height;
        var max = new Vector2(content.Max.X, bottom);
        dl.AddRectFilledMultiColor(content.Min, max,
            ImGui.GetColorU32(NavyTop), ImGui.GetColorU32(NavyTop),
            ImGui.GetColorU32(NavyBottom), ImGui.GetColorU32(NavyBottom));

        dl.PushClipRect(content.Min, max, true);
        Sprite(dl, "watermark_dark", new Vector2(content.Max.X - 34f * scale, content.Min.Y + 14f * scale),
            50f * scale, new Vector4(1f, 1f, 1f, 0.55f));
        Sprite(dl, "watermark_dark", new Vector2(content.Max.X - 92f * scale, content.Min.Y + height - 8f * scale),
            36f * scale, new Vector4(1f, 1f, 1f, 0.45f));
        dl.PopClipRect();

        dl.AddLine(new Vector2(content.Min.X, content.Min.Y + 1f * scale),
            new Vector2(content.Max.X, content.Min.Y + 1f * scale),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.20f)), 1.2f * scale);
        dl.AddLine(max with { X = content.Min.X }, max, ImGui.GetColorU32(NavyEdge), 2.5f * scale);

        var titleCenter = new Vector2(content.Center.X + (iconSprite is null ? 0f : 12f * scale),
            content.Min.Y + (subtitle is null ? height * 0.5f : 21f * scale));
        TextOutlined(titleCenter, title, new Vector4(1f, 1f, 1f, 1f), NavyEdge, TextStyles.Title2, scale);
        if (iconSprite is not null)
        {
            var titleWidth = Typography.Measure(title, TextStyles.Title2).X;
            Sprite(dl, iconSprite,
                new Vector2(titleCenter.X - titleWidth * 0.5f - 22f * scale, titleCenter.Y + 4f * scale),
                33f * scale);
        }

        if (subtitle is not null)
        {
            Pill(dl, new Vector2(content.Center.X, content.Min.Y + 48f * scale), subtitle,
                TextStyles.FootnoteEmphasized, scale);
        }

        return bottom;
    }

    // A navy inset pill of coloured text segments (badge counts, money, level tags), centred at
    // `center`. Returns the pill's rect so callers can hit-test or align against it.
    public static Rect Pill(ImDrawListPtr dl, Vector2 center, (string Text, Vector4 Color)[] segments,
        in TextStyle style, float scale)
    {
        var gap = 5f * scale;
        var totalW = -gap;
        foreach (var (text, _) in segments)
        {
            totalW += Typography.Measure(text, style).X + gap;
        }

        var min = new Vector2(center.X - totalW * 0.5f - 11f * scale, center.Y - 10f * scale);
        var max = new Vector2(center.X + totalW * 0.5f + 11f * scale, center.Y + 10f * scale);
        var radius = (max.Y - min.Y) * 0.5f;
        Squircle.Fill(dl, min, max, radius, ImGui.GetColorU32(NavyInset));
        Squircle.Stroke(dl, min, max, radius, ImGui.GetColorU32(NavyLine with { W = 0.45f }), 1f * scale);

        var x = center.X - totalW * 0.5f;
        foreach (var (text, color) in segments)
        {
            var w = Typography.Measure(text, style).X;
            Typography.DrawCentered(new Vector2(x + w * 0.5f, center.Y), text, color, style);
            x += w + gap;
        }

        return new Rect(min, max);
    }

    // The cream content panel that sits between the navy header and the bottom nav bar.
    public static void CreamPanel(ImDrawListPtr dl, Rect r, float scale)
    {
        Squircle.FillVerticalGradient(dl, r.Min, r.Max, 12f * scale,
            ImGui.GetColorU32(Cream), ImGui.GetColorU32(CreamShade));
        Squircle.Stroke(dl, r.Min, r.Max, 12f * scale, ImGui.GetColorU32(NavyEdge with { W = 0.55f }), 1.6f * scale);
    }

    // The dark navy list card used for items, moves, tiers and gyms: soft shadow, navy gradient,
    // light blue border and a shine line, plus an optional coloured accent bar down the left edge.
    // `sunken` renders the flat, dimmed variant for locked/disabled rows.
    public static void DarkCard(ImDrawListPtr dl, Rect r, float radius, float scale, bool hovered = false,
        bool sunken = false, Vector4? accent = null)
    {
        if (sunken)
        {
            Squircle.Fill(dl, r.Min, r.Max, radius, ImGui.GetColorU32(GamePalette.Darken(CardBottom, 0.05f)));
            Squircle.Stroke(dl, r.Min, r.Max, radius, ImGui.GetColorU32(CardEdge with { W = 0.35f }), 1.6f * scale);
        }
        else
        {
            dl.AddRectFilled(r.Min + new Vector2(0f, 2.5f * scale), r.Max + new Vector2(0f, 2.5f * scale),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.16f)), radius);
            var top = hovered ? GamePalette.Lighten(CardTop, 0.05f) : CardTop;
            var bottom = hovered ? GamePalette.Lighten(CardBottom, 0.05f) : CardBottom;
            Squircle.FillVerticalGradient(dl, r.Min, r.Max, radius, ImGui.GetColorU32(top),
                ImGui.GetColorU32(bottom));
            Squircle.Stroke(dl, r.Min, r.Max, radius, ImGui.GetColorU32(CardEdge with { W = hovered ? 1f : 0.8f }),
                1.6f * scale);
            dl.AddLine(new Vector2(r.Min.X + radius, r.Min.Y + 2f * scale),
                new Vector2(r.Max.X - radius, r.Min.Y + 2f * scale),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.14f)), 1.2f * scale);
        }

        if (accent is { } bar)
        {
            dl.PushClipRect(r.Min, new Vector2(r.Min.X + 5f * scale, r.Max.Y), true);
            Squircle.Fill(dl, r.Min, r.Max, radius, ImGui.GetColorU32(bar with { W = sunken ? bar.W * 0.4f : bar.W }));
            dl.PopClipRect();
        }
    }

    // The inset rounded-square tile that frames an item/TM icon on a navy card.
    public static void IconTile(ImDrawListPtr dl, Vector2 center, float size, float scale, Vector4? edge = null)
    {
        var half = new Vector2(size, size) * 0.5f;
        Squircle.Fill(dl, center - half, center + half, 7f * scale, ImGui.GetColorU32(NavyInset));
        Squircle.Stroke(dl, center - half, center + half, 7f * scale,
            ImGui.GetColorU32(edge ?? CardEdge with { W = 0.9f }), 1.6f * scale);
    }

    // Two chunky folder tabs (TRAINING/GYMS, Items/TMs, LEVEL-UP/TM'S): the selected tab is glossy
    // green with outlined white text, the rest are navy slabs. Returns the clicked index, or -1.
    public static int FolderTabs(Rect bounds, string[] labels, int selected, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var clicked = -1;
        var gap = 6f * scale;
        var tabW = (bounds.Width - gap * (labels.Length - 1)) / labels.Length;
        for (var i = 0; i < labels.Length; i++)
        {
            var min = new Vector2(bounds.Min.X + i * (tabW + gap), bounds.Min.Y);
            var r = new Rect(min, new Vector2(min.X + tabW, bounds.Max.Y));
            var isSelected = i == selected;
            var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(r.Min, r.Max);
            var radius = 9f * scale;
            if (isSelected)
            {
                ChunkyCard(dl, r.Min, r.Max, radius, scale, GamePalette.Lighten(Green, 0.16f),
                    GamePalette.Darken(Green, 0.06f), GamePalette.Darken(Green, 0.30f));
                TextOutlined(r.Center, labels[i], new Vector4(1f, 1f, 1f, 1f), GamePalette.Darken(Green, 0.35f),
                    TextStyles.SubheadlineEmphasized, scale);
            }
            else
            {
                ChunkyCard(dl, r.Min, r.Max, radius, scale,
                    GamePalette.Lighten(NavyInset, hovered ? 0.08f : 0.04f), NavyInset, NavyEdge, hovered);
                Typography.DrawCentered(r.Center, labels[i], new Vector4(1f, 1f, 1f, hovered ? 0.95f : 0.72f),
                    TextStyles.SubheadlineEmphasized);
            }

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (!isSelected && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    clicked = i;
                }
            }
        }

        return clicked;
    }

    // The tab strip is far narrower than the mockup's render, so a label ("Poké Balls", "Medicine")
    // rarely fits Caption2 on one line. Rather than ellipsize — which hides which pocket a tab is —
    // wrap it at its space, then step the type size down until both lines fit. The ladder has to
    // reach far enough down for the unwrappable single words ("Medicine", "Berries") to survive a
    // 280px canvas, where six tabs leave each one about 37px.
    private static (string[] Lines, TextStyle Style) FitTabLabel(string label, float maxWidth)
    {
        Span<float> sizes = stackalloc float[] { 0.60f, 0.55f, 0.50f, 0.46f, 0.42f, 0.38f };
        var space = label.LastIndexOf(' ');
        foreach (var size in sizes)
        {
            var style = new TextStyle(size, FontWeight.Medium);
            if (Typography.Measure(label, style).X <= maxWidth)
            {
                return (new[] { label }, style);
            }

            if (space > 0)
            {
                var head = label[..space];
                var tail = label[(space + 1)..];
                if (MathF.Max(Typography.Measure(head, style).X, Typography.Measure(tail, style).X) <= maxWidth)
                {
                    return (new[] { head, tail }, style);
                }
            }
        }

        var last = new TextStyle(sizes[^1], FontWeight.Medium);
        return space > 0
            ? (new[] { Ellipsize(label[..space], maxWidth, last), Ellipsize(label[(space + 1)..], maxWidth, last) },
                last)
            : (new[] { Ellipsize(label, maxWidth, last) }, last);
    }

    // The Bag mockup's category filter strip: chunky icon-over-label tabs, the selected one glossy
    // green with outlined white text, the rest raised cream tiles. Returns the clicked index or -1.
    public static int CategoryTabs(Rect bounds, IReadOnlyList<CategoryTab> tabs, int selected, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var clicked = -1;
        var gap = 5f * scale;
        var tabW = (bounds.Width - gap * (tabs.Count - 1)) / tabs.Count;
        for (var i = 0; i < tabs.Count; i++)
        {
            var min = new Vector2(bounds.Min.X + i * (tabW + gap), bounds.Min.Y);
            var r = new Rect(min, new Vector2(min.X + tabW, bounds.Max.Y));
            var isSelected = i == selected;
            var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(r.Min, r.Max);
            var radius = 9f * scale;
            if (isSelected)
            {
                ChunkyCard(dl, r.Min, r.Max, radius, scale, GamePalette.Lighten(Green, 0.16f),
                    GamePalette.Darken(Green, 0.06f), GamePalette.Darken(Green, 0.30f));
            }
            else
            {
                ChunkyCard(dl, r.Min, r.Max, radius, scale,
                    GamePalette.Lighten(TileCream, hovered ? 0.05f : 0.02f), TileCream, TileEdge, hovered);
            }

            // Fit the label first — a wrapped label eats into the icon's share of the tab.
            var tab = tabs[i];
            var (lines, labelStyle) = FitTabLabel(tab.Label, tabW - 4f * scale);
            var lineHeight = Typography.Measure("Xg", labelStyle).Y;
            var textBlock = lines.Length * lineHeight;
            var iconZone = r.Height - textBlock - 8f * scale;

            var iconCenter = new Vector2(r.Center.X, r.Min.Y + 4f * scale + iconZone * 0.5f);
            var iconSize = MathF.Min(tabW - 6f * scale, iconZone);
            if (tab.Sprite is null || !Sprite(dl, tab.Sprite, iconCenter, iconSize))
            {
                ProgressRing.CenterIcon(dl, iconCenter, tab.Fallback,
                    isSelected ? new Vector4(1f, 1f, 1f, 0.96f) : tab.FallbackTint, iconSize * 0.5f);
            }

            var lineY = r.Max.Y - 3f * scale - textBlock + lineHeight * 0.5f;
            foreach (var line in lines)
            {
                var lineCenter = new Vector2(r.Center.X, lineY);
                if (isSelected)
                {
                    TextOutlined(lineCenter, line, new Vector4(1f, 1f, 1f, 1f),
                        GamePalette.Darken(Green, 0.35f), labelStyle, scale);
                }
                else
                {
                    Typography.DrawCentered(lineCenter, line, InkNavy with { W = hovered ? 1f : 0.85f }, labelStyle);
                }

                lineY += lineHeight;
            }

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (!isSelected && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    clicked = i;
                }
            }
        }

        return clicked;
    }

    // Space the chevron and its divider occupy on the capsule's right edge.
    private const float SortChevronZone = 17f;

    // The width a Sort capsule needs to show its *longest* value without ellipsizing. Callers size
    // the capsule once from every label it can cycle through, so the text never shifts or clips.
    public static float SortButtonWidth(IReadOnlyList<string> values, float scale)
    {
        var widest = 0f;
        foreach (var value in values)
        {
            widest = MathF.Max(widest, Typography.Measure($"Sort: {value}", TextStyles.FootnoteEmphasized).X);
        }

        return widest + (SortChevronZone + 16f) * scale;
    }

    // The mockup's navy "Sort: Type ▾" capsule: left-aligned label, a hairline divider and a
    // chevron on the right. Clicking cycles the sort mode; returns true on click-release.
    public static bool SortButton(Rect r, string value, float scale, bool enabled)
    {
        var dl = ImGui.GetWindowDrawList();
        var hovered = enabled && LgUi.Interactive && ImGui.IsMouseHoveringRect(r.Min, r.Max);
        var pressed = hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var radius = MathF.Min(11f * scale, r.Height * 0.5f);
        var fill = enabled ? Blue : GamePalette.Darken(Blue, 0.25f) with { X = 0.28f, Y = 0.33f, Z = 0.40f };
        var top = GamePalette.Lighten(fill, pressed ? 0.02f : hovered ? 0.16f : 0.10f);
        var bottom = GamePalette.Darken(fill, pressed ? 0.22f : 0.10f);

        dl.AddRectFilled(r.Min + new Vector2(0f, 2f * scale), r.Max + new Vector2(0f, 2f * scale),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.20f)), radius);
        Squircle.FillVerticalGradient(dl, r.Min, r.Max, radius, ImGui.GetColorU32(top), ImGui.GetColorU32(bottom));
        Squircle.Stroke(dl, r.Min, r.Max, radius, ImGui.GetColorU32(NavyEdge), 1.6f * scale);
        dl.AddLine(new Vector2(r.Min.X + radius, r.Min.Y + 2f * scale),
            new Vector2(r.Max.X - radius, r.Min.Y + 2f * scale),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f)), 1.2f * scale);

        var ink = new Vector4(1f, 1f, 1f, enabled ? 1f : 0.45f);
        var chevronZone = SortChevronZone * scale;
        var divX = r.Max.X - chevronZone;
        dl.AddLine(new Vector2(divX, r.Min.Y + 5f * scale), new Vector2(divX, r.Max.Y - 5f * scale),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.30f)), 1f * scale);
        var chev = new Vector2(divX + chevronZone * 0.5f, r.Center.Y);
        var cw = 3.4f * scale;
        dl.AddLine(chev + new Vector2(-cw, -cw * 0.55f), chev + new Vector2(0f, cw * 0.55f),
            ImGui.GetColorU32(ink), 1.8f * scale);
        dl.AddLine(chev + new Vector2(0f, cw * 0.55f), chev + new Vector2(cw, -cw * 0.55f),
            ImGui.GetColorU32(ink), 1.8f * scale);

        var label = $"Sort: {value}";
        var labelWidth = r.Width - chevronZone - 14f * scale;
        var style = TextStyles.FootnoteEmphasized;
        if (Typography.Measure(label, style).X > labelWidth)
        {
            style = TextStyles.Caption2;
        }

        label = Ellipsize(label, labelWidth, style);
        var size = Typography.Measure(label, style);
        Typography.Draw(new Vector2(r.Min.X + 8f * scale, r.Center.Y - size.Y * 0.5f), label, ink, style);

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        return hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
    }
}
