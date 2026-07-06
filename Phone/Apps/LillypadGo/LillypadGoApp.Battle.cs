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
    // ---- Battle ---------------------------------------------------------------------

    private void DrawBattle(Rect content, PhoneTheme theme, float dt)
    {
        if (battle is null)
        {
            view = View.Map;
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, true);
        var player = displayedPlayer ?? battle.Active;
        playerAnim.Update(dt);
        wildAnim.Update(dt);
        AdvancePlayback(dt);
        UpdateBattlePopups(dt);
        UpdateMoveFx(dt);

        // Positions share the same normalized arena used by BiomeBackdrop, keeping creatures on
        // the imported Showdown scene instead of drifting as the phone size changes.
        var wildPos = BiomeBackdrop.BattlePoint(content, 0.76f, 0.34f);
        DrawGroundShadow(drawList, wildPos + new Vector2(0f, 32f * scale), 40f * scale);
        MonsterArt.Draw(drawList, wildPos, 42f * scale, battle.Wild.Species, -1f,
            new MonsterPose(time, wildAnim.Lunge, wildAnim.Hurt, wildAnim.Alpha, displayedWildHp <= 0));
        DrawStatusFx(drawList, wildPos, displayedWildStatus, time, scale);
        DrawImpactFx(drawList, wildPos, wildAnim.Hurt, Elements.Color(battle.Wild.Element), scale);
        var wildPanel = new Rect(new Vector2(content.Min.X + 8f * scale, content.Min.Y + 18f * scale),
            new Vector2(content.Min.X + 188f * scale, content.Min.Y + 78f * scale));
        DrawStatusPanel(drawList, wildPanel.Min, wildPanel.Max, battle.Wild, displayedWildHp, displayedWildStatus,
            displayedWildAtkStage, displayedWildDefStage, displayedWildSpAtkStage, displayedWildSpDefStage,
            displayedWildSpdStage, displayedWildLevel, 0f, false, theme, scale);
        if (wildPanel.Contains(ImGui.GetMousePos()))
        {
            ImGui.SetTooltip(BuildMonsterTooltip(battle.Wild, "Wild opponent.", displayedWildHp));
        }

        // Player active (bottom).
        var playerPos = BiomeBackdrop.BattlePoint(content, 0.24f, 0.76f);
        DrawGroundShadow(drawList, playerPos + new Vector2(0f, 36f * scale), 44f * scale);
        MonsterArt.Draw(drawList, playerPos, 46f * scale, player.Species, 1f,
            new MonsterPose(time, playerAnim.Lunge, playerAnim.Hurt, playerAnim.Alpha, displayedPlayerHp <= 0),
            back: true);
        DrawStatusFx(drawList, playerPos, displayedPlayerStatus, time + 0.8f, scale);
        DrawImpactFx(drawList, playerPos, playerAnim.Hurt, Elements.Color(player.Element), scale);
        var playerPanel = new Rect(
            new Vector2(content.Max.X - 194f * scale, content.Min.Y + content.Height * 0.5f),
            new Vector2(content.Max.X - 8f * scale, content.Min.Y + content.Height * 0.5f + 68f * scale));
        DrawStatusPanel(drawList, playerPanel.Min, playerPanel.Max, player, displayedPlayerHp, displayedPlayerStatus,
            displayedPlayerAtkStage, displayedPlayerDefStage, displayedPlayerSpAtkStage, displayedPlayerSpDefStage,
            displayedPlayerSpdStage, displayedPlayerLevel, displayedPlayerXpFraction, true, theme, scale);
        if (playerPanel.Contains(ImGui.GetMousePos()))
        {
            ImGui.SetTooltip(BuildMonsterTooltip(player, "Your active creature.", displayedPlayerHp));
        }

        DrawMoveFx(drawList, content, playerPos, wildPos, scale);
        DrawBattlePopups(wildPos, playerPos, theme, scale);

        // Bottom panel: message, action menu, or result.
        var panelTop = content.Max.Y - content.Height * 0.32f;
        var panelMin = new Vector2(content.Min.X + 6f * scale, panelTop);
        var panelMax = new Vector2(content.Max.X - 6f * scale, content.Max.Y - 6f * scale);
        Elevation.Draw(drawList, panelMin, panelMax, 14f * scale, scale, 14f, -5f, 0.26f);
        Squircle.FillVerticalGradient(drawList, panelMin, panelMax, 14f * scale,
            ImGui.GetColorU32(GamePalette.Lighten(GamePalette.Board, 0.05f) with { W = 0.94f }),
            ImGui.GetColorU32(GamePalette.Darken(GamePalette.Board, 0.16f) with { W = 0.94f }));
        Squircle.Stroke(drawList, panelMin, panelMax, 14f * scale,
            ImGui.GetColorU32(Accent with { W = 0.34f }), 1.2f * scale);
        drawList.AddLine(new Vector2(panelMin.X + 16f * scale, panelMin.Y + 1f * scale),
            new Vector2(panelMax.X - 16f * scale, panelMin.Y + 1f * scale),
            ImGui.GetColorU32(Accent with { W = 0.58f }), 2f * scale);
        var panel = new Rect(panelMin, panelMax);

        if (message is not null)
        {
            DrawMessage(panel, theme, scale);
        }
        else if (battle.PendingMoveChoice is not null)
        {
            DrawMoveLearnMenu(panel, theme, scale);
        }
        else if (awaitingResult)
        {
            DrawResult(panel, theme, scale);
        }
        else
        {
            DrawActionMenu(panel, theme, scale);
        }
    }

    private void AdvancePlayback(float dt)
    {
        if (battle is null)
        {
            return;
        }

        if (message is not null)
        {
            for (var i = 0; i < battleText.Count; i++)
            {
                battleText[i] = battleText[i] with { Age = battleText[i].Age + dt };
            }

            messageTimer -= dt;
            var tapped = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
            if (messageTimer > 0f && !tapped)
            {
                return;
            }

            // Continue in this frame so the next queued message or result replaces the
            // old one directly. Returning here briefly exposed the action menu between
            // consecutive messages, which looked like a bright flash.
            message = null;
        }

        if (battle.Log.Count > 0)
        {
            var msg = battle.Log.Dequeue();
            ApplyCue(msg);
            message = msg.Text;
            AddBattleText(msg);
            messageTimer = msg.Cue == BattleCue.CaptureShake
                ? 0.45f
                : Math.Clamp(0.9f + msg.Text.Length * 0.025f, 1.15f, 2.4f);
            return;
        }

        if (battle.RequiresSwitch)
        {
            menu = Menu.Switch;
        }

        awaitingResult = battle.PendingMoveChoice is null && battle.Outcome != BattleOutcome.Ongoing;
    }

    private void ApplyCue(BattleMessage battleMessage)
    {
        var cue = battleMessage.Cue;
        var previousPlayerLevel = displayedPlayerLevel;
        if (battleMessage.HpAfter is { } hp && battleMessage.Subject is { } subject)
        {
            if (ReferenceEquals(subject, battle?.Wild))
            {
                AddBattlePopup(true, hp - displayedWildHp, battleMessage);
                displayedWildHp = hp;
            }
            else if (ReferenceEquals(subject, displayedPlayer))
            {
                AddBattlePopup(false, hp - displayedPlayerHp, battleMessage);
                displayedPlayerHp = hp;
            }
        }

        if (battleMessage.StateAfter is { } state && battleMessage.Subject is { } stateSubject)
        {
            if (ReferenceEquals(stateSubject, battle?.Wild))
            {
                SetDisplayedWild(state);
            }
            else if (ReferenceEquals(stateSubject, displayedPlayer))
            {
                SetDisplayedPlayer(state);
            }
        }

        switch (cue)
        {
            case BattleCue.PlayerAttack:
                playerAnim.Lunge = 1f;
                BeginMoveFx(battleMessage, true);
                break;
            case BattleCue.WildAttack:
                wildAnim.Lunge = 1f;
                BeginMoveFx(battleMessage, false);
                break;
            case BattleCue.PlayerHurt:
                playerAnim.Hurt = 1f;
                moveFx = null;
                break;
            case BattleCue.WildHurt:
                wildAnim.Hurt = 1f;
                moveFx = null;
                break;
            case BattleCue.PlayerFaint:
                playerAnim.AlphaTarget = 0.35f;
                break;
            case BattleCue.PlayerSwitch:
                displayedPlayer = battleMessage.Subject ?? battle?.Active;
                if (battleMessage.StateAfter is { } switchState)
                {
                    SetDisplayedPlayer(switchState);
                }
                else
                {
                    displayedPlayerHp = battleMessage.HpAfter ?? displayedPlayer?.CurrentHp ?? 0;
                }
                playerAnim.Reset();
                break;
            case BattleCue.XpGain when displayedPlayerLevel > previousPlayerLevel:
                battlePopups.Add(new BattlePopup
                {
                    OnWild = false,
                    Value = "LEVEL UP",
                    Label = "LV " + displayedPlayerLevel,
                    Color = Accent,
                });
                break;
            case BattleCue.WildFaint:
                wildAnim.AlphaTarget = 0.35f;
                break;
            case BattleCue.Captured:
                wildAnim.AlphaTarget = 0f;
                break;
        }

        if (cue == BattleCue.PlayerFaint)
        {
            playerAnim.Alpha = 1f;
        }
    }

    private void SetDisplayedPlayer(BattleSnapshot state)
    {
        displayedPlayerHp = state.Hp;
        displayedPlayerStatus = state.Status;
        displayedPlayerAtkStage = state.AtkStage;
        displayedPlayerDefStage = state.DefStage;
        displayedPlayerSpAtkStage = state.SpAtkStage;
        displayedPlayerSpDefStage = state.SpDefStage;
        displayedPlayerSpdStage = state.SpdStage;
        displayedPlayerLevel = state.Level;
        displayedPlayerXpFraction = state.XpFraction;
    }

    private void SetDisplayedWild(BattleSnapshot state)
    {
        displayedWildHp = state.Hp;
        displayedWildStatus = state.Status;
        displayedWildAtkStage = state.AtkStage;
        displayedWildDefStage = state.DefStage;
        displayedWildSpAtkStage = state.SpAtkStage;
        displayedWildSpDefStage = state.SpDefStage;
        displayedWildSpdStage = state.SpdStage;
        displayedWildLevel = state.Level;
    }

}
