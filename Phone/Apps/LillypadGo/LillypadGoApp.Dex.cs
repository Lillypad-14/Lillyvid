using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
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
        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, false);
        var species = Dex.All.OrderBy(monster => monster.Name).ToArray();
        var caughtIds = State.Party.Concat(State.Box).Select(monster => monster.Species.Id).ToHashSet();
        LgUi.Header(content, theme, Accent, "Field Guide",
            $"{caughtIds.Count}/{species.Length} caught  |  {State.Seen.Count}/{species.Length} seen", scale);

        InitializeDexExpansion();
        var sortBounds = new Rect(new Vector2(content.Min.X + 12f * scale, content.Min.Y + 66f * scale),
            new Vector2(content.Max.X - 12f * scale, content.Min.Y + 96f * scale));
        var changedSort = LgUi.Segmented(sortBounds, new[] { "Progress", "Region", "Missing" }, (int)dexSort,
            Accent, theme, scale, ref dexSortIndicator);
        if (changedSort >= 0)
        {
            dexSort = (DexSort)changedSort;
            dexScroll = 0f;
            dexMaxScroll = 0f;
        }

        var list = new Rect(new Vector2(content.Min.X + 4f * scale, content.Min.Y + 104f * scale),
            new Vector2(content.Max.X - 9f * scale, content.Max.Y - 48f * scale));
        dexMaxScroll = MathF.Max(0f, MeasureDexContentHeight(scale) - list.Height);
        dexScroll = Math.Clamp(dexScroll, 0f, dexMaxScroll);
        var mouse = ImGui.GetMousePos();
        if (list.Contains(mouse) && LgUi.Interactive)
        {
            dexScroll = Math.Clamp(dexScroll - ImGui.GetIO().MouseWheel * 52f * scale, 0f, dexMaxScroll);
        }

        var y = list.Min.Y - dexScroll;
        drawList.PushClipRect(list.Min, list.Max, true);
        if (dexSort == DexSort.Region)
        {
            foreach (var region in ArrZones.All.GroupBy(zone => new { zone.Region, zone.RegionOrder })
                         .OrderBy(group => group.Key.RegionOrder))
            {
                DrawDexRegion(region.Key.Region, region.OrderBy(zone => zone.ProgressionOrder).ToArray(), list,
                    caughtIds, ref y, theme, scale);
            }
        }
        else
        {
            foreach (var zone in SortedDexZones(caughtIds))
            {
                DrawDexZone(zone, list, caughtIds, ref y, theme, scale, false);
            }
        }
        drawList.PopClipRect();

        var contentHeight = y + dexScroll - list.Min.Y;
        var maxScroll = MathF.Max(0f, contentHeight - list.Height);
        dexMaxScroll = maxScroll;
        dexScroll = Math.Clamp(dexScroll, 0f, maxScroll);
        LgUi.Scrollbar(new Rect(new Vector2(content.Max.X - 6f * scale, list.Min.Y),
                new Vector2(content.Max.X - 3f * scale, list.Max.Y)), dexScroll, maxScroll,
            list.Height / MathF.Max(list.Height, contentHeight), Accent, scale);
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

    private IEnumerable<ZoneDefinition> SortedDexZones(HashSet<string> caughtIds) => dexSort switch
    {
        DexSort.Missing => ArrZones.All.OrderBy(zone => DexCaughtCount(zone, caughtIds))
            .ThenBy(zone => zone.ProgressionOrder),
        _ => ArrZones.All.OrderBy(zone => zone.ProgressionOrder),
    };

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
            LgUi.Card(ImGui.GetWindowDrawList(), rect.Min, rect.Max, 8f * scale, scale, hovered, !seen);
            var portrait = new Vector2(rect.Min.X + 24f * scale, rect.Center.Y);
            if (seen)
            {
                MonsterArt.Draw(ImGui.GetWindowDrawList(), portrait, 17f * scale, species, 1f,
                    MonsterPose.Idle(time + encounter.MinLevel));
            }
            else
            {
                Typography.DrawCentered(portrait, "?", theme.TextMuted, TextStyles.Title3);
            }

            var rightReserve = 66f * scale;
            var name = FitLabel(seen ? species.Name : "Undiscovered",
                rect.Max.X - (rect.Min.X + 48f * scale) - rightReserve, TextStyles.SubheadlineEmphasized);
            Typography.Draw(new Vector2(rect.Min.X + 48f * scale, rect.Min.Y + 6f * scale), name,
                seen ? theme.TextStrong : theme.TextMuted, TextStyles.SubheadlineEmphasized);
            if (seen)
            {
                LgUi.TypeChips(ImGui.GetWindowDrawList(),
                    new Vector2(rect.Min.X + 48f * scale, rect.Min.Y + 26f * scale), species.Element,
                    species.SecondaryElement, scale);
            }
            Typography.DrawCentered(new Vector2(rect.Max.X - 31f * scale, rect.Min.Y + 14f * scale),
                caught ? "Caught" : seen ? "Seen" : "---", caught ? Accent : theme.TextMuted, TextStyles.Caption1);
            var exclusive = ArrZones.IsExclusiveTo(zone, species.Id);
            Typography.DrawCentered(new Vector2(rect.Max.X - 31f * scale, rect.Min.Y + 33f * scale),
                exclusive ? "EXCLUSIVE" : $"Lv {encounter.MinLevel}-{encounter.MaxLevel}",
                exclusive ? Accent : theme.TextMuted, TextStyles.Caption2);

            if (hovered)
            {
                var note = exclusive ? $"Exclusive to {zone.Name}." : $"Found in {zone.Name}.";
                ImGui.SetTooltip(seen ? BuildSpeciesTooltip(species, note) :
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
                        dexEntryTabIndicator = -1f;
                        dexEntryScroll = 0f;
                        dexEntryReturnView = View.Dex;
                        view = View.DexEntry;
                    }
                }
            }
        }
        y += height + 4f * scale;
    }

    private bool DrawDexDisclosure(Rect rect, string title, string trailing, bool expanded, PhoneTheme theme,
        float scale, bool emphasized, Rect clip)
    {
        var interactive = LgUi.Interactive;
        if (!clip.Contains(ImGui.GetMousePos()))
        {
            LgUi.Interactive = false;
        }
        var clicked = LgUi.Disclosure(rect, title, trailing, expanded, Accent, theme, scale, emphasized);
        LgUi.Interactive = interactive;
        return clicked;
    }

    private static bool RowVisible(Rect row, Rect clip) => row.Max.Y >= clip.Min.Y && row.Min.Y <= clip.Max.Y;

    private float MeasureDexContentHeight(float scale)
    {
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
