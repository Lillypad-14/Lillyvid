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
    private void DrawBag(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, false);
        LgUi.Header(content, theme, Accent, "Bag", null, scale);
        var items = new[]
        {
            ("Aether Snare", "Catches wild monsters.", State.Bag.Snares, FontAwesomeIcon.Bullseye),
            ("Tonic", $"Restores {Bag.TonicHeal} HP.", State.Bag.Tonics, FontAwesomeIcon.Flask),
        };
        var top = content.Min.Y + 54f * scale;
        for (var i = 0; i < items.Length; i++)
        {
            var (name, desc, count, icon) = items[i];
            var min = new Vector2(content.Min.X + 14f * scale, top + i * 60f * scale);
            var max = new Vector2(content.Max.X - 14f * scale, top + i * 60f * scale + 52f * scale);
            var hovered = ImGui.IsMouseHoveringRect(min, max);
            LgUi.Card(drawList, min, max, 12f * scale, scale, hovered);
            var iconCenter = new Vector2(min.X + 32f * scale, (min.Y + max.Y) * 0.5f);
            drawList.AddCircleFilled(iconCenter, 18f * scale, ImGui.GetColorU32(GamePalette.CellSunken));
            drawList.AddCircle(iconCenter, 18f * scale, ImGui.GetColorU32(Accent with { W = 0.4f }), 24, 1f * scale);
            ProgressRing.CenterIcon(drawList, iconCenter, icon, Accent, 17f * scale);
            Typography.Draw(new Vector2(min.X + 62f * scale, min.Y + 10f * scale), name, theme.TextStrong,
                TextStyles.Headline);
            Typography.Draw(new Vector2(min.X + 62f * scale, min.Y + 30f * scale), desc, theme.TextMuted,
                TextStyles.Caption1);
            Typography.DrawCentered(new Vector2(max.X - 26f * scale, (min.Y + max.Y) * 0.5f), "x" + count, Accent,
                TextStyles.Title3);
            if (hovered)
            {
                ImGui.SetTooltip(i == 0
                    ? "Use during a wild battle. Lower HP and status effects improve capture odds."
                    : $"Use in battle or from this bag to restore up to {Bag.TonicHeal} HP. It can revive a fainted creature.");
            }
        }

        var hurt = State.Party.Where(monster => monster.CurrentHp < monster.MaxHp)
            .OrderBy(monster => monster.HpFraction).FirstOrDefault();
        var tonicRect = CenteredAt(new Vector2(content.Center.X, top + 138f * scale),
            new Vector2(210f * scale, 34f * scale));
        var canUseTonic = hurt is not null && State.Bag.Tonics > 0;
        var tonicLabel = hurt is null ? "Team is fully restored" : $"Use Tonic on {hurt.Name}";
        if (LgUi.Button(tonicRect, tonicLabel, canUseTonic ? theme.Accent : GamePalette.CellSunken, theme,
                canUseTonic))
        {
            State.Bag.Tonics--;
            hurt!.Heal(Bag.TonicHeal);
            State.Save();
        }

        if (ImGui.IsMouseHoveringRect(tonicRect.Min, tonicRect.Max))
        {
            var hint = hurt is null ? "Every team creature is already at full HP." :
                State.Bag.Tonics <= 0 ? "No Tonics remain." :
                $"Restore {Math.Min(Bag.TonicHeal, hurt.MaxHp - hurt.CurrentHp)} HP to {hurt.Name}.";
            ImGui.SetTooltip(hint);
        }

        Typography.DrawCentered(new Vector2(content.Center.X, top + 178f * scale),
            "Win battles to recover trail supplies.", theme.TextMuted, TextStyles.Caption1);
        Typography.DrawCentered(new Vector2(content.Center.X, top + 198f * scale),
            $"{State.BattlesWon} wins  |  {State.Captures} captures", theme.TextMuted, TextStyles.Caption1);

        DrawNavigation(content, theme, scale);
    }

}
