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
    private void DrawDetail(Rect content, PhoneTheme theme)
    {
        if (detailMonster is not { } monster)
        {
            view = detailReturnView;
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, false);
        var back = CenteredAt(new Vector2(content.Min.X + 38f * scale, content.Min.Y + 20f * scale),
            new Vector2(62f * scale, 26f * scale));
        if (LgUi.Button(back, "Back", GamePalette.Cell, theme, true))
        {
            view = detailReturnView;
            return;
        }

        var learnset = CenteredAt(new Vector2(content.Max.X - 54f * scale, content.Min.Y + 20f * scale),
            new Vector2(92f * scale, 26f * scale));
        if (LgUi.Button(learnset, "Moves", Accent, theme, true))
        {
            dexEntrySpecies = monster.Species;
            learnsetMonster = monster;
            teachPendingMove = null;
            dexEntryTab = 1;
            dexEntryTabIndicator = -1f;
            dexEntryScroll = 0f;
            dexEntryReturnView = View.Detail;
            view = View.DexEntry;
            return;
        }

        if (ImGui.IsMouseHoveringRect(learnset.Min, learnset.Max))
        {
            ImGui.SetTooltip($"View {monster.Species.Name}'s learnset and customise its moves.");
        }

        var portrait = new Vector2(content.Center.X - (Dex.EvolutionOf(monster.Species) is not null ? 30f * scale : 0f),
            content.Min.Y + 88f * scale);
        ProgressRing.Glow(portrait, 54f * scale, Elements.Color(monster.Element), 0.45f);
        MonsterArt.Draw(drawList, portrait, 48f * scale, monster.Species, 1f,
            MonsterPose.Idle(time));

        // Next evolution preview: arrow + a small sprite of the evolved form, with the trigger.
        if (Dex.EvolutionOf(monster.Species) is { } evo)
        {
            var evoCenter = new Vector2(content.Center.X + 74f * scale, content.Min.Y + 84f * scale);
            Typography.DrawCentered(new Vector2(content.Center.X + 30f * scale, portrait.Y), ">",
                theme.TextStrong with { W = 0.7f }, TextStyles.Title2);
            ProgressRing.Glow(evoCenter, 28f * scale, Elements.Color(evo.Element), 0.32f);
            MonsterArt.Draw(drawList, evoCenter, 24f * scale, evo, 1f, MonsterPose.Idle(time + 1.3f));
            var trigger = monster.Species.EvolveLevel > 0
                ? $"Lv {monster.Species.EvolveLevel}"
                : monster.Species.EvolveMethod ?? evo.Name;
            Typography.DrawCentered(new Vector2(evoCenter.X, evoCenter.Y + 34f * scale),
                FitLabel(trigger, 92f * scale, TextStyles.Caption2), theme.TextStrong with { W = 0.82f },
                TextStyles.Caption2);
        }
        var nameText = FitLabel(monster.Name, content.Width - 24f * scale, TextStyles.Title2);
        Typography.DrawCentered(new Vector2(content.Center.X, content.Min.Y + 148f * scale), nameText,
            theme.TextStrong, TextStyles.Title2);
        var subtitleText = FitLabel($"{monster.Species.Name}  |  {Elements.Format(monster.Element, monster.SecondaryElement)}  |  Lv {monster.Level}",
            content.Width - 24f * scale, TextStyles.Caption1);
        Typography.DrawCentered(new Vector2(content.Center.X, content.Min.Y + 170f * scale), subtitleText,
            theme.TextStrong with { W = 0.85f }, TextStyles.Caption1);

        Typography.Draw(new Vector2(content.Min.X + 16f * scale, content.Min.Y + 188f * scale), "Nickname",
            theme.TextMuted, TextStyles.Caption2);
        var nickRect = new Rect(new Vector2(content.Min.X + 16f * scale, content.Min.Y + 200f * scale),
            new Vector2(content.Max.X - 96f * scale, content.Min.Y + 228f * scale));
        var submittedName = LgUi.Input(nickRect, "##lillypadgo-nickname", ref detailNameDraft, 21, theme, scale);
        var trimmedName = detailNameDraft.Trim();
        var nameChanged = trimmedName != monster.Nickname;
        var saveName = CenteredAt(new Vector2(content.Max.X - 44f * scale, content.Min.Y + 214f * scale),
            new Vector2(72f * scale, 28f * scale));
        var clickedSaveName = LgUi.Button(saveName, "Save", nameChanged ? Accent : GamePalette.CellSunken, theme,
            nameChanged);
        if (nameChanged && (clickedSaveName || submittedName))
        {
            monster.Rename(detailNameDraft);
            detailNameDraft = monster.Nickname;
            State.Save();
        }

        var statsMin = new Vector2(content.Min.X + 12f * scale, content.Min.Y + 242f * scale);
        var statsMax = new Vector2(content.Max.X - 12f * scale, content.Min.Y + 302f * scale);
        LgUi.Card(drawList, statsMin, statsMax, 12f * scale, scale);
        var stats = new[]
        {
            ("HP", $"{monster.CurrentHp}/{monster.MaxHp}"), ("ATK", monster.Atk.ToString()),
            ("DEF", monster.Def.ToString()), ("SP.A", monster.SpAtk.ToString()),
            ("SP.D", monster.SpDef.ToString()), ("SPD", monster.Spd.ToString()),
        };
        for (var i = 0; i < stats.Length; i++)
        {
            var x = statsMin.X + (i + 0.5f) * (statsMax.X - statsMin.X) / stats.Length;
            Typography.DrawCentered(new Vector2(x, statsMin.Y + 20f * scale), stats[i].Item2, theme.TextStrong,
                TextStyles.Headline);
            Typography.DrawCentered(new Vector2(x, statsMin.Y + 40f * scale), stats[i].Item1,
                theme.TextStrong with { W = 0.78f }, TextStyles.Caption2);
        }

        var xpLabel = monster.Level >= 100 ? "Maximum level" : $"XP {monster.Xp}/{monster.XpToNext}";
        Typography.Draw(new Vector2(content.Min.X + 16f * scale, content.Min.Y + 314f * scale), xpLabel,
            theme.TextStrong with { W = 0.8f }, TextStyles.Caption2);
        LgUi.Meter(drawList, new Vector2(content.Min.X + 16f * scale, content.Min.Y + 330f * scale),
            new Vector2(content.Max.X - 16f * scale, content.Min.Y + 337f * scale), monster.XpFraction, Accent);

        var winRate = monster.Battles == 0 ? "--" : $"{monster.Victories * 100 / monster.Battles}%";
        Typography.DrawCentered(new Vector2(content.Center.X, content.Min.Y + 354f * scale),
            $"{monster.Battles} battles  |  {monster.Victories} victories  |  {winRate} win rate",
            theme.TextStrong with { W = 0.82f }, TextStyles.Caption2);
        Typography.DrawCentered(new Vector2(content.Center.X, content.Min.Y + 371f * scale),
            $"{monster.DamageDealt} total damage", theme.TextStrong with { W = 0.82f }, TextStyles.Caption2);
        Typography.DrawCentered(new Vector2(content.Center.X, content.Min.Y + 388f * scale),
            FitLabel($"Habitats: {ArrZones.Habitats(monster.Species.Id)}", content.Width - 28f * scale,
                TextStyles.Caption2), theme.TextStrong with { W = 0.78f }, TextStyles.Caption2);
        Typography.Draw(new Vector2(content.Min.X + 14f * scale, content.Min.Y + 405f * scale), "Moves",
            theme.TextStrong, TextStyles.SubheadlineEmphasized);

        var movesTop = content.Min.Y + 423f * scale;
        var movesBottom = content.Max.Y - 8f * scale;
        if (movesBottom - movesTop < 24f * scale)
        {
            // No room for the moves grid on a very short / high-DPI screen; better to omit it than
            // to draw inverted, overlapping cards.
            return;
        }

        var columns = 2;
        var rows = Math.Max(1, (monster.Moves.Count + columns - 1) / columns);
        var gap = 5f * scale;
        var cardWidth = (content.Width - 24f * scale - gap) / columns;
        var rowH = MathF.Max(22f * scale, MathF.Min(58f * scale, (movesBottom - movesTop - gap * (rows - 1)) / rows));
        for (var i = 0; i < monster.Moves.Count; i++)
        {
            var move = monster.Moves[i];
            var column = i % columns;
            var row = i / columns;
            var min = new Vector2(content.Min.X + 12f * scale + column * (cardWidth + gap),
                movesTop + row * (rowH + gap));
            var max = min + new Vector2(cardWidth, rowH);
            if (max.Y > movesBottom + 1f * scale)
            {
                break;
            }

            var hovered = ImGui.IsMouseHoveringRect(min, max);
            var color = Elements.Color(move.Element);
            LgUi.Card(drawList, min, max, 9f * scale, scale, hovered);
            drawList.AddRectFilled(min, new Vector2(min.X + 5f * scale, max.Y), ImGui.GetColorU32(color),
                4f * scale);
            Typography.Draw(new Vector2(min.X + 14f * scale, min.Y + 7f * scale), move.Name, theme.TextStrong,
                TextStyles.SubheadlineEmphasized);
            Typography.Draw(new Vector2(min.X + 14f * scale, max.Y - 16f * scale),
                $"{Elements.Name(move.Element)} {move.CategoryLabel}  {monster.Pp[i]}/{move.Pp} PP", theme.TextMuted,
                TextStyles.Caption2);
            if (hovered)
            {
                ImGui.SetTooltip(BuildProfileMoveTooltip(move, monster.Pp[i]));
            }
        }
    }

}
