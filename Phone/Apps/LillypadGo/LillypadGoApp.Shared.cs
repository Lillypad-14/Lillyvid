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
    private void DrawNavigation(Rect content, PhoneTheme theme, float scale)
    {
        var labels = new[] { "Map", "Team", "Guide", "Bag", "Options" };
        var views = new[] { View.Map, View.Team, View.Dex, View.Bag, View.Options };
        var drawList = ImGui.GetWindowDrawList();
        var dock = new Rect(new Vector2(content.Min.X + 8f * scale, content.Max.Y - 39f * scale),
            new Vector2(content.Max.X - 8f * scale, content.Max.Y - 2f * scale));
        var dockRadius = 12f * scale;
        Elevation.Draw(drawList, dock.Min, dock.Max, dockRadius, scale, 12f, -4f, 0.22f);
        Material.Frosted(drawList, dock.Min, dock.Max, dockRadius, scale);

        var inset = 3f * scale;
        var width = (dock.Width - inset * 2f) / labels.Length;
        var selectedIndex = Array.IndexOf(views, view);
        if (selectedIndex < 0)
        {
            selectedIndex = 0;
        }

        if (navIndicator < 0f)
        {
            navIndicator = selectedIndex;
        }

        var dt = MathF.Min(ImGui.GetIO().DeltaTime, 0.1f);
        navIndicator += (selectedIndex - navIndicator) * MathF.Min(1f, dt * 16f);

        // Sliding accent pill behind the active tab.
        var pillMin = new Vector2(dock.Min.X + inset + navIndicator * width, dock.Min.Y + inset);
        var pillMax = new Vector2(pillMin.X + width, dock.Max.Y - inset);
        Elevation.Draw(drawList, pillMin, pillMax, 9f * scale, scale, 5f, 2f, 0.28f);
        Squircle.FillVerticalGradient(drawList, pillMin, pillMax, 9f * scale,
            ImGui.GetColorU32(GamePalette.Lighten(Accent, 0.16f)), ImGui.GetColorU32(GamePalette.Darken(Accent, 0.08f)));
        drawList.AddLine(new Vector2(pillMin.X + 9f * scale, pillMin.Y + 1f * scale),
            new Vector2(pillMax.X - 9f * scale, pillMin.Y + 1f * scale),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.28f)), 1f * scale);

        for (var i = 0; i < labels.Length; i++)
        {
            var min = new Vector2(dock.Min.X + inset + i * width, dock.Min.Y + inset);
            var max = new Vector2(min.X + width, dock.Max.Y - inset);
            var selected = view == views[i];
            var hovered = ImGui.IsMouseHoveringRect(min, max);
            if (hovered && !selected)
            {
                Squircle.Fill(drawList, min, max, 9f * scale,
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.06f)));
            }

            var proximity = Math.Clamp(1f - MathF.Abs(navIndicator - i), 0f, 1f);
            var labelColor = selected ? GamePalette.InkOn(Accent)
                : hovered ? theme.TextStrong
                : Vector4.Lerp(theme.TextMuted, GamePalette.InkOn(Accent), proximity * 0.6f);
            Typography.DrawCentered((min + max) * 0.5f, labels[i], labelColor, TextStyles.SubheadlineEmphasized);
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
    }

    // ---- Shared widgets -------------------------------------------------------------

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
        var status = monster.Status == Status.None ? "None" : monster.Status.ToString();
        var xp = monster.Level >= 100 ? "MAX" : $"{monster.Xp}/{monster.XpToNext}";
        var moves = string.Join(", ", monster.Moves.Select((move, index) =>
            $"{move.Name} {monster.Pp[index]}/{move.Pp}"));
        return $"{monster.Name}  Lv {monster.Level}\n{Elements.Format(monster.Element, monster.SecondaryElement)}\n\n" +
               $"HP {hpOverride ?? monster.CurrentHp}/{monster.MaxHp}\nATK {monster.Atk}    DEF {monster.Def}    SPD {monster.Spd}\n" +
               $"SP. ATK {monster.SpAtk}    SP. DEF {monster.SpDef}\n" +
               $"XP {xp}    Status: {status}\nMoves: {moves}\n\n{note}";
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

    private void DrawStatusPanel(ImDrawListPtr drawList, Vector2 min, Vector2 max, MonsterInstance m, int displayedHp,
        Status displayedStatus, int atkStage, int defStage, int spAtkStage, int spDefStage, int spdStage,
        int displayedLevel, float displayedXpFraction, bool showXp, PhoneTheme theme, float scale)
    {
        var elementColor = Elements.Color(m.Element);
        var slant = 14f * scale;
        var mirrored = showXp;
        var shadow = new Vector2(3f * scale, 4f * scale);
        var shadowColor = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.38f));
        var fillTop = ImGui.GetColorU32(new Vector4(0.10f, 0.13f, 0.17f, 0.94f));
        var fillBottom = ImGui.GetColorU32(new Vector4(0.05f, 0.06f, 0.08f, 0.96f));
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
        var nameMaxWidth = MathF.Max(20f * scale, innerMaxX - innerMinX - levelWidth - 10f * scale);
        var statusName = FitLabel(m.Name, nameMaxWidth, TextStyles.Subheadline);
        Typography.Draw(new Vector2(innerMinX, min.Y + 6f * scale), statusName, theme.TextStrong,
            TextStyles.Subheadline);
        Typography.Draw(new Vector2(innerMaxX - levelWidth, min.Y + 6f * scale), levelText, theme.TextMuted,
            TextStyles.Caption1);

        var hpFraction = m.MaxHp <= 0 ? 0f : Math.Clamp(displayedHp / (float)m.MaxHp, 0f, 1f);
        var hpLabelX = innerMinX + 2f * scale;
        var hpTop = min.Y + 30f * scale;
        Typography.Draw(new Vector2(hpLabelX, hpTop - 3f * scale), "HP", new Vector4(0.95f, 0.84f, 0.32f, 1f),
            TextStyles.Caption2);
        var barMin = new Vector2(innerMinX + 26f * scale, hpTop);
        var barMax = new Vector2(innerMaxX, hpTop + 8f * scale);
        LgUi.HpBar(drawList, barMin, barMax, hpFraction);
        var hpText = $"{displayedHp}/{m.MaxHp}";
        var hpTextSize = Typography.Measure(hpText, TextStyles.Caption2);
        Typography.Draw(new Vector2(innerMaxX - hpTextSize.X, hpTop + 10f * scale), hpText, theme.TextMuted,
            TextStyles.Caption2);

        var hasStatus = displayedStatus != Status.None;
        var stages = StageSummary(atkStage, defStage, spAtkStage, spDefStage, spdStage);
        if (stages.Length > 0)
        {
            var stageRight = hasStatus ? innerMaxX - 38f * scale : innerMaxX - hpTextSize.X - 8f * scale;
            var stagesText = FitLabel(stages, MathF.Max(0f, stageRight - innerMinX), TextStyles.Caption2);
            Typography.Draw(new Vector2(innerMinX, hpTop + 10f * scale), stagesText, theme.TextMuted,
                TextStyles.Caption2);
        }

        if (hasStatus)
        {
            var (element, label) = displayedStatus switch
            {
                Status.Freeze => (Element.Ice, "FRZ"),
                Status.Paralysis => (Element.Electric, "PAR"),
                Status.Poison => (Element.Poison, "PSN"),
                _ => (Element.Fire, "BRN"),
            };
            LgUi.Chip(drawList, new Vector2(innerMaxX - 34f * scale, hpTop + 9f * scale), element, scale, label);
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

    private static string StageSummary(int atkStage, int defStage, int spAtkStage, int spDefStage, int spdStage)
    {
        var parts = new List<string>(5);
        AddStage(parts, "ATK", atkStage);
        AddStage(parts, "DEF", defStage);
        AddStage(parts, "SPA", spAtkStage);
        AddStage(parts, "SDF", spDefStage);
        AddStage(parts, "SPE", spdStage);
        return string.Join(" ", parts);
    }

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

    private void BackButton(Rect panel, PhoneTheme theme, float scale)
    {
        var size = new Vector2(56f * scale, 22f * scale);
        var center = new Vector2(panel.Min.X + 40f * scale, panel.Max.Y - 18f * scale);
        if (LgUi.Button(new Rect(center - size * 0.5f, center + size * 0.5f), "Back", GamePalette.Cell, theme, true))
        {
            menu = Menu.Root;
        }
    }
}
