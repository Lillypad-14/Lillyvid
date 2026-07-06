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
    // ---- Starter selection ----------------------------------------------------------

    private void DrawStarter(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        BiomeBackdrop.Draw(drawList, content, Biome.Grassland, time, false);
        LgUi.Header(content, theme, Accent,"Choose your first partner", "Walk Eorzea to find more in the wild.", scale);

        var top = content.Min.Y + 76f * scale;
        var cardH = (content.Max.Y - top - 62f * scale) / StarterIds.Length;
        var mouse = ImGui.GetMousePos();
        for (var i = 0; i < StarterIds.Length; i++)
        {
            var species = Dex.Find(StarterIds[i])!;
            var min = new Vector2(content.Min.X + 14f * scale, top + i * cardH + 6f * scale);
            var max = new Vector2(content.Max.X - 14f * scale, top + (i + 1) * cardH - 6f * scale);
            var hovered = ImGui.IsMouseHoveringRect(min, max);
            var selected = starterCandidate == species;
            LgUi.Card(drawList, min, max, 14f * scale, scale, hovered || selected);
            Squircle.Stroke(drawList, min, max, 14f * scale,
                ImGui.GetColorU32(Elements.Color(species.Element) with { W = selected ? 1f : 0.7f }),
                (selected ? 2.4f : 1.4f) * scale);
            var portrait = new Vector2(min.X + cardH * 0.55f, (min.Y + max.Y) * 0.5f);
            MonsterArt.Draw(drawList, portrait, cardH * 0.3f, species, 1f, MonsterPose.Idle(time + i));
            Typography.Draw(new Vector2(min.X + cardH * 1.05f, min.Y + cardH * 0.28f), species.Name, theme.TextStrong,
                TextStyles.Headline);
            LgUi.TypeChips(drawList, new Vector2(min.X + cardH * 1.05f, min.Y + cardH * 0.56f), species.Element,
                species.SecondaryElement, scale);
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    starterCandidate = species;
                }

                ImGui.SetTooltip(BuildSpeciesTooltip(species, "Click to select this partner."));
            }
        }

        var confirmSize = new Vector2(210f * scale, 34f * scale);
        var confirm = CenteredAt(new Vector2(content.Center.X, content.Max.Y - 24f * scale), confirmSize);
        var canConfirm = starterCandidate is not null;
        if (LgUi.Button(confirm, canConfirm ? $"Choose {starterCandidate!.Name}" : "Choose a partner", Accent, theme,
                canConfirm))
        {
            ChooseStarter(starterCandidate!);
            starterCandidate = null;
        }
    }

    private void ChooseStarter(MonsterSpecies species)
    {
        State.Party.Add(new MonsterInstance(species, 5));
        State.Seen.Add(species.Id);
        State.StarterChosen = true;
        State.Save();
        view = View.Map;
    }

}
