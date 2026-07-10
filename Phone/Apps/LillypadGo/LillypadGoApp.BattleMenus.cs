using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using VideoSyncPrototype.Phone.Apps.Games.Framework;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Animation;
using VideoSyncPrototype.Phone.Core.Apps;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal sealed partial class LillypadGoApp
{
    // ---- Battle: message box, action menus, result ----

    private readonly record struct BattleTextEntry(string Text, float Age, string? MoveName, Vector4? MoveColor);

    private readonly record struct BattleTextSegment(string Text, Vector4? Color);

    private void DrawMessage(Rect panel, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var bedMin = panel.Min + new Vector2(10f * scale, 10f * scale);
        var bedMax = panel.Max - new Vector2(10f * scale, 12f * scale);
        Squircle.Fill(drawList, bedMin, bedMax, 10f * scale,
            ImGui.GetColorU32(RosterUi.NavyInset with { W = 0.8f }));
        Squircle.Stroke(drawList, bedMin, bedMax, 10f * scale, ImGui.GetColorU32(RosterUi.NavyEdge with { W = 0.9f }),
            1.4f * scale);

        var maxWidth = bedMax.X - bedMin.X - 34f * scale;
        var lineHeight = 21f * scale;
        var gap = 8f * scale;
        var top = bedMin.Y + 12f * scale;
        var bottomLimit = bedMax.Y - 20f * scale;
        var yTop = top;
        var newestIndex = battleText.Count - 1;
        var visibleEntries = 0;
        var firstIndex = Math.Max(0, newestIndex - 2);

        for (var entryIndex = firstIndex; entryIndex <= newestIndex && yTop < bottomLimit && visibleEntries < 3; entryIndex++)
        {
            var entry = battleText[entryIndex];
            var wrapped = WrapBattleText(entry, maxWidth);
            if (wrapped.Count > 2)
            {
                wrapped = wrapped.Take(2).ToList();
            }

            var isNewest = entryIndex == newestIndex;
            var fullHeight = wrapped.Count * lineHeight + gap;
            var reveal = isNewest ? Easing.EaseOutCubic(Math.Clamp(entry.Age / 0.22f, 0f, 1f)) : 1f;
            var visibleHeight = fullHeight * reveal;
            var clipBottom = yTop + visibleHeight;
            var alpha = isNewest ? Math.Clamp(entry.Age / 0.12f, 0f, 1f) : 0.58f;
            var textColor = isNewest ? theme.TextStrong : theme.TextMuted;

            if (isNewest)
            {
                drawList.AddRectFilled(new Vector2(bedMin.X + 8f * scale, yTop + 3f * scale),
                    new Vector2(bedMin.X + 11f * scale, yTop + visibleHeight - 4f * scale),
                    ImGui.GetColorU32((entry.MoveColor ?? Accent) with { W = alpha }),
                    2f * scale);
            }

            drawList.PushClipRect(new Vector2(bedMin.X, yTop),
                new Vector2(bedMax.X, MathF.Min(bottomLimit, clipBottom + 2f * scale)), true);
            for (var lineIndex = 0; lineIndex < wrapped.Count; lineIndex++)
            {
                var y = yTop + lineIndex * lineHeight + gap * 0.5f;
                if (y > bottomLimit)
                {
                    break;
                }

                DrawBattleTextLine(new Vector2(bedMin.X + 18f * scale, y), wrapped[lineIndex],
                    textColor with { W = alpha }, alpha, isNewest ? TextStyles.BodyEmphasized : TextStyles.Body);
            }
            drawList.PopClipRect();

            yTop += visibleHeight;
            visibleEntries++;
        }

        if (messageTimer > 0.18f)
        {
            Typography.DrawCentered(new Vector2(bedMax.X - 23f * scale, bedMax.Y - 10f * scale), "click",
                theme.TextMuted with { W = 0.62f }, TextStyles.Caption2);
        }
    }

    private void AddBattleText(BattleMessage msg)
    {
        var color = BattleTextColor(msg);
        if (battleText.Count > 0 && battleText[^1].Text == msg.Text)
        {
            battleText[^1] = new BattleTextEntry(msg.Text, 0f, msg.Move?.Name, color);
            return;
        }

        battleText.Add(new BattleTextEntry(msg.Text, 0f, msg.Move?.Name, color));
        while (battleText.Count > 5)
        {
            battleText.RemoveAt(0);
        }
    }

    private static Vector4? BattleTextColor(BattleMessage msg)
    {
        if (msg.Move is null)
        {
            return null;
        }

        return msg.Cue is BattleCue.PlayerAttack or BattleCue.WildAttack or BattleCue.PlayerHurt or BattleCue.WildHurt
            ? Elements.Color(msg.Move.Element)
            : null;
    }

    private List<List<BattleTextSegment>> WrapBattleText(BattleTextEntry entry, float maxWidth)
    {
        var segments = SplitBattleText(entry);
        var lines = new List<List<BattleTextSegment>>();
        var current = new List<BattleTextSegment>();
        var currentWidth = 0f;

        foreach (var segment in segments)
        {
            var words = segment.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                var token = current.Count == 0 ? word : " " + word;
                var tokenWidth = Typography.Measure(token, TextStyles.Body).X;
                if (current.Count > 0 && currentWidth + tokenWidth > maxWidth)
                {
                    lines.Add(current);
                    current = new List<BattleTextSegment>();
                    currentWidth = 0f;
                    token = word;
                    tokenWidth = Typography.Measure(token, TextStyles.Body).X;
                }

                current.Add(new BattleTextSegment(token, segment.Color));
                currentWidth += tokenWidth;
            }
        }

        if (current.Count > 0)
        {
            lines.Add(current);
        }

        return lines.Count > 0 ? lines : new List<List<BattleTextSegment>> { new() };
    }

    private static List<BattleTextSegment> SplitBattleText(BattleTextEntry entry)
    {
        if (entry.MoveName is null || entry.MoveColor is null)
        {
            return new List<BattleTextSegment> { new(entry.Text, null) };
        }

        var index = entry.Text.IndexOf(entry.MoveName, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return new List<BattleTextSegment> { new(entry.Text, null) };
        }

        var segments = new List<BattleTextSegment>(3);
        if (index > 0)
        {
            segments.Add(new BattleTextSegment(entry.Text[..index], null));
        }

        segments.Add(new BattleTextSegment(entry.Text.Substring(index, entry.MoveName.Length), entry.MoveColor));
        var suffixStart = index + entry.MoveName.Length;
        if (suffixStart < entry.Text.Length)
        {
            segments.Add(new BattleTextSegment(entry.Text[suffixStart..], null));
        }

        return segments;
    }

    private static void DrawBattleTextLine(Vector2 position, IReadOnlyList<BattleTextSegment> line, Vector4 baseColor,
        float alpha, in TextStyle style)
    {
        var x = position.X;
        foreach (var segment in line)
        {
            var color = segment.Color is { } moveColor ? GamePalette.Lighten(moveColor, 0.28f) with { W = alpha } : baseColor;
            Typography.Draw(new Vector2(x, position.Y), segment.Text, color, style);
            x += Typography.Measure(segment.Text, style).X;
        }
    }

    private void DrawActionMenu(Rect panel, PhoneTheme theme, float scale)
    {
        if (battle is null)
        {
            return;
        }

        if (battle.RequiresSwitch)
        {
            DrawSwitchMenu(panel, theme, scale);
            return;
        }

        if (confirmingRun)
        {
            DrawRunConfirm(panel, theme, scale);
            return;
        }

        switch (menu)
        {
            case Menu.Root:
            {
                // Mid-charge on a two-turn move, or recharging after a Hyper Beam-type move: the
                // creature is committed, so lock the menu to continuing (no switching/items).
                if (battle.Active.ChargingMove is { } charging)
                {
                    Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 16f * scale),
                        $"{battle.Active.Name} is charging {charging.Name}!", theme.TextStrong, TextStyles.Headline);
                    var unleash = CenteredAt(new Vector2(panel.Center.X, panel.Center.Y + 14f * scale),
                        new Vector2(panel.Width * 0.62f, panel.Height * 0.34f));
                    if (RosterUi.ColorButton(unleash, $"Unleash {charging.Name}", RosterUi.Green, scale, true))
                    {
                        battle.UseMove(0);
                        menu = Menu.Root;
                    }

                    break;
                }

                if (battle.Active.MustRecharge)
                {
                    Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 16f * scale),
                        $"{battle.Active.Name} must recharge!", theme.TextStrong, TextStyles.Headline);
                    var recharge = CenteredAt(new Vector2(panel.Center.X, panel.Center.Y + 14f * scale),
                        new Vector2(panel.Width * 0.62f, panel.Height * 0.34f));
                    if (RosterUi.ColorButton(recharge, "Recharge", RosterUi.Green, scale, true))
                    {
                        battle.UseMove(0);
                        menu = Menu.Root;
                    }

                    break;
                }

                if (battle.Active.LockedMove is { } rampage)
                {
                    Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 16f * scale),
                        $"{battle.Active.Name} is locked into {rampage.Name}!", theme.TextStrong, TextStyles.Headline);
                    var cont = CenteredAt(new Vector2(panel.Center.X, panel.Center.Y + 14f * scale),
                        new Vector2(panel.Width * 0.62f, panel.Height * 0.34f));
                    if (RosterUi.ColorButton(cont, rampage.Name, RosterUi.Green, scale, true))
                    {
                        battle.UseMove(0);
                        menu = Menu.Root;
                    }

                    break;
                }

                var quad = new[] { "FIGHT", "BAG", "TEAM", "RUN" };
                var colors = new[] { RosterUi.Green, RosterUi.Blue, RosterUi.Purple, RosterUi.Red };
                var hints = new[]
                {
                    "Choose a move. Hover over a move to inspect its power and effects.",
                    battle.IsTrainerBattle
                        ? "Use an item to heal, revive or cure your team."
                        : "Throw a Poké Ball, or restore your active creature's HP.",
                    "Switch creatures. The opponent will attack after the switch.",
                    battle.IsTrainerBattle
                        ? "Forfeit the battle. Counts as a loss — no badge, money or spoils."
                        : $"Attempt to escape. Current success chance: {battle.EscapeChance:P0}.",
                };
                Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 8f * scale),
                    $"What will {battle.Active.Name} do?", new Vector4(1f, 1f, 1f, 0.75f), TextStyles.Caption2);
                for (var i = 0; i < 4; i++)
                {
                    var cx = panel.Center.X + (i % 2 == 0 ? -1 : 1) * panel.Width * 0.24f;
                    var cy = panel.Min.Y + (i < 2 ? 0.32f : 0.7f) * panel.Height;
                    var size = new Vector2(panel.Width * 0.42f, panel.Height * 0.32f);
                    var button = CenteredAt(new Vector2(cx, cy), size);
                    if (RosterUi.ColorButton(button, quad[i], colors[i], scale, true))
                    {
                        OnRootAction(i);
                    }

                    if (ImGui.IsMouseHoveringRect(button.Min, button.Max))
                    {
                        ShowTooltip(hints[i]);
                    }
                }

                break;
            }
            case Menu.Fight:
                DrawMoveMenu(panel, theme, scale);
                break;
            case Menu.Item:
                DrawItemMenu(panel, theme, scale);
                break;
            case Menu.Switch:
                DrawSwitchMenu(panel, theme, scale);
                break;
        }
    }

    private void OnRootAction(int index)
    {
        switch (index)
        {
            case 0:
                menu = Menu.Fight;
                break;
            case 1:
                menu = Menu.Item;
                break;
            case 2:
                menu = Menu.Switch;
                break;
            case 3:
                confirmingRun = true;
                break;
        }
    }

    private void DrawRunConfirm(Rect panel, PhoneTheme theme, float scale)
    {
        var trainer = battle!.IsTrainerBattle;
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + panel.Height * 0.26f),
            trainer ? "Forfeit this battle?" : "Run away?", theme.TextStrong, TextStyles.Headline);
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + panel.Height * 0.46f),
            FitLabel(trainer ? "It counts as a loss — no badge, money or spoils." : "Give up on this wild encounter.",
                panel.Width - 24f * scale, TextStyles.Caption1), theme.TextStrong with { W = 0.8f },
            TextStyles.Caption1);

        var yes = CenteredAt(new Vector2(panel.Center.X - panel.Width * 0.22f, panel.Min.Y + panel.Height * 0.76f),
            new Vector2(panel.Width * 0.38f, panel.Height * 0.32f));
        if (RosterUi.ColorButton(yes, trainer ? "FORFEIT" : "RUN", RosterUi.Red, scale, true))
        {
            confirmingRun = false;
            battle.Run();
            menu = Menu.Root;
        }

        var no = CenteredAt(new Vector2(panel.Center.X + panel.Width * 0.22f, panel.Min.Y + panel.Height * 0.76f),
            new Vector2(panel.Width * 0.38f, panel.Height * 0.32f));
        if (RosterUi.ColorButton(no, "STAY", RosterUi.Blue, scale, true))
        {
            confirmingRun = false;
        }
    }

    private void DrawMoveMenu(Rect panel, PhoneTheme theme, float scale)
    {
        var moves = battle!.Active.Moves;
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 8f * scale), "Choose a move",
            new Vector4(1f, 1f, 1f, 0.75f), TextStyles.Caption2);
        if (battle.Active.Pp.All(value => value <= 0))
        {
            var recover = Centered(panel, 0.48f, new Vector2(panel.Width * 0.72f, panel.Height * 0.3f));
            if (RosterUi.ColorButton(recover, "Struggle", RosterUi.Red, scale, true,
                    sub: "50 power  |  recoil"))
            {
                battle.UseMove(-1);
                menu = Menu.Root;
            }

            if (ImGui.IsMouseHoveringRect(recover.Min, recover.Max))
            {
                ShowTooltip(BuildMoveTooltip(Moves.Struggle, battle.Active, battle.Wild, 1));
            }

            BackButton(panel, theme, scale);
            return;
        }

        for (var i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            var cx = panel.Center.X + (i % 2 == 0 ? -1 : 1) * panel.Width * 0.24f;
            var cy = panel.Min.Y + (i < 2 ? 0.3f : 0.62f) * panel.Height;
            var size = new Vector2(panel.Width * 0.44f, panel.Height * 0.28f);
            var rect = new Rect(new Vector2(cx, cy) - size * 0.5f, new Vector2(cx, cy) + size * 0.5f);
            var enabled = battle.Active.Pp[i] > 0;
            var fill = enabled ? Elements.Color(move.Element) : GamePalette.CellSunken;
            var effectiveness = move.IsStatus ? 1f : Elements.Effectiveness(move.Element, battle.Wild.Element,
                battle.Wild.SecondaryElement);
            if (DrawMoveButton(rect, move, battle.Active.Pp[i], effectiveness, fill, theme, enabled, scale))
            {
                battle.UseMove(i);
                menu = Menu.Root;
            }

            if (ImGui.IsMouseHoveringRect(rect.Min, rect.Max))
            {
                ShowTooltip(BuildMoveTooltip(move, battle.Active, battle.Wild, battle.Active.Pp[i]));
            }
        }

        BackButton(panel, theme, scale);
    }

    private bool DrawMoveButton(Rect rect, MoveDef move, int pp, float effectiveness, Vector4 fill, PhoneTheme theme,
        bool enabled, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var hovered = enabled && LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        var pressed = hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var radius = 10f * scale;

        if (!enabled)
        {
            Squircle.Fill(drawList, rect.Min, rect.Max, radius,
                ImGui.GetColorU32(GamePalette.Darken(RosterUi.CardBottom, 0.05f)));
            Squircle.Stroke(drawList, rect.Min, rect.Max, radius,
                ImGui.GetColorU32(RosterUi.CardEdge with { W = 0.35f }), 1.4f * scale);
        }
        else
        {
            // The chunky button chrome from the UI Update kit: drop shadow, dark outline, top shine.
            drawList.AddRectFilled(rect.Min + new Vector2(0f, 2f * scale), rect.Max + new Vector2(0f, 2f * scale),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.20f)), radius);
            Squircle.FillVerticalGradient(drawList, rect.Min, rect.Max, radius,
                ImGui.GetColorU32(GamePalette.Lighten(fill, pressed ? 0.03f : hovered ? 0.16f : 0.11f)),
                ImGui.GetColorU32(GamePalette.Darken(fill, pressed ? 0.24f : hovered ? 0.08f : 0.12f)));
            Squircle.Stroke(drawList, rect.Min, rect.Max, radius, ImGui.GetColorU32(RosterUi.NavyEdge),
                1.6f * scale);
            drawList.AddLine(new Vector2(rect.Min.X + radius, rect.Min.Y + 2f * scale),
                new Vector2(rect.Max.X - radius, rect.Min.Y + 2f * scale),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, hovered ? 0.4f : 0.3f)), 1.2f * scale);
        }

        var ink = enabled ? GamePalette.InkOn(fill) : RosterUi.CardMuted;
        var x = rect.Min.X + 10f * scale;
        var right = rect.Max.X - 10f * scale;
        var titleStyle = TextStyles.Headline;
        var metaStyle = TextStyles.Caption1;
        var badgeStyle = TextStyles.Caption2;
        var title = FitLabel(move.Name, rect.Width - 20f * scale, titleStyle);
        Typography.DrawCentered(new Vector2(rect.Center.X, rect.Min.Y + 16f * scale), title, ink,
            titleStyle);

        var typeText = FitLabel(Elements.Name(move.Element), rect.Width * 0.42f, metaStyle);
        Typography.Draw(new Vector2(x, rect.Min.Y + 33f * scale), typeText, ink with { W = 0.84f }, metaStyle);

        // "Physical" is the longest category label; the old proportional cap clipped it to
        // "Physi..." on narrow phone cards. Reserve a real minimum width for the right column.
        var categoryText = FitLabel(move.CategoryLabel, MathF.Max(68f * scale, rect.Width * 0.42f), metaStyle);
        var categorySize = Typography.Measure(categoryText, metaStyle);
        Typography.Draw(new Vector2(right - categorySize.X, rect.Min.Y + 33f * scale), categoryText,
            ink with { W = 0.8f }, metaStyle);

        var ppText = $"PP {pp}/{move.Pp}";
        var ppSize = Typography.Measure(ppText, metaStyle);
        Typography.Draw(new Vector2(right - ppSize.X, rect.Max.Y - 18f * scale), ppText, ink with { W = 0.9f },
            metaStyle);

        var matchup = MoveMatchupLabel(effectiveness, move.IsStatus);
        if (matchup.Length > 0)
        {
            var badgeSize = Typography.Measure(matchup, badgeStyle);
            var badgeMin = new Vector2(x, rect.Max.Y - 19f * scale);
            var badgeMax = badgeMin + new Vector2(badgeSize.X + 10f * scale, 15f * scale);
            var badgeColor = effectiveness switch
            {
                0f => GamePalette.CellSunken,
                > 1f => new Vector4(1f, 0.72f, 0.25f, 1f),
                < 1f => new Vector4(0.54f, 0.72f, 0.92f, 1f),
                _ => Accent,
            };
            Squircle.Fill(drawList, badgeMin, badgeMax, 7f * scale, ImGui.GetColorU32(badgeColor with { W = 0.32f }));
            Typography.Draw(new Vector2(badgeMin.X + 5f * scale, badgeMin.Y + 1f * scale), matchup,
                GamePalette.Lighten(badgeColor, 0.28f), badgeStyle);
        }

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        return hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
    }

    private static string MoveMatchupLabel(float effectiveness, bool isStatus)
    {
        if (isStatus)
        {
            return string.Empty;
        }

        return effectiveness switch
        {
            0f => "IMMUNE",
            > 1f => "STRONG",
            < 1f => "RESIST",
            _ => string.Empty,
        };
    }

    private float battleItemScroll;

    private void DrawItemMenu(Rect panel, PhoneTheme theme, float scale)
    {
        var owned = State.Bag.Contents().ToList();
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 8f * scale), "Choose an item",
            new Vector4(1f, 1f, 1f, 0.75f), TextStyles.Caption2);

        if (owned.Count == 0)
        {
            Typography.DrawCentered(new Vector2(panel.Center.X, panel.Center.Y - 6f * scale),
                "Your bag is empty. Restock at a town Marketboard.", RosterUi.CardMuted, TextStyles.Subheadline);
            BackButton(panel, theme, scale);
            return;
        }

        var listArea = new Rect(new Vector2(panel.Min.X + 10f * scale, panel.Min.Y + 24f * scale),
            new Vector2(panel.Max.X - 10f * scale, panel.Max.Y - 30f * scale));
        DrawScrollList(listArea, 36f * scale, 6f * scale, owned.Count, ref battleItemScroll, scale,
            (i, rowRect) => DrawBattleItemRow(owned[i].Def, owned[i].Count, rowRect, theme, scale));

        BackButton(panel, theme, scale);
    }

    private void DrawBattleItemRow(ItemDef item, int count, Rect rect, PhoneTheme theme, float scale)
    {
        var enabled = battle!.CanUseItem(item);
        var tint = LgUi.ItemTint(item.Category);
        // Leave room on the left for the item sprite, mirroring the bag layout.
        if (RosterUi.ColorButton(rect, "      " + item.Name, tint, scale, enabled,
                sub: BattleItemSub(item, count)))
        {
            if (item.Category == ItemCategory.Ball)
            {
                pendingCaptureBallId = item.Id; // remembered so the throw animation shows the right ball
            }

            battle.UseItem(item);
            menu = Menu.Root;
        }

        var drawList = ImGui.GetWindowDrawList();
        var iconCenter = new Vector2(rect.Min.X + rect.Height * 0.55f, rect.Center.Y);
        LgUi.ItemIcon(drawList, iconCenter, rect.Height * 0.7f, item);

        if (LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max))
        {
            ShowTooltip(BuildBattleItemTooltip(item));
        }
    }

    private string BattleItemSub(ItemDef item, int count) => item.Category switch
    {
        ItemCategory.Ball => battle!.IsTrainerBattle
            ? $"x{count}   ·   can't catch"
            : $"x{count}   ·   {battle.CaptureChanceWith(item):P0} catch",
        ItemCategory.Potion => $"x{count}   ·   {(item.RestoresFullHp ? "full" : "+" + item.HealAmount)} HP",
        ItemCategory.Revive => $"x{count}   ·   revive a fainted ally",
        ItemCategory.StatusHeal => $"x{count}   ·   {(item.CuresAllStatus ? "cure any status" : "cure " + StatusWord(item.CuresStatus))}",
        _ => "x" + count,
    };

    private string BuildBattleItemTooltip(ItemDef item)
    {
        var line = item.Category switch
        {
            ItemCategory.Ball => battle!.IsTrainerBattle
                ? "You can't catch another trainer's Pokémon!"
                : $"Estimated catch chance on {battle.Wild.Name}: {battle.CaptureChanceWith(item):P0}.\n" +
                  "Lower its HP or inflict a status to improve the odds.",
            ItemCategory.Potion => battle!.CanUseItem(item)
                ? $"Restores {(item.RestoresFullHp ? "all" : item.HealAmount.ToString())} HP to {battle.Active.Name}."
                : $"{battle.Active.Name} is already at full HP.",
            ItemCategory.Revive => battle!.CanUseItem(item)
                ? "Revives your first fainted creature (it stays on the bench)."
                : "None of your creatures have fainted.",
            ItemCategory.StatusHeal => battle!.CanUseItem(item)
                ? $"Cures {battle.Active.Name}'s condition."
                : $"{battle.Active.Name} has no matching condition to cure.",
            _ => item.Description,
        };
        return $"{item.Name}\n{ItemStatsLine(item)}\n\n{item.Description}\n\n{line}\nUsing an item takes your turn.";
    }

    private static string StatusWord(Status status) => status switch
    {
        Status.Burn => "burn",
        Status.Freeze => "freeze",
        Status.Sleep => "sleep",
        Status.Paralysis => "paralysis",
        Status.Poison => "poison",
        _ => "status",
    };

    private void DrawMoveLearnMenu(Rect panel, PhoneTheme theme, float scale)
    {
        if (battle?.PendingMoveChoice is not { } choice)
        {
            return;
        }

        var title = FitLabel($"Learn {choice.Move.Name}?", panel.Width - 32f * scale, TextStyles.SubheadlineEmphasized);
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 10f * scale), title, RosterUi.CardInk,
            TextStyles.SubheadlineEmphasized);
        var instruction = FitLabel($"Choose a move for {choice.Monster.Name} to forget",
            panel.Width - 32f * scale, TextStyles.Caption2);
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 27f * scale), instruction,
            RosterUi.CardMuted, TextStyles.Caption2);

        var headerRect = new Rect(new Vector2(panel.Min.X + 10f * scale, panel.Min.Y + 2f * scale),
            new Vector2(panel.Max.X - 10f * scale, panel.Min.Y + 34f * scale));
        if (ImGui.IsMouseHoveringRect(headerRect.Min, headerRect.Max))
        {
            ShowTooltip(BuildProfileMoveTooltip(choice.Move, choice.Move.Pp));
        }

        for (var i = 0; i < choice.Monster.Moves.Count; i++)
        {
            var move = choice.Monster.Moves[i];
            var column = i % 2;
            var row = i / 2;
            var size = new Vector2(panel.Width * 0.43f, panel.Height * 0.21f);
            var center = new Vector2(panel.Center.X + (column == 0 ? -1f : 1f) * panel.Width * 0.235f,
                panel.Min.Y + panel.Height * (0.42f + row * 0.25f));
            var rect = new Rect(center - size * 0.5f, center + size * 0.5f);
            var label = FitLabel(move.Name, size.X - 12f * scale, TextStyles.Subheadline);
            if (RosterUi.ColorButton(rect, label, Elements.Color(move.Element), scale, true,
                    sub: $"{Elements.Name(move.Element)}  {choice.Monster.Pp[i]}/{move.Pp} PP"))
            {
                battle.ResolveMoveChoice(i);
                return;
            }

            if (ImGui.IsMouseHoveringRect(rect.Min, rect.Max))
            {
                ShowTooltip(BuildProfileMoveTooltip(move, choice.Monster.Pp[i]));
            }
        }

        var keepRect = Centered(panel, 0.89f, new Vector2(panel.Width * 0.52f, panel.Height * 0.14f));
        if (RosterUi.BlueButton(keepRect, "Keep current moves", scale, true))
        {
            battle.ResolveMoveChoice(null);
        }
    }

    private void DrawSwitchMenu(Rect panel, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var forced = battle!.RequiresSwitch;
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 8f * scale),
            forced ? "Choose your next creature" : "Switch creature", new Vector4(1f, 1f, 1f, 0.75f),
            TextStyles.Caption2);
        var count = 0;
        for (var i = 0; i < State.Party.Count; i++)
        {
            if (i == battle!.ActiveIndex)
            {
                continue;
            }

            var m = State.Party[i];
            var column = count % 2;
            var row = count / 2;
            var size = new Vector2(panel.Width * 0.42f, panel.Height * 0.24f);
            var center = new Vector2(panel.Center.X + (column == 0 ? -1f : 1f) * panel.Width * 0.235f,
                panel.Min.Y + (0.24f + row * 0.25f) * panel.Height);
            var rect = new Rect(center - size * 0.5f, center + size * 0.5f);
            if (RosterUi.ColorButton(rect, "     " + m.Name, Elements.Color(m.Element), scale, !m.Fainted,
                    sub: $"Lv {m.Level}   {m.CurrentHp}/{m.MaxHp} HP"))
            {
                battle.Switch(i);
                menu = Menu.Root;
            }

            // Animated sprite tucked into the button's left edge for quick recognition.
            var spriteCenter = new Vector2(rect.Min.X + rect.Height * 0.5f, rect.Center.Y);
            MonsterArt.Draw(drawList, spriteCenter, rect.Height * 0.34f, m.Species, 1f,
                new MonsterPose(time + i, 0f, 0f, m.Fainted ? 0.4f : 1f, m.Fainted));

            if (ImGui.IsMouseHoveringRect(rect.Min, rect.Max))
            {
                ShowTooltip(BuildMonsterTooltip(m, m.Fainted ? "This creature has fainted and cannot switch in." :
                    forced ? "Choose this creature to continue the battle." : "Switching uses your turn."));
            }

            count++;
        }

        if (count == 0)
        {
            Typography.DrawCentered(new Vector2(panel.Center.X, panel.Center.Y - 6f * scale),
                "No other creature can battle.", RosterUi.CardMuted, TextStyles.Subheadline);
        }

        if (!forced)
        {
            BackButton(panel, theme, scale);
        }
    }

    // The win / loss / forfeit screen, drawn inside the battle's bottom message box (not full-screen).
    // The arena and creatures stay visible above; this panel replaces the action menu once the battle
    // is decided. Emblems, lettering and reward icons are bundled art under Assets/pokemon/result.
    private void DrawResult(Rect panel, PhoneTheme theme, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var win = new Vector4(0.40f, 0.80f, 0.46f, 1f);
        var gold = new Vector4(0.93f, 0.76f, 0.36f, 1f);
        var xpBlue = new Vector4(0.44f, 0.70f, 0.95f, 1f);

        var outcome = battle!.Outcome;
        var won = outcome == BattleOutcome.Won;
        var captured = outcome == BattleOutcome.Captured;
        var whiteout = outcome == BattleOutcome.Whiteout;
        var forfeit = outcome == BattleOutcome.Fled && battle.IsTrainerBattle;
        var escaped = outcome == BattleOutcome.Fled && !battle.IsTrainerBattle;
        var positive = won || captured;
        var defeated = whiteout || forfeit;
        var accent = positive ? gold : defeated ? theme.Danger : new Vector4(0.62f, 0.64f, 0.72f, 1f);

        // Entrance: fade the contents in over a short beat.
        if (resultShownAt < 0f)
        {
            resultShownAt = time;
        }

        var ease = 1f - MathF.Pow(1f - Math.Clamp((time - resultShownAt) / 0.3f, 0f, 1f), 3f);

        var w = panel.Width;
        var h = panel.Height;
        var pad = 8f * scale;
        float Y(float f) => panel.Min.Y + h * f;

        // Panel surface: the navy combat box, tinted by outcome.
        DrawCombatBox(dl, panel.Min, panel.Max, scale);
        ResultTintWash(dl, panel, accent, Y(0.6f), positive ? 0.16f : 0.12f);
        Squircle.Stroke(dl, panel.Min, panel.Max, 14f * scale, ImGui.GetColorU32(accent with { W = 0.5f }),
            1.3f * scale);
        DrawResultCorners(dl, panel, ImGui.GetColorU32(accent with { W = 0.8f }), 16f * scale, 2f * scale,
            8f * scale);

        // Sparkles behind a win.
        if (positive)
        {
            for (var i = 0; i < 12; i++)
            {
                var ph = time * 1.4f + i * 2.61f;
                var sx = panel.Min.X + (0.08f + 0.84f * ((i * 0.137f + time * 0.04f) % 1f)) * w;
                var sy = Y(0.08f + 0.5f * ((i * 0.29f) % 1f));
                var tw = 0.5f + 0.5f * MathF.Sin(ph);
                dl.AddCircleFilled(new Vector2(sx, sy), (0.8f + tw * 1.4f) * scale,
                    ImGui.GetColorU32((i % 2 == 0 ? gold : win) with { W = (0.18f + tw * 0.32f) * ease }));
            }
        }

        var buttonCol = positive ? win : defeated ? theme.Danger : new Vector4(0.30f, 0.34f, 0.42f, 1f);
        var button = CenteredAt(new Vector2(panel.Center.X, Y(0.82f)), new Vector2(w * 0.52f, h * 0.24f));

        // A neutral wild escape gets a simple, centred treatment (no bundled "escaped" artwork).
        if (escaped)
        {
            ProgressRing.CenterIcon(dl, new Vector2(panel.Center.X, Y(0.24f)), FontAwesomeIcon.Walking,
                accent with { W = ease }, h * 0.2f);
            DrawResultHeadline(dl, new Vector2(panel.Center.X, Y(0.46f)), "Got away safely!", accent, TextStyles.Title2,
                true, ease);
            Typography.DrawCentered(new Vector2(panel.Center.X, Y(0.6f)),
                FitLabel("You slipped out of the encounter.", w - 24f * scale, TextStyles.Callout),
                RosterUi.CardMuted with { W = RosterUi.CardMuted.W * ease }, TextStyles.Callout);
            if (RosterUi.ColorButton(button, "CONTINUE", buttonCol, scale, true))
            {
                FinishBattle();
            }

            return;
        }

        // Left column: the hero emblem. Right column: title, subtitle and rewards.
        var emblemZone = new Rect(new Vector2(panel.Min.X + pad, Y(0.06f)),
            new Vector2(panel.Min.X + w * 0.34f, Y(0.66f)));
        if (won || defeated)
        {
            ResultAsset(dl, won ? "result/victory_emblem.png" : "result/defeat_emblem.png", emblemZone, ease);
        }
        else // captured — show the caught creature as the hero.
        {
            var caught = battle.Captured ?? battle.Wild;
            var ec = emblemZone.Center;
            ProgressRing.Glow(ec, emblemZone.Height * 0.46f, Elements.Color(caught.Element), 0.5f * ease);
            MonsterArt.Draw(dl, ec, emblemZone.Height * 0.72f, caught.Species, 1f, MonsterPose.Idle(time));
        }

        var x0 = panel.Min.X + w * 0.37f;
        var contentW = panel.Max.X - x0 - pad;

        // Title: bundled lettering for a win or a true defeat; drawn text for a catch or a forfeit
        // (which read differently from a loss and have no bundled artwork).
        float titleBottom;
        if (won)
        {
            titleBottom = ResultAsset(dl, "result/victory_title.png", x0, Y(0.1f), contentW, h * 0.3f, ease);
        }
        else if (whiteout)
        {
            titleBottom = ResultAsset(dl, "result/defeat_title.png", x0, Y(0.1f), contentW, h * 0.3f, ease);
        }
        else
        {
            titleBottom = Y(0.1f) + h * 0.26f;
            var (word, wordCol) = forfeit ? ("Forfeit", accent) : ("Caught!", gold);
            DrawResultHeadline(dl, new Vector2(x0, Y(0.1f) + h * 0.13f), word, wordCol, TextStyles.Title1, false, ease);
        }

        // Subtitle.
        var subtitle = outcome switch
        {
            BattleOutcome.Captured when State.Party.Count >= LillypadGoState.PartyLimit =>
                $"{battle.Wild.Name} was sent to storage.",
            BattleOutcome.Captured => $"{battle.Wild.Name} joined your team!",
            BattleOutcome.Won when battle.IsTrainerBattle => $"You defeated {battle.TrainerName}!",
            BattleOutcome.Won => "",
            BattleOutcome.Whiteout => "You were unable to win.",
            _ => "You fled from the battle.",
        };
        var subY = titleBottom + 5f * scale;
        Typography.Draw(new Vector2(x0, subY),
            FitLabel(subtitle, contentW, TextStyles.Callout),
            theme.TextStrong with { W = 0.9f * ease }, TextStyles.Callout);
        subY += 17f * scale;
        if (whiteout)
        {
            Typography.Draw(new Vector2(x0, subY), "But don't give up!",
                accent with { W = 0.95f * ease }, TextStyles.FootnoteEmphasized);
            subY += 16f * scale;
        }

        // Rewards row (wins) or a "no rewards" note (defeats / forfeits).
        var rowY = subY + 12f * scale;
        if (won)
        {
            var cx = x0;
            if (battle.PrizeMoney > 0)
            {
                cx += ResultRewardChip(dl, cx, rowY, "result/coin.png", FontAwesomeIcon.Coins,
                    LgUi.Money(battle.PrizeMoney), gold, theme, scale, ease) + 8f * scale;
            }

            if (battle.XpGained > 0)
            {
                ResultRewardChip(dl, cx, rowY, "result/star.png", FontAwesomeIcon.Star,
                    $"+{battle.XpGained} XP", xpBlue, theme, scale, ease);
            }
        }
        else if (defeated)
        {
            var sfBox = new Rect(new Vector2(x0, rowY - 11f * scale), new Vector2(x0 + 22f * scale, rowY + 11f * scale));
            if (!ResultAsset(dl, "result/sadface.png", sfBox, ease))
            {
                ProgressRing.CenterIcon(dl, sfBox.Center, FontAwesomeIcon.HeartBroken, theme.TextMuted, 18f * scale);
            }

            Typography.Draw(new Vector2(x0 + 28f * scale, rowY - 7f * scale), "You received no rewards.",
                theme.TextMuted, TextStyles.Caption1);
        }
        else if (captured)
        {
            ProgressRing.CenterIcon(dl, new Vector2(x0 + 10f * scale, rowY), FontAwesomeIcon.Check, win, 15f * scale);
            Typography.Draw(new Vector2(x0 + 24f * scale, rowY - 7f * scale), "New teammate added.",
                win with { W = 0.95f }, TextStyles.Caption1);
        }

        if (RosterUi.ColorButton(button, defeated ? "RETREAT" : "CONTINUE", buttonCol, scale, true))
        {
            FinishBattle();
        }
    }

    // A downward accent wash from the top of the panel, faded out by `midY`.
    private static void ResultTintWash(ImDrawListPtr dl, Rect panel, Vector4 accent, float midY, float strength)
    {
        var top = ImGui.GetColorU32(accent with { W = strength });
        var clear = ImGui.GetColorU32(accent with { W = 0f });
        dl.AddRectFilledMultiColor(panel.Min, new Vector2(panel.Max.X, midY), top, top, clear, clear);
    }

    // Draws a bundled result texture centred and aspect-fit inside `box`. Returns false if not yet loaded.
    private static bool ResultAsset(ImDrawListPtr dl, string path, Rect box, float alpha)
    {
        if (!AssetTextures.TryGet(path, out var tex, out var aspect))
        {
            return false;
        }

        var w = box.Width;
        var hh = w / MathF.Max(0.01f, aspect);
        if (hh > box.Height)
        {
            hh = box.Height;
            w = hh * aspect;
        }

        var c = box.Center;
        dl.AddImage(tex, new Vector2(c.X - w * 0.5f, c.Y - hh * 0.5f), new Vector2(c.X + w * 0.5f, c.Y + hh * 0.5f),
            Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)));
        return true;
    }

    // Left-aligned, aspect-fit texture starting at (x, top). Returns the y just below it (reserving
    // `maxH` until the texture streams in so the layout stays stable).
    private static float ResultAsset(ImDrawListPtr dl, string path, float x, float top, float maxW, float maxH,
        float alpha)
    {
        if (!AssetTextures.TryGet(path, out var tex, out var aspect))
        {
            return top + maxH;
        }

        var w = maxW;
        var hh = w / MathF.Max(0.01f, aspect);
        if (hh > maxH)
        {
            hh = maxH;
            w = hh * aspect;
        }

        dl.AddImage(tex, new Vector2(x, top), new Vector2(x + w, top + hh), Vector2.Zero, Vector2.One,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)));
        return top + hh;
    }

    // A reward pill: tinted icon (bundled art, else a glyph) beside its value. Returns the pill width.
    private float ResultRewardChip(ImDrawListPtr dl, float x, float centerY, string iconPath,
        FontAwesomeIcon fallback, string value, Vector4 tint, PhoneTheme theme, float scale, float alpha)
    {
        var chipH = 23f * scale;
        var iconSz = 18f * scale;
        var valSize = Typography.Measure(value, TextStyles.SubheadlineEmphasized);
        var chipW = 8f * scale + iconSz + 5f * scale + valSize.X + 12f * scale;
        var min = new Vector2(x, centerY - chipH * 0.5f);
        var max = new Vector2(x + chipW, centerY + chipH * 0.5f);
        Squircle.Fill(dl, min, max, chipH * 0.5f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.06f)));
        Squircle.Stroke(dl, min, max, chipH * 0.5f, ImGui.GetColorU32(tint with { W = 0.55f }), 1f * scale);
        var iconBox = new Rect(new Vector2(min.X + 8f * scale, min.Y), new Vector2(min.X + 8f * scale + iconSz, max.Y));
        if (!ResultAsset(dl, iconPath, iconBox, alpha))
        {
            ProgressRing.CenterIcon(dl, iconBox.Center, fallback, tint, iconSz * 0.9f);
        }

        Typography.Draw(new Vector2(iconBox.Max.X + 5f * scale, centerY - valSize.Y * 0.5f), value, theme.TextStrong,
            TextStyles.SubheadlineEmphasized);
        return chipW;
    }

    // A bold headline with a drop shadow; centred when `center` is true, else left-aligned at `pos`.
    private static void DrawResultHeadline(ImDrawListPtr dl, Vector2 pos, string text, Vector4 color,
        in TextStyle style, bool center, float alpha)
    {
        var shadow = new Vector4(0f, 0f, 0f, 0.5f * alpha);
        var tint = color with { W = color.W * alpha };
        if (center)
        {
            Typography.DrawCentered(pos + new Vector2(0f, 2f), text, shadow, style);
            Typography.DrawCentered(pos, text, tint, style);
        }
        else
        {
            var half = Typography.Measure(text, style).Y * 0.5f;
            Typography.Draw(new Vector2(pos.X + 1.5f, pos.Y - half + 2f), text, shadow, style);
            Typography.Draw(new Vector2(pos.X, pos.Y - half), text, tint, style);
        }
    }

    // Ornamental L-brackets in each corner of the result panel.
    private static void DrawResultCorners(ImDrawListPtr dl, Rect r, uint col, float len, float thick, float inset)
    {
        var corners = new[]
        {
            (P: new Vector2(r.Min.X + inset, r.Min.Y + inset), Dx: 1f, Dy: 1f),
            (P: new Vector2(r.Max.X - inset, r.Min.Y + inset), Dx: -1f, Dy: 1f),
            (P: new Vector2(r.Min.X + inset, r.Max.Y - inset), Dx: 1f, Dy: -1f),
            (P: new Vector2(r.Max.X - inset, r.Max.Y - inset), Dx: -1f, Dy: -1f),
        };
        foreach (var (p, dx, dy) in corners)
        {
            dl.AddLine(p, p + new Vector2(len * dx, 0f), col, thick);
            dl.AddLine(p, p + new Vector2(0f, len * dy), col, thick);
        }
    }


    private void FinishBattle()
    {
        if (battle is null)
        {
            return;
        }

        battle.FinalizeStats();

        if (battle.Outcome == BattleOutcome.Captured && battle.Captured is { } caught)
        {
            State.Captures++;
            State.AddCaught(caught);
        }

        if (battle.Outcome == BattleOutcome.Won)
        {
            State.BattlesWon++;
            State.Money += battle.PrizeMoney;
            if (pendingGymIndex >= 0)
            {
                State.EarnBadge(pendingGymIndex);
            }
        }

        pendingGymIndex = -1;

        // Undo any Transform (Ditto) so nothing is stored as its copied form, and register evolved
        // forms as seen.
        foreach (var m in State.Party)
        {
            m.RevertTransform();
            State.Seen.Add(m.Species.Id);
        }

        // A whiteout no longer auto-heals: the team stays fainted until revived at a town
        // Marketboard's Pokécenter counter.
        State.InBattle = false;
        State.Save();
        battle = null;
        displayedPlayer = null;
        sendOutFx = null;
        enemyAwaitingSendOut = false;
        battlePopups.Clear();
        moveFx = null;
        awaitingResult = false;
        resultShownAt = -1f;
        view = View.Map;
    }

}
