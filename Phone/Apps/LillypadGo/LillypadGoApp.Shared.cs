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
    // Height of the bottom navigation bar (unscaled). Screens should keep content above
    // content.Max.Y - (NavBarHeight + ~4) * scale.
    internal const float NavBarHeight = 52f;

    // The game-style bottom tab bar: a navy dock with sprite icons (Assets/pokemon/roster) and
    // labels for Map/Team/Dex/Bag/Arena, a green sliding highlight behind the active tab, and a
    // bordered Settings gear tile on the right.
    private void DrawNavigation(Rect content, PhoneTheme theme, float scale)
    {
        var labels = new[] { "MAP", "TEAM", "DEX", "BAG", "ARENA" };
        var icons = new[] { "nav_map", "nav_team", "nav_dex", "nav_bag", "nav_arena" };
        var views = new[] { View.Map, View.Team, View.Dex, View.Bag, View.Arena };
        var drawList = ImGui.GetWindowDrawList();

        var bar = new Rect(new Vector2(content.Min.X, content.Max.Y - NavBarHeight * scale), content.Max);
        drawList.AddRectFilledMultiColor(bar.Min, bar.Max,
            ImGui.GetColorU32(RosterUi.NavyTop), ImGui.GetColorU32(RosterUi.NavyTop),
            ImGui.GetColorU32(GamePalette.Darken(RosterUi.NavyBottom, 0.06f)),
            ImGui.GetColorU32(GamePalette.Darken(RosterUi.NavyBottom, 0.06f)));
        drawList.AddLine(bar.Min, bar.Min with { X = bar.Max.X }, ImGui.GetColorU32(RosterUi.NavyEdge), 2f * scale);
        drawList.AddLine(bar.Min + new Vector2(0f, 2f * scale), new Vector2(bar.Max.X, bar.Min.Y + 2f * scale),
            ImGui.GetColorU32(RosterUi.NavyLine with { W = 0.55f }), 1.2f * scale);

        var inset = 4f * scale;
        var gearGap = 5f * scale;
        var slotW = (bar.Width - inset * 2f - gearGap) / (labels.Length + 1);
        var slotTop = bar.Min.Y + inset;
        var slotBottom = bar.Max.Y - inset;
        // Sub-screens keep their parent tab lit: the Marketboard belongs to the Bag, and the
        // detail/relearner profiles belong to the Team.
        var highlightView = view switch
        {
            View.Market => View.Bag,
            View.Detail or View.MoveRelearn => View.Team,
            View.DexEntry => View.Dex,
            _ => view,
        };
        var selectedIndex = Array.IndexOf(views, highlightView);
        var showPill = selectedIndex >= 0;

        if (showPill)
        {
            if (navIndicator < 0f)
            {
                navIndicator = selectedIndex;
            }

            var dt = MathF.Min(ImGui.GetIO().DeltaTime, 0.1f);
            navIndicator += (selectedIndex - navIndicator) * MathF.Min(1f, dt * 16f);

            // Sliding green highlight behind the active tab.
            var pillMin = new Vector2(bar.Min.X + inset + navIndicator * slotW, slotTop);
            var pillMax = new Vector2(pillMin.X + slotW, slotBottom);
            Squircle.FillVerticalGradient(drawList, pillMin, pillMax, 8f * scale,
                ImGui.GetColorU32(GamePalette.Lighten(RosterUi.Green, 0.10f)),
                ImGui.GetColorU32(GamePalette.Darken(RosterUi.Green, 0.10f)));
            Squircle.Stroke(drawList, pillMin, pillMax, 8f * scale,
                ImGui.GetColorU32(RosterUi.GreenBright with { W = 0.9f }), 1.6f * scale);
            drawList.AddLine(new Vector2(pillMin.X + 8f * scale, pillMin.Y + 1.5f * scale),
                new Vector2(pillMax.X - 8f * scale, pillMin.Y + 1.5f * scale),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.30f)), 1f * scale);
        }

        for (var i = 0; i < labels.Length; i++)
        {
            var min = new Vector2(bar.Min.X + inset + i * slotW, slotTop);
            var max = new Vector2(min.X + slotW, slotBottom);
            var selected = view == views[i];
            var hovered = ImGui.IsMouseHoveringRect(min, max);
            if (i > 0)
            {
                drawList.AddLine(new Vector2(min.X, slotTop + 6f * scale), new Vector2(min.X, slotBottom - 6f * scale),
                    ImGui.GetColorU32(RosterUi.NavyEdge with { W = 0.5f }), 1f * scale);
            }

            if (hovered && !selected)
            {
                Squircle.Fill(drawList, min, max, 8f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.07f)));
            }

            var cx = (min.X + max.X) * 0.5f;
            var iconShown = RosterUi.Sprite(drawList, icons[i], new Vector2(cx, slotTop + 15f * scale), 24f * scale);
            var labelAlpha = selected ? 1f : hovered ? 0.95f : 0.72f;
            var labelY = iconShown ? slotBottom - 9f * scale : (slotTop + slotBottom) * 0.5f;
            Typography.DrawCentered(new Vector2(cx, labelY),
                FitLabel(labels[i], slotW - 4f * scale, TextStyles.FootnoteEmphasized),
                new Vector4(1f, 1f, 1f, labelAlpha), TextStyles.FootnoteEmphasized);
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    view = views[i];
                    menu = Menu.Root;
                }
            }
        }

        // Settings gear tile on the right, separated and bordered like the mockup.
        var gearMin = new Vector2(bar.Min.X + inset + labels.Length * slotW + gearGap, slotTop);
        var gearMax = new Vector2(bar.Max.X - inset, slotBottom);
        var gearHovered = ImGui.IsMouseHoveringRect(gearMin, gearMax);
        var gearSelected = view == View.Options;
        if (gearSelected)
        {
            Squircle.FillVerticalGradient(drawList, gearMin, gearMax, 8f * scale,
                ImGui.GetColorU32(GamePalette.Lighten(RosterUi.Green, 0.10f)),
                ImGui.GetColorU32(GamePalette.Darken(RosterUi.Green, 0.10f)));
            Squircle.Stroke(drawList, gearMin, gearMax, 8f * scale,
                ImGui.GetColorU32(RosterUi.GreenBright with { W = 0.9f }), 1.6f * scale);
        }
        else
        {
            if (gearHovered)
            {
                Squircle.Fill(drawList, gearMin, gearMax, 8f * scale,
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.07f)));
            }

            Squircle.Stroke(drawList, gearMin, gearMax, 8f * scale,
                ImGui.GetColorU32(RosterUi.NavyLine with { W = 0.6f }), 1.4f * scale);
        }

        var gearCenter = (gearMin + gearMax) * 0.5f;
        if (!RosterUi.Sprite(drawList, "nav_settings", gearCenter, 26f * scale))
        {
            ProgressRing.CenterIcon(drawList, gearCenter, FontAwesomeIcon.Cog,
                new Vector4(1f, 1f, 1f, gearSelected ? 1f : 0.75f), 13f * scale);
        }

        if (gearHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ShowTooltip("Options");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                view = View.Options;
                menu = Menu.Root;
            }
        }
    }

    // ---- Shared widgets -------------------------------------------------------------

    private static string GenderRatioText(MonsterSpecies species)
    {
        if (species.Genderless)
        {
            return "Genderless";
        }

        var male = (int)MathF.Round(species.MaleRatio * 100f);
        return male switch
        {
            100 => "♂ only",
            0 => "♀ only",
            _ => $"♂ {male}% ♀ {100 - male}%",
        };
    }

    private static string EvolutionSummary(MonsterSpecies species)
    {
        if (Dex.EvolutionOf(species) is not { } evo)
        {
            return "Does not evolve.";
        }

        if (species.EvolveLevel > 0)
        {
            return $"Evolves into {evo.Name} at Lv {species.EvolveLevel}.";
        }

        return string.IsNullOrEmpty(species.EvolveMethod)
            ? $"Evolves into {evo.Name}."
            : $"Evolves into {evo.Name} via {species.EvolveMethod}.";
    }

    private static string BuildSpeciesTooltip(MonsterSpecies species, string note)
    {
        var specialty = new[]
            {
                ("Attack", species.BaseAtk), ("Defense", species.BaseDef), ("Sp. Atk", species.BaseSpAtk),
                ("Sp. Def", species.BaseSpDef), ("Speed", species.BaseSpd),
            }
            .MaxBy(stat => stat.Item2).Item1;
        return $"{species.Name}\n{Elements.Format(species.Element, species.SecondaryElement)} creature\n\n" +
               $"Specialty: {specialty}\nBase HP {species.BaseHp}    ATK {species.BaseAtk}    DEF {species.BaseDef}\n" +
               $"SP. ATK {species.BaseSpAtk}    SP. DEF {species.BaseSpDef}    SPD {species.BaseSpd}\n\n" +
               $"Click for the full entry and learnset.\n{note}";
    }

    private static string BuildMoveTooltip(MoveDef move, MonsterInstance user, MonsterInstance target, int pp)
    {
        var power = move.IsStatus ? "--" : move.Power.ToString();
        var accuracy = move.Accuracy <= 0 ? "Always" : move.Accuracy + "%";
        var matchup = "No direct damage.";
        if (!move.IsStatus)
        {
            var isStruggle = ReferenceEquals(move, Moves.Struggle);
            var effectiveness = isStruggle ? 1f :
                Elements.Effectiveness(move.Element, target.Element, target.SecondaryElement);
            matchup = effectiveness <= 0f ? $"No effect against {target.Name}." :
                effectiveness > 1f ? $"Super effective against {target.Name} ({effectiveness:0.##}x)." :
                effectiveness < 1f ? $"Not very effective against {target.Name} ({effectiveness:0.##}x)." :
                $"Neutral against {target.Name} (1x).";
            if (!isStruggle && user.HasType(move.Element))
            {
                matchup += " STAB: 1.5x.";
            }
        }

        var priority = move.Priority == 0 ? string.Empty : $"    Priority: {move.Priority:+#;-#;0}";
        var availability = pp > 0 ? string.Empty : "\nNo PP remains. Rest before using this move again.";
        return $"{move.Name}\n{Elements.Name(move.Element)} {move.CategoryLabel}\n\n{move.Description}\n\n" +
               $"Power: {power}    Accuracy: {accuracy}{priority}\nPP: {pp}/{move.Pp}\n{matchup}{availability}";
    }

    private static string BuildProfileMoveTooltip(MoveDef move, int pp)
    {
        var power = move.IsStatus ? "--" : move.Power.ToString();
        var accuracy = move.Accuracy <= 0 ? "Always" : move.Accuracy + "%";
        return $"{move.Name}\n{Elements.Name(move.Element)} {move.CategoryLabel}\n\n{move.Description}\n\n" +
               $"Power: {power}    Accuracy: {accuracy}\nPP: {pp}/{move.Pp}";
    }

    private static string BuildMonsterTooltip(MonsterInstance monster, string note, int? hpOverride = null)
    {
        var status = monster.Status == Status.None ? "None" : monster.Status switch
        {
            Status.Poison when monster.BadlyPoisoned => $"Badly poisoned (x{monster.ToxicCounter})",
            Status.Sleep when monster.SleepTurns > 0 => $"Sleep ({monster.SleepTurns} turn{(monster.SleepTurns == 1 ? "" : "s")})",
            Status.Freeze => "Frozen (may thaw each turn)",
            _ => monster.Status.ToString(),
        };
        var xp = monster.Level >= 100 ? "MAX" : $"{monster.Xp}/{monster.XpToNext}";
        var moves = string.Join(", ", monster.Moves.Select((move, index) =>
            $"{move.Name} {monster.Pp[index]}/{move.Pp}"));
        var volatileState = new List<string>();
        if (monster.AccuracyStage != 0) volatileState.Add($"Accuracy {StageValue(monster.AccuracyStage)}");
        if (monster.EvasionStage != 0) volatileState.Add($"Evasion {StageValue(monster.EvasionStage)}");
        if (monster.SubstituteHp > 0) volatileState.Add($"Substitute {monster.SubstituteHp} HP");
        if (monster.TauntTurns > 0) volatileState.Add($"Taunt {monster.TauntTurns} turns");
        if (monster.BindingTurns > 0) volatileState.Add($"Binding {monster.BindingTurns} turns");
        if (monster.ConfusionTurns > 0) volatileState.Add($"Confusion {monster.ConfusionTurns} turns");
        if (monster.HealBlockTurns > 0) volatileState.Add($"Heal Block {monster.HealBlockTurns} turns");
        var volatileText = volatileState.Count == 0 ? "None" : string.Join(", ", volatileState);
        return $"{monster.Name}  Lv {monster.Level}\n{Elements.Format(monster.Element, monster.SecondaryElement)}\n\n" +
               $"HP {hpOverride ?? monster.CurrentHp}/{monster.MaxHp}\nATK {monster.Atk}    DEF {monster.Def}    SPD {monster.Spd}\n" +
               $"SP. ATK {monster.SpAtk}    SP. DEF {monster.SpDef}\n" +
               $"XP {xp}    Status: {status}\nEffects: {volatileText}\nMoves: {moves}\n\n{note}";
    }

    private static List<string> WrapText(string text, float maxWidth, in TextStyle style)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && Typography.Measure(candidate, style).X > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        return lines;
    }

    private static string FitLabel(string text, float maxWidth, in TextStyle style)
    {
        if (maxWidth <= 0f || Typography.Measure(text, style).X <= maxWidth)
        {
            return text;
        }

        const string suffix = "...";
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length].TrimEnd() + suffix;
            if (Typography.Measure(candidate, style).X <= maxWidth)
            {
                return candidate;
            }
        }

        return suffix;
    }

    // Fits a name into `maxWidth` by stepping down through `styles` (largest first) before falling
    // back to FitLabel's ellipsis in the smallest one. Returns the text and the style to draw with,
    // so long names shrink instead of getting cut off.
    private static (string Text, TextStyle Style) FitName(string text, float maxWidth, params TextStyle[] styles)
    {
        foreach (var style in styles)
        {
            if (Typography.Measure(text, style).X <= maxWidth)
            {
                return (text, style);
            }
        }

        var smallest = styles[^1];
        return (FitLabel(text, maxWidth, smallest), smallest);
    }

    // A width-bounded tooltip so long descriptive text (move/ability/item summaries) wraps into a
    // readable box instead of streaming off the screen edge. Uses TextUnformatted so stray '%' in
    // descriptions can't be read as a format specifier.
    private static void ShowTooltip(string text)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 20f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private void DrawStatusPanel(ImDrawListPtr drawList, Vector2 min, Vector2 max, MonsterInstance m, float displayedHp,
        Status displayedStatus, int atkStage, int defStage, int spAtkStage, int spDefStage, int spdStage,
        int accuracyStage, int evasionStage, int displayedLevel, float displayedXpFraction, bool showXp,
        PhoneTheme theme, float scale)
    {
        var elementColor = Elements.Color(m.Element);
        var slant = 14f * scale;
        var mirrored = showXp;
        var shadow = new Vector2(3f * scale, 4f * scale);
        var shadowColor = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.38f));
        var fillTop = ImGui.GetColorU32(new Vector4(0.10f, 0.20f, 0.34f, 0.95f));
        var fillBottom = ImGui.GetColorU32(new Vector4(0.05f, 0.11f, 0.20f, 0.96f));
        var edge = ImGui.GetColorU32(GamePalette.Lighten(elementColor, 0.22f) with { W = 0.64f });

        if (mirrored)
        {
            DrawAngledPlate(drawList, min + shadow, max + shadow, slant, true, shadowColor, shadowColor, shadowColor,
                1.2f * scale);
            DrawAngledPlate(drawList, min, max, slant, true, fillTop, fillBottom, edge, 1.2f * scale);
        }
        else
        {
            DrawAngledPlate(drawList, min + shadow, max + shadow, slant, false, shadowColor, shadowColor, shadowColor,
                1.2f * scale);
            DrawAngledPlate(drawList, min, max, slant, false, fillTop, fillBottom, edge, 1.2f * scale);
        }

        var innerMinX = min.X + (mirrored ? 18f : 10f) * scale;
        var innerMaxX = max.X - (mirrored ? 10f : 18f) * scale;
        var levelText = "Lv" + displayedLevel;
        var levelWidth = Typography.Measure(levelText, TextStyles.Caption1).X;

        // The status condition is a coloured tag on the name row (next to the level), so it never
        // collides with the HP readout on the row below.
        var hasStatus = displayedStatus != Status.None;
        var (statusElement, statusLabel) = displayedStatus switch
        {
            Status.Freeze => (Element.Ice, "FRZ"),
            Status.Sleep => (Element.Psychic, "SLP"),
            Status.Paralysis => (Element.Electric, "PAR"),
            Status.Poison => (Element.Poison, m.BadlyPoisoned ? $"PSN x{m.ToxicCounter}" : "PSN"),
            _ => (Element.Fire, "BRN"),
        };
        var volatileLabels = new List<string>(4);
        if (m.SubstituteHp > 0) volatileLabels.Add("SUB");
        if (m.TauntTurns > 0) volatileLabels.Add($"TAU {m.TauntTurns}");
        if (m.BindingTurns > 0) volatileLabels.Add($"BND {m.BindingTurns}");
        if (m.HealBlockTurns > 0) volatileLabels.Add($"HLB {m.HealBlockTurns}");
        if (m.PerishTurns > 0) volatileLabels.Add($"PER {m.PerishTurns}");
        if (m.ConfusionTurns > 0) volatileLabels.Add($"CNF {m.ConfusionTurns}");
        if (volatileLabels.Count > 0)
        {
            statusLabel = hasStatus ? $"{statusLabel} · {string.Join(" ", volatileLabels)}" : string.Join(" ", volatileLabels);
            hasStatus = true;
        }
        var statusChipWidth = hasStatus
            ? Typography.Measure(statusLabel, TextStyles.Caption2).X + 14f * scale
            : 0f;

        var rightReserve = levelWidth + (hasStatus ? statusChipWidth + 7f * scale : 0f);
        var nameMaxWidth = MathF.Max(20f * scale, innerMaxX - innerMinX - rightReserve - 8f * scale);
        var statusName = FitLabel(m.Name, nameMaxWidth, TextStyles.Subheadline);
        Typography.Draw(new Vector2(innerMinX, min.Y + 6f * scale), statusName, theme.TextStrong,
            TextStyles.Subheadline);
        Typography.Draw(new Vector2(innerMaxX - levelWidth, min.Y + 6f * scale), levelText, theme.TextMuted,
            TextStyles.Caption1);
        if (hasStatus)
        {
            LgUi.Chip(drawList, new Vector2(innerMaxX - levelWidth - 7f * scale - statusChipWidth, min.Y + 5f * scale),
                statusElement, scale, statusLabel);
        }

        var hpFraction = m.MaxHp <= 0 ? 0f : Math.Clamp(displayedHp / (float)m.MaxHp, 0f, 1f);
        var hpLabelX = innerMinX + 2f * scale;
        var hpTop = min.Y + 30f * scale;
        Typography.Draw(new Vector2(hpLabelX, hpTop - 3f * scale), "HP", new Vector4(0.95f, 0.84f, 0.32f, 1f),
            TextStyles.Caption2);
        var barMin = new Vector2(innerMinX + 26f * scale, hpTop);
        var barMax = new Vector2(innerMaxX, hpTop + 8f * scale);
        LgUi.HpBar(drawList, barMin, barMax, hpFraction);
        var hpText = $"{(int)MathF.Round(displayedHp)}/{m.MaxHp}";
        var hpTextSize = Typography.Measure(hpText, TextStyles.Caption2);
        Typography.Draw(new Vector2(innerMaxX - hpTextSize.X, hpTop + 10f * scale), hpText, theme.TextMuted,
            TextStyles.Caption2);

        var stages = StageSummary(atkStage, defStage, spAtkStage, spDefStage, spdStage, accuracyStage, evasionStage);
        if (stages.Length > 0)
        {
            var stageRight = innerMaxX - hpTextSize.X - 8f * scale;
            var stagesText = FitLabel(stages, MathF.Max(0f, stageRight - innerMinX), TextStyles.Caption2);
            Typography.Draw(new Vector2(innerMinX, hpTop + 10f * scale), stagesText, theme.TextMuted,
                TextStyles.Caption2);
        }

        if (showXp)
        {
            var xpMin = new Vector2(barMin.X, max.Y - 8f * scale);
            var xpMax = new Vector2(barMax.X, max.Y - 4f * scale);
            Typography.Draw(new Vector2(hpLabelX, xpMin.Y - 5f * scale), "EXP", new Vector4(0.38f, 0.78f, 1f, 1f),
                TextStyles.Caption2);
            LgUi.Meter(drawList, xpMin, xpMax, displayedXpFraction, new Vector4(0.20f, 0.62f, 1f, 1f));
        }
    }

    private static void DrawAngledPlate(ImDrawListPtr drawList, Vector2 min, Vector2 max, float slant, bool mirrored,
        uint fillTop, uint fillBottom, uint edge, float thickness)
    {
        var midY = min.Y + (max.Y - min.Y) * 0.45f;
        if (mirrored)
        {
            drawList.AddQuadFilled(new Vector2(min.X + slant, min.Y), max with { Y = min.Y }, max,
                min with { Y = max.Y }, fillBottom);
            drawList.AddQuadFilled(new Vector2(min.X + slant, min.Y), max with { Y = min.Y },
                max with { Y = midY }, new Vector2(min.X + slant * 0.55f, midY), fillTop);
            drawList.AddLine(new Vector2(min.X + slant, min.Y), max with { Y = min.Y }, edge, thickness);
            drawList.AddLine(max with { Y = min.Y }, max, edge, thickness);
            drawList.AddLine(max, min with { Y = max.Y }, edge, thickness);
            drawList.AddLine(min with { Y = max.Y }, new Vector2(min.X + slant, min.Y), edge, thickness);
            return;
        }

        drawList.AddQuadFilled(min, new Vector2(max.X - slant, min.Y), max, min with { Y = max.Y }, fillBottom);
        drawList.AddQuadFilled(min, new Vector2(max.X - slant, min.Y), new Vector2(max.X - slant * 0.55f, midY),
            min with { Y = midY }, fillTop);
        drawList.AddLine(min, new Vector2(max.X - slant, min.Y), edge, thickness);
        drawList.AddLine(new Vector2(max.X - slant, min.Y), max, edge, thickness);
        drawList.AddLine(max, min with { Y = max.Y }, edge, thickness);
        drawList.AddLine(min with { Y = max.Y }, min, edge, thickness);
    }

    private static string StageSummary(int atkStage, int defStage, int spAtkStage, int spDefStage, int spdStage,
        int accuracyStage, int evasionStage)
    {
        var parts = new List<string>(7);
        AddStage(parts, "ATK", atkStage);
        AddStage(parts, "DEF", defStage);
        AddStage(parts, "SPA", spAtkStage);
        AddStage(parts, "SDF", spDefStage);
        AddStage(parts, "SPE", spdStage);
        AddStage(parts, "ACC", accuracyStage);
        AddStage(parts, "EVA", evasionStage);
        return string.Join(" ", parts);
    }

    private static string StageValue(int stage) => stage > 0 ? $"+{stage}" : stage.ToString();

    private static void AddStage(List<string> parts, string label, int stage)
    {
        if (stage != 0)
        {
            parts.Add(label + (stage > 0 ? "+" : string.Empty) + stage);
        }
    }

    private static Rect Centered(Rect panel, float yFraction, Vector2 size)
    {
        var center = new Vector2(panel.Center.X, panel.Min.Y + yFraction * panel.Height);
        return new Rect(center - size * 0.5f, center + size * 0.5f);
    }

    // A clipped, wheel-scrollable vertical list of fixed-height rows. Rows are culled when off
    // screen, and row interaction is suppressed while the cursor is outside the list so buttons
    // never react through the clip edges. Returns the clamped scroll via the ref parameter.
    private void DrawScrollList(Rect area, float rowHeight, float gap, int count, ref float scroll, float scale,
        Action<int, Rect> drawRow)
    {
        var drawList = ImGui.GetWindowDrawList();
        var step = rowHeight + gap;
        var contentHeight = count <= 0 ? 0f : count * step - gap;
        var maxScroll = MathF.Max(0f, contentHeight - area.Height);
        var mouseInArea = LgUi.Interactive && ImGui.IsMouseHoveringRect(area.Min, area.Max);
        if (mouseInArea)
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0f)
            {
                scroll -= wheel * step;
            }
        }

        scroll = Math.Clamp(scroll, 0f, maxScroll);

        var previousInteractive = LgUi.Interactive;
        drawList.PushClipRect(area.Min, area.Max, true);
        for (var i = 0; i < count; i++)
        {
            var top = area.Min.Y - scroll + i * step;
            var bottom = top + rowHeight;
            if (bottom < area.Min.Y - 2f || top > area.Max.Y + 2f)
            {
                continue;
            }

            LgUi.Interactive = previousInteractive && mouseInArea;
            drawRow(i, new Rect(new Vector2(area.Min.X, top), new Vector2(area.Max.X, bottom)));
        }

        LgUi.Interactive = previousInteractive;
        drawList.PopClipRect();

        if (maxScroll > 0f)
        {
            var track = new Rect(new Vector2(area.Max.X - 4f * scale, area.Min.Y),
                new Vector2(area.Max.X - 1f * scale, area.Max.Y));
            LgUi.Scrollbar(track, scroll, maxScroll, area.Height / MathF.Max(contentHeight, 1f), Accent, scale);
        }
    }

    private void BackButton(Rect panel, PhoneTheme theme, float scale)
    {
        var size = new Vector2(58f * scale, 22f * scale);
        var center = new Vector2(panel.Min.X + 41f * scale, panel.Max.Y - 18f * scale);
        if (RosterUi.BlueButton(new Rect(center - size * 0.5f, center + size * 0.5f), "BACK", scale, true))
        {
            menu = Menu.Root;
        }
    }
}
