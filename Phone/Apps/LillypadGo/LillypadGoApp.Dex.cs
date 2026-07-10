using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using VideoSyncPrototype.Phone.Apps.Games.Framework;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal sealed partial class LillypadGoApp
{
    private void DrawDex(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(RosterUi.NavyBottom));
        var species = Dex.All.OrderBy(monster => monster.Name).ToArray();
        var caughtIds = State.Party.Concat(State.Box).Select(monster => monster.Species.Id).ToHashSet();
        var headerBottom = RosterUi.ScreenHeader(content, "FIELD GUIDE", "nav_dex", new[]
        {
            ($"{caughtIds.Count}/{species.Length}", RosterUi.CountGreen),
            ("caught", new Vector4(1f, 1f, 1f, 1f)),
            ("|", RosterUi.NavyLine),
            ($"{State.Seen.Count}/{species.Length}", RosterUi.CountBlue),
            ("seen", new Vector4(1f, 1f, 1f, 1f)),
        }, scale);

        InitializeDexExpansion();

        // Cream panel first so the folder tabs can sit on its top edge (like the Arena).
        var navTop = content.Max.Y - NavBarHeight * scale;
        var panel = new Rect(new Vector2(content.Min.X + 7f * scale, headerBottom + 32f * scale),
            new Vector2(content.Max.X - 7f * scale, navTop - 7f * scale));
        RosterUi.CreamPanel(drawList, panel, scale);

        var sortBounds = new Rect(new Vector2(content.Min.X + 12f * scale, headerBottom + 8f * scale),
            new Vector2(content.Max.X - 12f * scale, headerBottom + 36f * scale));
        var changedSort = RosterUi.FolderTabs(sortBounds, new[] { "REGION", "NATIONAL", "ALPHAS" }, (int)dexSort,
            scale);
        if (changedSort >= 0)
        {
            dexSort = (DexSort)changedSort;
            dexScroll = 0f;
            dexMaxScroll = 0f;
        }

        var list = new Rect(new Vector2(panel.Min.X + 3f * scale, panel.Min.Y + 8f * scale),
            new Vector2(panel.Max.X - 4f * scale, panel.Max.Y - 8f * scale));
        dexMaxScroll = MathF.Max(0f, MeasureDexContentHeight(scale) - list.Height);
        dexScroll = Math.Clamp(dexScroll, 0f, dexMaxScroll);
        var mouse = ImGui.GetMousePos();
        if (list.Contains(mouse) && LgUi.Interactive)
        {
            dexScroll = Math.Clamp(dexScroll - ImGui.GetIO().MouseWheel * 52f * scale, 0f, dexMaxScroll);
        }

        var y = list.Min.Y - dexScroll;
        drawList.PushClipRect(list.Min, list.Max, true);
        if (dexSort == DexSort.National)
        {
            DrawDexNational(list, caughtIds, ref y, theme, scale);
        }
        else if (dexSort == DexSort.Alphas)
        {
            DrawDexAlphas(list, ref y, theme, scale);
        }
        else
        {
            foreach (var region in ArrZones.All.GroupBy(zone => new { zone.Region, zone.RegionOrder })
                         .OrderBy(group => group.Key.RegionOrder))
            {
                DrawDexRegion(region.Key.Region, region.OrderBy(zone => zone.ProgressionOrder).ToArray(), list,
                    caughtIds, ref y, theme, scale);
            }
        }
        drawList.PopClipRect();

        var contentHeight = y + dexScroll - list.Min.Y;
        var maxScroll = MathF.Max(0f, contentHeight - list.Height);
        dexMaxScroll = maxScroll;
        dexScroll = Math.Clamp(dexScroll, 0f, maxScroll);
        LgUi.Scrollbar(new Rect(new Vector2(panel.Max.X - 6f * scale, list.Min.Y),
                new Vector2(panel.Max.X - 3f * scale, list.Max.Y)), dexScroll, maxScroll,
            list.Height / MathF.Max(list.Height, contentHeight), RosterUi.Blue, scale);
        DrawNavigation(content, theme, scale);
    }

    private void InitializeDexExpansion()
    {
        if (dexInitialized)
        {
            return;
        }

        dexInitialized = true;
        var current = ArrZones.Find(State.Territory) ?? ArrZones.All[0];
        expandedDexRegions.Add(current.Region);
        expandedDexZones.Add(current.TerritoryId);
    }

    private void DrawDexRegion(string region, ZoneDefinition[] zones, Rect clip, HashSet<string> caughtIds,
        ref float y, PhoneTheme theme, float scale)
    {
        const float gap = 5f;
        var height = 32f * scale;
        var rect = new Rect(new Vector2(clip.Min.X + 6f * scale, y),
            new Vector2(clip.Max.X - 4f * scale, y + height));
        var expanded = expandedDexRegions.Contains(region);
        var regionSpecies = zones.SelectMany(zone => zone.Encounters.Select(entry => entry.SpeciesId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var caught = regionSpecies.Count(caughtIds.Contains);
        var total = regionSpecies.Length;
        if (RowVisible(rect, clip) && DrawDexDisclosure(rect, FitLabel(region, rect.Width - 112f * scale,
                TextStyles.SubheadlineEmphasized), $"{caught}/{total}", expanded, theme, scale, true, clip))
        {
            Toggle(expandedDexRegions, region);
        }
        y += height + gap * scale;

        if (!expanded)
        {
            return;
        }

        foreach (var zone in zones)
        {
            DrawDexZone(zone, clip, caughtIds, ref y, theme, scale, true);
        }
    }

    private void DrawDexZone(ZoneDefinition zone, Rect clip, HashSet<string> caughtIds, ref float y,
        PhoneTheme theme, float scale, bool indented)
    {
        var left = clip.Min.X + (indented ? 14f : 6f) * scale;
        var headerHeight = 36f * scale;
        var rect = new Rect(new Vector2(left, y), new Vector2(clip.Max.X - 4f * scale, y + headerHeight));
        var expanded = expandedDexZones.Contains(zone.TerritoryId);
        var uniqueCount = zone.Encounters.Select(entry => entry.SpeciesId).Distinct().Count();
        var caughtCount = DexCaughtCount(zone, caughtIds);
        var trailing = $"{caughtCount}/{uniqueCount}  {zone.LevelLabel}";
        var title = FitLabel(zone.Name, rect.Width - 142f * scale, TextStyles.SubheadlineEmphasized);
        if (RowVisible(rect, clip) && DrawDexDisclosure(rect, title, trailing, expanded, theme, scale,
                zone.TerritoryId == State.Territory, clip))
        {
            Toggle(expandedDexZones, zone.TerritoryId);
        }
        y += headerHeight + 4f * scale;

        if (!expanded)
        {
            return;
        }

        foreach (var encounter in zone.Encounters.GroupBy(entry => entry.SpeciesId).Select(group => group.First()))
        {
            DrawDexSpecies(zone, encounter, clip, caughtIds, ref y, theme, scale, indented);
        }
        y += 4f * scale;
    }

    private void DrawDexSpecies(ZoneDefinition zone, SpawnEntry encounter, Rect clip, HashSet<string> caughtIds,
        ref float y, PhoneTheme theme, float scale, bool regionIndented)
    {
        var height = 48f * scale;
        var left = clip.Min.X + (regionIndented ? 24f : 16f) * scale;
        var rect = new Rect(new Vector2(left, y), new Vector2(clip.Max.X - 4f * scale, y + height));
        var species = Dex.Find(encounter.SpeciesId);
        if (species is null)
        {
            y += height + 4f * scale;
            return;
        }

        var seen = State.Seen.Contains(species.Id);
        var caught = caughtIds.Contains(species.Id);
        if (RowVisible(rect, clip))
        {
            var hovered = clip.Contains(ImGui.GetMousePos()) && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
            RosterUi.DarkCard(ImGui.GetWindowDrawList(), rect, 8f * scale, scale, hovered, !seen,
                accent: seen ? Elements.Color(species.Element) : null);
            var portrait = new Vector2(rect.Min.X + 24f * scale, rect.Center.Y);
            if (seen)
            {
                MonsterArt.Draw(ImGui.GetWindowDrawList(), portrait, 17f * scale, species, 1f,
                    MonsterPose.Idle(time + encounter.MinLevel));
            }
            else
            {
                Typography.DrawCentered(portrait, "?", RosterUi.CardMuted, TextStyles.Title3);
            }

            var rightReserve = 66f * scale;
            var name = FitLabel(seen ? species.Name : "Undiscovered",
                rect.Max.X - (rect.Min.X + 48f * scale) - rightReserve, TextStyles.SubheadlineEmphasized);
            Typography.Draw(new Vector2(rect.Min.X + 48f * scale, rect.Min.Y + 6f * scale), name,
                seen ? RosterUi.CardInk : RosterUi.CardMuted, TextStyles.SubheadlineEmphasized);
            if (seen)
            {
                LgUi.TypeChips(ImGui.GetWindowDrawList(),
                    new Vector2(rect.Min.X + 48f * scale, rect.Min.Y + 26f * scale), species.Element,
                    species.SecondaryElement, scale);
            }
            Typography.DrawCentered(new Vector2(rect.Max.X - 31f * scale, rect.Min.Y + 14f * scale),
                caught ? "Caught" : seen ? "Seen" : "---",
                caught ? RosterUi.CountGreen : RosterUi.CardMuted, TextStyles.Caption1);
            var exclusive = ArrZones.IsExclusiveTo(zone, species.Id);
            Typography.DrawCentered(new Vector2(rect.Max.X - 31f * scale, rect.Min.Y + 33f * scale),
                exclusive ? "EXCLUSIVE" : $"Lv {encounter.MinLevel}-{encounter.MaxLevel}",
                exclusive ? RosterUi.Gold : RosterUi.CardMuted, TextStyles.Caption2);

            if (hovered)
            {
                var note = exclusive ? $"Exclusive to {zone.Name}." : $"Found in {zone.Name}.";
                ShowTooltip(seen ? BuildSpeciesTooltip(species, note) :
                    $"Undiscovered creature\n\n{note} Search this zone to reveal it.");
                if (seen && LgUi.Interactive)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        dexEntrySpecies = species;
                        learnsetMonster = null;
                        teachPendingMove = null;
                        dexEntryTab = 0;
                        dexEntryScroll = 0f;
                        dexEntryReturnView = View.Dex;
                        view = View.DexEntry;
                    }
                }
            }
        }
        y += height + 4f * scale;
    }

    // A tan chunky disclosure row (regions and zones): arrow, title, right-aligned summary, and a
    // green edge bar when it marks the player's current region/zone.
    private bool DrawDexDisclosure(Rect rect, string title, string trailing, bool expanded, PhoneTheme theme,
        float scale, bool emphasized, Rect clip)
    {
        var drawList = ImGui.GetWindowDrawList();
        var interactive = LgUi.Interactive && clip.Contains(ImGui.GetMousePos());
        var hovered = interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        var radius = 8f * scale;
        RosterUi.ChunkyCard(drawList, rect.Min, rect.Max, radius, scale, RosterUi.TanTop, RosterUi.TanBottom,
            RosterUi.TanEdge, hovered);
        if (emphasized)
        {
            drawList.PushClipRect(rect.Min, new Vector2(rect.Min.X + 5f * scale, rect.Max.Y), true);
            Squircle.Fill(drawList, rect.Min, rect.Max, radius, ImGui.GetColorU32(RosterUi.Green));
            drawList.PopClipRect();
        }

        Typography.DrawCentered(new Vector2(rect.Min.X + 17f * scale, rect.Center.Y), expanded ? "v" : ">",
            emphasized ? GamePalette.Darken(RosterUi.Green, 0.15f) : RosterUi.InkTan,
            TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(rect.Min.X + 31f * scale, rect.Center.Y - 8f * scale), title,
            RosterUi.InkNavy, TextStyles.SubheadlineEmphasized);
        if (!string.IsNullOrEmpty(trailing))
        {
            var size = Typography.Measure(trailing, TextStyles.Caption1);
            Typography.Draw(new Vector2(rect.Max.X - size.X - 10f * scale, rect.Center.Y - 7f * scale), trailing,
                RosterUi.InkTan, TextStyles.Caption1);
        }

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        return hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }

    private static bool RowVisible(Rect row, Rect clip) => row.Max.Y >= clip.Min.Y && row.Min.Y <= clip.Max.Y;

    // The numbered National dex: every species by dex number in a two-column grid, seen/caught state
    // shown, tap-through to the full entry — just like the mainline Pokédex list.
    private void DrawDexNational(Rect clip, HashSet<string> caughtIds, ref float y, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var all = Dex.All.OrderBy(species => species.DexNumber).ToArray();
        const int cols = 2;
        var gap = 6f * scale;
        var cellW = (clip.Width - 8f * scale - gap * (cols - 1)) / cols;
        var cellH = 52f * scale;
        var left = clip.Min.X + 4f * scale;
        var mouse = ImGui.GetMousePos();
        for (var i = 0; i < all.Length; i++)
        {
            var col = i % cols;
            var min = new Vector2(left + col * (cellW + gap), y + i / cols * (cellH + gap));
            var max = min + new Vector2(cellW, cellH);
            var rect = new Rect(min, max);
            if (!RowVisible(rect, clip))
            {
                continue;
            }

            var species = all[i];
            var seen = State.Seen.Contains(species.Id);
            var caught = caughtIds.Contains(species.Id);
            var hovered = clip.Contains(mouse) && ImGui.IsMouseHoveringRect(min, max);
            RosterUi.DarkCard(drawList, rect, 8f * scale, scale, hovered, !seen);

            Typography.Draw(new Vector2(min.X + 8f * scale, min.Y + 6f * scale),
                $"#{species.DexNumber:D3}", RosterUi.CardMuted, TextStyles.Caption2);
            var portrait = new Vector2(min.X + 24f * scale, max.Y - 17f * scale);
            if (seen)
            {
                MonsterArt.Draw(drawList, portrait, 15f * scale, species, 1f,
                    MonsterPose.Idle(time + species.DexNumber));
            }
            else
            {
                Typography.DrawCentered(portrait, "?", RosterUi.CardMuted, TextStyles.Title3);
            }

            var name = FitLabel(seen ? species.Name : "----", max.X - (min.X + 44f * scale) - 8f * scale,
                TextStyles.SubheadlineEmphasized);
            Typography.Draw(new Vector2(min.X + 44f * scale, min.Y + 22f * scale), name,
                seen ? RosterUi.CardInk : RosterUi.CardMuted, TextStyles.SubheadlineEmphasized);
            if (caught)
            {
                var dot = new Vector2(max.X - 12f * scale, min.Y + 12f * scale);
                drawList.AddCircleFilled(dot, 4f * scale, ImGui.GetColorU32(RosterUi.CountGreen));
            }

            if (hovered && seen)
            {
                ShowTooltip(BuildSpeciesTooltip(species, caught ? "In your collection." : "Seen in the wild."));
                if (LgUi.Interactive)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        dexEntrySpecies = species;
                        learnsetMonster = null;
                        teachPendingMove = null;
                        dexEntryTab = 0;
                        dexEntryScroll = 0f;
                        dexEntryReturnView = View.Dex;
                        view = View.DexEntry;
                    }
                }
            }
        }

        y += (all.Length + cols - 1) / cols * (cellH + gap);
    }

    // The ALPHAS tab: one hero card per region Alpha — where it lairs, its trait, whether it is
    // alive right now (or how long until it returns), the defeat tally, the first-clear trophy,
    // and its possible spoils. Alphas are landmarks, so every card is fully visible from the start.
    private const float AlphaCardHeight = 118f;

    private void DrawDexAlphas(Rect clip, ref float y, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var mouse = ImGui.GetMousePos();
        Typography.DrawCentered(new Vector2(clip.Center.X, y + 10f * scale),
            FitLabel("One Alpha rules each region. Fell it for rare spoils — it returns hours later.",
                clip.Width - 20f * scale, TextStyles.Caption2), RosterUi.InkTan, TextStyles.Caption2);
        y += 24f * scale;

        foreach (var alphaDef in Alphas.All)
        {
            var height = AlphaCardHeight * scale;
            var rect = new Rect(new Vector2(clip.Min.X + 6f * scale, y),
                new Vector2(clip.Max.X - 4f * scale, y + height));
            y += height + 6f * scale;
            if (!RowVisible(rect, clip) || alphaDef.Species is not { } species)
            {
                continue;
            }

            var alive = State.IsAlphaAlive(alphaDef.Id);
            var kills = State.AlphaKills(alphaDef.Id);
            var seen = State.Seen.Contains(species.Id);
            var traitColor = Alphas.TraitColor(alphaDef.Trait);
            var hovered = clip.Contains(mouse) && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
            RosterUi.DarkCard(drawList, rect, 10f * scale, scale, hovered, false, accent: traitColor);

            var portrait = new Vector2(rect.Min.X + 38f * scale, rect.Min.Y + 46f * scale);
            if (alive)
            {
                DrawAlphaAura(drawList, portrait, 26f * scale, traitColor, time + alphaDef.Level);
            }

            MonsterArt.Draw(drawList, portrait, 24f * scale, species, 1f,
                new MonsterPose(time + alphaDef.Level, 0f, 0f, alive ? 1f : 0.4f, !alive));

            var textX = rect.Min.X + 74f * scale;
            var rightEdge = rect.Max.X - 10f * scale;
            var name = FitLabel(alphaDef.DisplayName, rightEdge - textX - 92f * scale,
                TextStyles.SubheadlineEmphasized);
            Typography.Draw(new Vector2(textX, rect.Min.Y + 8f * scale), name, RosterUi.Gold,
                TextStyles.SubheadlineEmphasized);
            if (kills > 0)
            {
                var nameWidth = Typography.Measure(name, TextStyles.SubheadlineEmphasized).X;
                ProgressRing.CenterIcon(drawList, new Vector2(textX + nameWidth + 12f * scale,
                    rect.Min.Y + 16f * scale), FontAwesomeIcon.Trophy, RosterUi.Gold, 10f * scale);
            }

            var status = alive ? "PROWLING" : FormatRespawn(State.AlphaRespawnIn(alphaDef.Id));
            var statusColor = alive ? RosterUi.CountGreen : new Vector4(0.93f, 0.76f, 0.36f, 1f);
            var statusSize = Typography.Measure(status, TextStyles.Caption1);
            Typography.Draw(new Vector2(rightEdge - statusSize.X, rect.Min.Y + 10f * scale), status,
                statusColor, TextStyles.Caption1);

            Typography.Draw(new Vector2(textX, rect.Min.Y + 28f * scale),
                FitLabel($"Lv {alphaDef.Level}  ·  {alphaDef.ZoneName} {alphaDef.CoordsLabel}",
                    rightEdge - textX - 70f * scale, TextStyles.Caption1), RosterUi.CardInk, TextStyles.Caption1);
            var tally = kills > 0 ? $"x{kills}" : "Undefeated";
            var tallySize = Typography.Measure(tally, TextStyles.Caption2);
            Typography.Draw(new Vector2(rightEdge - tallySize.X, rect.Min.Y + 29f * scale), tally,
                kills > 0 ? RosterUi.CountGreen : RosterUi.CardMuted, TextStyles.Caption2);

            Typography.Draw(new Vector2(textX, rect.Min.Y + 46f * scale),
                FitLabel($"{alphaDef.Lair} — {alphaDef.Lore}", rightEdge - textX, TextStyles.Caption2),
                RosterUi.CardMuted, TextStyles.Caption2);

            // Trait pill + its one-line effect.
            var traitLabel = Alphas.TraitName(alphaDef.Trait).ToUpperInvariant();
            var traitSize = Typography.Measure(traitLabel, TextStyles.Caption2);
            var pillMin = new Vector2(textX, rect.Min.Y + 64f * scale);
            var pillMax = pillMin + new Vector2(traitSize.X + 12f * scale, 16f * scale);
            Squircle.Fill(drawList, pillMin, pillMax, 8f * scale,
                ImGui.GetColorU32(traitColor with { W = 0.28f }));
            Typography.Draw(new Vector2(pillMin.X + 6f * scale, pillMin.Y + 2f * scale), traitLabel,
                GamePalette.Lighten(traitColor, 0.25f), TextStyles.Caption2);
            Typography.Draw(new Vector2(pillMax.X + 8f * scale, pillMin.Y + 2f * scale),
                FitLabel(Alphas.TraitBlurb(alphaDef.Trait), rightEdge - pillMax.X - 8f * scale,
                    TextStyles.Caption2), RosterUi.CardMuted, TextStyles.Caption2);

            Typography.Draw(new Vector2(rect.Min.X + 12f * scale, rect.Max.Y - 20f * scale),
                FitLabel($"Spoils: {Alphas.DropSummary(alphaDef)}", rect.Width - 24f * scale,
                    TextStyles.Caption2), RosterUi.CardMuted, TextStyles.Caption2);

            if (hovered)
            {
                ShowTooltip($"{alphaDef.DisplayName}  ·  Lv {alphaDef.Level}\n" +
                    $"{alphaDef.Region} — {alphaDef.ZoneName} {alphaDef.CoordsLabel}\n{alphaDef.Lore}\n\n" +
                    $"{Alphas.TraitName(alphaDef.Trait)}: {Alphas.TraitBlurb(alphaDef.Trait)}\n" +
                    (Alphas.WeatherLabel(alphaDef.Weather) is { Length: > 0 } weather
                        ? $"Lair weather: {weather}.\n"
                        : string.Empty) +
                    $"Possible spoils: {Alphas.DropSummary(alphaDef)}\n" +
                    "First clear: Region Trophy, Alpha Badge and a Dex-completion gil bonus.\n\n" +
                    (alive
                        ? $"It prowls its den at {alphaDef.CoordsLabel} right now — travel within " +
                          $"{Alphas.ChallengeRadius:0} yalms and challenge it from the Map tab."
                        : $"Defeated. It reclaims its lair in {FormatRespawn(State.AlphaRespawnIn(alphaDef.Id))}.") +
                    (seen ? "\nClick to open the species entry." : ""));
                if (seen && LgUi.Interactive)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        dexEntrySpecies = species;
                        learnsetMonster = null;
                        teachPendingMove = null;
                        dexEntryTab = 0;
                        dexEntryScroll = 0f;
                        dexEntryReturnView = View.Dex;
                        view = View.DexEntry;
                    }
                }
            }
        }
    }

    private float MeasureDexContentHeight(float scale)
    {
        if (dexSort == DexSort.National)
        {
            var rows = (Dex.All.Count + 1) / 2;
            return rows * 58f * scale;
        }

        if (dexSort == DexSort.Alphas)
        {
            return (24f + Alphas.All.Count * (AlphaCardHeight + 6f)) * scale;
        }

        var total = 0f;
        if (dexSort == DexSort.Region)
        {
            foreach (var region in ArrZones.All.GroupBy(zone => new { zone.Region, zone.RegionOrder }))
            {
                total += 37f * scale;
                if (!expandedDexRegions.Contains(region.Key.Region))
                {
                    continue;
                }

                foreach (var zone in region)
                {
                    total += MeasureDexZoneHeight(zone, scale);
                }
            }

            return total;
        }

        foreach (var zone in ArrZones.All)
        {
            total += MeasureDexZoneHeight(zone, scale);
        }

        return total;
    }

    private float MeasureDexZoneHeight(ZoneDefinition zone, float scale)
    {
        var height = 40f * scale;
        if (expandedDexZones.Contains(zone.TerritoryId))
        {
            var speciesCount = zone.Encounters.Select(entry => entry.SpeciesId).Distinct().Count();
            height += (speciesCount * 52f + 4f) * scale;
        }

        return height;
    }

    private static int DexCaughtCount(ZoneDefinition zone, HashSet<string> caughtIds) =>
        zone.Encounters.Select(entry => entry.SpeciesId).Distinct().Count(caughtIds.Contains);

    private static void Toggle<T>(HashSet<T> set, T value)
    {
        if (!set.Add(value))
        {
            set.Remove(value);
        }
    }
}
