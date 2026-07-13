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

        // Immerse mode is offered up front rather than buried in Options, since it changes how the
        // whole game is played. Off by default — this is the opt-in.
        var immerseMin = new Vector2(content.Min.X + 14f * scale, content.Min.Y + 68f * scale);
        var immerseMax = new Vector2(content.Max.X - 14f * scale, content.Min.Y + 104f * scale);
        LgUi.Card(drawList, immerseMin, immerseMax, 12f * scale, scale);
        var immersive = State.ImmersiveModeEnabled;
        var immerseRow = new Rect(new Vector2(immerseMin.X + 12f * scale, immerseMin.Y),
            new Vector2(immerseMax.X - 12f * scale, immerseMax.Y));
        if (DrawToggleRow(immerseRow, scale, ref immersive, "Immerse mode — hunt & battle in the world"))
        {
            State.ImmersiveModeEnabled = immersive;
            State.Save();
        }

        if (ImGui.IsMouseHoveringRect(immerseRow.Min, immerseRow.Max))
        {
            ShowTooltip("Immerse mode (opt-in)\n\n" +
                "Wild Pokémon spawn around you in the game world — click one to engage. Battles run " +
                "from an on-screen battle hotbar: slots 1–4 are your lead's moves (hover for full move " +
                "info), 5 Bag, 6 Swap, 7 Run, 8 opens the phone.\n\n" +
                "⚠ WARNING: while a battle is active, keyboard keys 1–8 are captured for these battle " +
                "actions and will NOT trigger your normal game hotbar. Your real hotbars are never " +
                "modified or overwritten — the capture ends the moment the battle does.\n\n" +
                "You can turn this off any time in Options.");
        }

        var top = immerseMax.Y + 8f * scale;
        var bottom = content.Max.Y - 54f * scale; // leave room for the confirm button
        var available = bottom - top;
        var cardH = MathF.Min(available / StarterIds.Length, 132f * scale);
        var startY = top + MathF.Max(0f, (available - cardH * StarterIds.Length) * 0.5f);
        for (var i = 0; i < StarterIds.Length; i++)
        {
            var species = Dex.Find(StarterIds[i])!;
            var min = new Vector2(content.Min.X + 14f * scale, startY + i * cardH + 5f * scale);
            var max = new Vector2(content.Max.X - 14f * scale, startY + (i + 1) * cardH - 5f * scale);
            var center = (min + max) * 0.5f;
            var hovered = ImGui.IsMouseHoveringRect(min, max);
            var selected = starterCandidate == species;
            LgUi.Card(drawList, min, max, 14f * scale, scale, hovered || selected);
            Squircle.Stroke(drawList, min, max, 14f * scale,
                ImGui.GetColorU32(Elements.Color(species.Element) with { W = selected ? 1f : 0.7f }),
                (selected ? 2.4f : 1.4f) * scale);

            var portrait = new Vector2(min.X + 46f * scale, center.Y);
            ProgressRing.Glow(portrait, 30f * scale, Elements.Color(species.Element), 0.35f);
            MonsterArt.Draw(drawList, portrait, MathF.Min(cardH * 0.34f, 40f * scale), species, 1f,
                MonsterPose.Idle(time + i));

            var textX = min.X + 88f * scale;
            var textWidth = max.X - textX - 12f * scale;
            Typography.Draw(new Vector2(textX, center.Y - 26f * scale),
                FitLabel($"{species.Name}  #{species.DexNumber:000}", textWidth, TextStyles.Headline),
                theme.TextStrong, TextStyles.Headline);
            LgUi.TypeChips(drawList, new Vector2(textX, center.Y - 4f * scale), species.Element,
                species.SecondaryElement, scale);
            Typography.Draw(new Vector2(textX, center.Y + 18f * scale),
                FitLabel($"BST {species.BaseStatTotal}  ·  {species.Abilities[0]}", textWidth, TextStyles.Caption2),
                theme.TextStrong with { W = 0.75f }, TextStyles.Caption2);
            if (selected)
            {
                ProgressRing.CenterIcon(drawList, new Vector2(max.X - 20f * scale, center.Y),
                    FontAwesomeIcon.CheckCircle, Elements.Color(species.Element), 16f * scale);
            }

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    starterCandidate = species;
                }

                ShowTooltip(BuildSpeciesTooltip(species, "Click to select this partner."));
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
