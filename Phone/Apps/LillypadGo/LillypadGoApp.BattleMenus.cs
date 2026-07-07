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
        Squircle.Fill(drawList, bedMin, bedMax, 10f * scale, ImGui.GetColorU32(new Vector4(0.02f, 0.025f, 0.035f, 0.46f)));
        Squircle.Stroke(drawList, bedMin, bedMax, 10f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.06f)),
            1f * scale);

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

        switch (menu)
        {
            case Menu.Root:
            {
                var quad = new[] { "Fight", "Bag", "Team", "Run" };
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
                    $"What will {battle.Active.Name} do?", theme.TextMuted, TextStyles.Caption2);
                for (var i = 0; i < 4; i++)
                {
                    var cx = panel.Center.X + (i % 2 == 0 ? -1 : 1) * panel.Width * 0.24f;
                    var cy = panel.Min.Y + (i < 2 ? 0.32f : 0.7f) * panel.Height;
                    var size = new Vector2(panel.Width * 0.42f, panel.Height * 0.32f);
                    var accent = i == 0 ? Accent : i == 3 ? theme.Danger : theme.Accent;
                    var button = CenteredAt(new Vector2(cx, cy), size);
                    if (LgUi.Button(button, quad[i], accent, theme, true))
                    {
                        OnRootAction(i);
                    }

                    if (ImGui.IsMouseHoveringRect(button.Min, button.Max))
                    {
                        ImGui.SetTooltip(hints[i]);
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
                battle!.Run();
                break;
        }
    }

    private void DrawMoveMenu(Rect panel, PhoneTheme theme, float scale)
    {
        var moves = battle!.Active.Moves;
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 8f * scale), "Choose a move",
            theme.TextMuted, TextStyles.Caption2);
        if (battle.Active.Pp.All(value => value <= 0))
        {
            var recover = Centered(panel, 0.48f, new Vector2(panel.Width * 0.72f, panel.Height * 0.3f));
            if (LgUi.Button(recover, "Struggle", theme.Accent, theme, true, "50 power  |  recoil"))
            {
                battle.UseMove(-1);
                menu = Menu.Root;
            }

            if (ImGui.IsMouseHoveringRect(recover.Min, recover.Max))
            {
                ImGui.SetTooltip(BuildMoveTooltip(Moves.Struggle, battle.Active, battle.Wild, 1));
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
                ImGui.SetTooltip(BuildMoveTooltip(move, battle.Active, battle.Wild, battle.Active.Pp[i]));
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
            Squircle.Fill(drawList, rect.Min, rect.Max, radius, ImGui.GetColorU32(GamePalette.CellSunken));
            Squircle.Stroke(drawList, rect.Min, rect.Max, radius, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.05f)),
                1f * scale);
        }
        else
        {
            // Same hover language as LgUi.Button: a contained lift + brighter edge, no glow blob.
            if (hovered && !pressed)
            {
                Elevation.Draw(drawList, rect.Min, rect.Max, radius, scale, 9f, 3f, 0.22f);
            }

            Squircle.FillVerticalGradient(drawList, rect.Min, rect.Max, radius,
                ImGui.GetColorU32(GamePalette.Lighten(fill, pressed ? 0.03f : hovered ? 0.15f : 0.12f)),
                ImGui.GetColorU32(GamePalette.Darken(fill, pressed ? 0.24f : hovered ? 0.10f : 0.14f)));
            Squircle.Stroke(drawList, rect.Min, rect.Max, radius,
                ImGui.GetColorU32(GamePalette.Lighten(fill, 0.38f) with { W = hovered ? 0.85f : 0.55f }), 1f * scale);
            drawList.AddLine(new Vector2(rect.Min.X + radius, rect.Min.Y + 1f * scale),
                new Vector2(rect.Max.X - radius, rect.Min.Y + 1f * scale),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, hovered ? 0.32f : 0.22f)), 1f * scale);
        }

        var ink = enabled ? GamePalette.InkOn(fill) : theme.TextMuted;
        var x = rect.Min.X + 10f * scale;
        var right = rect.Max.X - 10f * scale;
        var titleStyle = TextStyles.Headline;
        var metaStyle = TextStyles.Caption1;
        var badgeStyle = TextStyles.Caption2;
        var title = FitLabel(move.Name, rect.Width - 20f * scale, titleStyle);
        Typography.DrawCentered(new Vector2(rect.Center.X, rect.Min.Y + 16f * scale), title, ink,
            titleStyle);

        var typeText = FitLabel(Elements.Name(move.Element), rect.Width * 0.48f, metaStyle);
        Typography.Draw(new Vector2(x, rect.Min.Y + 33f * scale), typeText, ink with { W = 0.84f }, metaStyle);

        var categoryText = FitLabel(move.CategoryLabel, rect.Width * 0.4f, metaStyle);
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
            theme.TextMuted, TextStyles.Caption2);

        if (owned.Count == 0)
        {
            Typography.DrawCentered(new Vector2(panel.Center.X, panel.Center.Y - 6f * scale),
                "Your bag is empty. Restock at a town Marketboard.", theme.TextMuted, TextStyles.Subheadline);
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
        if (LgUi.Button(rect, item.Name, enabled ? tint : GamePalette.CellSunken, theme, enabled,
                BattleItemSub(item, count)))
        {
            battle.UseItem(item);
            menu = Menu.Root;
        }

        if (LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max))
        {
            ImGui.SetTooltip(BuildBattleItemTooltip(item));
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
        return $"{item.Name}\n{item.Description}\n\n{line}\nUsing an item takes your turn.";
    }

    private static string StatusWord(Status status) => status switch
    {
        Status.Burn => "burn",
        Status.Freeze => "freeze",
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
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 10f * scale), title, theme.TextStrong,
            TextStyles.SubheadlineEmphasized);
        var instruction = FitLabel($"Choose a move for {choice.Monster.Name} to forget",
            panel.Width - 32f * scale, TextStyles.Caption2);
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 27f * scale), instruction,
            theme.TextMuted, TextStyles.Caption2);

        var headerRect = new Rect(new Vector2(panel.Min.X + 10f * scale, panel.Min.Y + 2f * scale),
            new Vector2(panel.Max.X - 10f * scale, panel.Min.Y + 34f * scale));
        if (ImGui.IsMouseHoveringRect(headerRect.Min, headerRect.Max))
        {
            ImGui.SetTooltip(BuildProfileMoveTooltip(choice.Move, choice.Move.Pp));
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
            if (LgUi.Button(rect, label, Elements.Color(move.Element), theme, true,
                    $"{Elements.Name(move.Element)}  {choice.Monster.Pp[i]}/{move.Pp} PP"))
            {
                battle.ResolveMoveChoice(i);
                return;
            }

            if (ImGui.IsMouseHoveringRect(rect.Min, rect.Max))
            {
                ImGui.SetTooltip(BuildProfileMoveTooltip(move, choice.Monster.Pp[i]));
            }
        }

        var keepRect = Centered(panel, 0.89f, new Vector2(panel.Width * 0.52f, panel.Height * 0.14f));
        if (LgUi.Button(keepRect, "Keep current moves", GamePalette.Cell, theme, true))
        {
            battle.ResolveMoveChoice(null);
        }
    }

    private void DrawSwitchMenu(Rect panel, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var forced = battle!.RequiresSwitch;
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + 8f * scale),
            forced ? "Choose your next creature" : "Switch creature", theme.TextStrong with { W = 0.82f },
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
            if (LgUi.Button(rect, "     " + m.Name,
                    m.Fainted ? GamePalette.CellSunken : Elements.Color(m.Element), theme, !m.Fainted,
                    $"Lv {m.Level}   {m.CurrentHp}/{m.MaxHp} HP"))
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
                ImGui.SetTooltip(BuildMonsterTooltip(m, m.Fainted ? "This creature has fainted and cannot switch in." :
                    forced ? "Choose this creature to continue the battle." : "Switching uses your turn."));
            }

            count++;
        }

        if (count == 0)
        {
            Typography.DrawCentered(new Vector2(panel.Center.X, panel.Center.Y - 6f * scale),
                "No other creature can battle.", theme.TextMuted, TextStyles.Subheadline);
        }

        if (!forced)
        {
            BackButton(panel, theme, scale);
        }
    }

    private void DrawResult(Rect panel, PhoneTheme theme, float scale)
    {
        var win = new Vector4(0.42f, 0.86f, 0.5f, 1f);
        var gym = pendingGymIndex >= 0 ? Gyms.All[pendingGymIndex] : null;
        var (title, color) = battle!.Outcome switch
        {
            BattleOutcome.Captured when State.Party.Count >= LillypadGoState.PartyLimit =>
                ($"{battle.Wild.Name} was caught!", win),
            BattleOutcome.Captured => ($"{battle.Wild.Name} joined your team!", win),
            BattleOutcome.Won when battle.IsTrainerBattle => ($"You defeated {battle.TrainerName}!", win),
            BattleOutcome.Won => ("You won the battle!", win),
            BattleOutcome.Fled when battle.IsTrainerBattle => ("You forfeited the battle.", theme.Danger),
            BattleOutcome.Fled => ("Got away safely.", theme.TextStrong with { W = 0.85f }),
            BattleOutcome.Whiteout => ("Your team was wiped out!", theme.Danger),
            _ => ("…", theme.TextStrong),
        };
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + panel.Height * 0.34f), title, color,
            TextStyles.Headline);
        var detail = battle.Outcome switch
        {
            BattleOutcome.Won when gym is not null && !State.HasBadge(gym.Index) =>
                $"You earned the {gym.Badge}!  (+{LgUi.Money(battle.PrizeMoney)})",
            BattleOutcome.Won => $"Earned {LgUi.Money(battle.PrizeMoney)}.",
            BattleOutcome.Captured when State.Party.Count >= LillypadGoState.PartyLimit => "Sent safely to storage.",
            BattleOutcome.Captured => "Added to your active team.",
            BattleOutcome.Fled when battle.IsTrainerBattle => "No badge, money or spoils for forfeiting.",
            BattleOutcome.Whiteout => "Revive your team at a town Marketboard.",
            _ => string.Empty,
        };
        if (detail.Length > 0)
        {
            Typography.DrawCentered(new Vector2(panel.Center.X, panel.Min.Y + panel.Height * 0.5f), detail,
                theme.TextMuted, TextStyles.Caption1);
        }

        var continueButton = CenteredAt(new Vector2(panel.Center.X, panel.Min.Y + panel.Height * 0.72f),
            new Vector2(panel.Width * 0.5f, panel.Height * 0.3f));
        if (LgUi.Button(continueButton, "Continue", Accent, theme, true))
        {
            FinishBattle();
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

        // A creature may have evolved mid-battle — make sure its new form is registered as seen.
        foreach (var m in State.Party)
        {
            State.Seen.Add(m.Species.Id);
        }

        // A whiteout no longer auto-heals: the team stays fainted until revived at a town
        // Marketboard's Pokécenter counter.
        State.InBattle = false;
        State.Save();
        battle = null;
        displayedPlayer = null;
        battlePopups.Clear();
        moveFx = null;
        awaitingResult = false;
        view = View.Map;
    }

}
