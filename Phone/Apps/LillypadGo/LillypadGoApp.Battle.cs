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

        // The gym-leader intro plays over the battle scene and freezes playback until it finishes.
        if (gymIntroTimer > 0f && gymIntroGym is not null)
        {
            gymIntroTimer -= dt;
            DrawGymIntro(content, theme, gymIntroGym, scale);
            if (gymIntroTimer <= 0f)
            {
                gymIntroGym = null;
            }

            return;
        }

        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, true);
        // Confine weather/terrain FX to the arena above the message panel so particles never spill
        // down behind the battle text box.
        var arenaRect = new Rect(content.Min, new Vector2(content.Max.X, content.Max.Y - content.Height * 0.32f));
        drawList.PushClipRect(arenaRect.Min, arenaRect.Max, true);
        DrawFieldEffects(drawList, arenaRect, battle.Weather, battle.Terrain, time, scale);
        drawList.PopClipRect();
        var player = displayedPlayer ?? battle.Active;
        playerAnim.Update(dt);
        wildAnim.Update(dt);
        AdvancePlayback(dt);
        UpdateBattlePopups(dt);
        UpdateMoveFx(dt);

        // Ease the HP/XP bars toward their target values so damage/heals/xp glide.
        var barLerp = 1f - MathF.Exp(-dt * 10f);
        animatedWildHp += (displayedWildHp - animatedWildHp) * barLerp;
        animatedPlayerHp += (displayedPlayerHp - animatedPlayerHp) * barLerp;
        if (displayedPlayerXpFraction < animatedPlayerXp - 0.25f)
        {
            // A level-up reset the fraction; snap rather than sweeping the bar backwards.
            animatedPlayerXp = displayedPlayerXpFraction;
        }
        else
        {
            animatedPlayerXp += (displayedPlayerXpFraction - animatedPlayerXp) * barLerp;
        }

        // Positions share the same normalized arena used by BiomeBackdrop, keeping creatures on
        // the imported Showdown scene instead of drifting as the phone size changes.
        MoveAnims.Preload();
        var wildBase = BiomeBackdrop.BattlePoint(content, 0.76f, 0.34f);
        var playerBase = BiomeBackdrop.BattlePoint(content, 0.24f, 0.76f);
        var sceneMap = new SceneMap(playerBase, wildBase);
        var shakeY = MoveFxShakeY(sceneMap);
        if (shakeY != 0f)
        {
            wildBase.Y += shakeY;
            playerBase.Y += shakeY;
            sceneMap = new SceneMap(playerBase, wildBase);
        }

        // Background flashes (behind the creatures, like Showdown's $bgEffect layer).
        DrawMoveBgFx(drawList, content);

        var wildAnimPose = MonAnimPose(false, sceneMap);
        var wildPos = wildBase + wildAnimPose.Offset;
        DrawGroundShadow(drawList, wildBase + new Vector2(wildAnimPose.Offset.X, 32f * scale), 40f * scale);

        // Capture animation: while the wild is inside the ball it fades out and shrinks toward it.
        var capWildAlpha = 1f;
        var capWildScale = 1f;
        var capWildPos = wildPos;
        var sendOutAlpha = enemyAwaitingSendOut ? 0f : 1f;
        var sendOutScale = 1f;
        var ballPos = Vector2.Zero;
        var ballAngle = 0f;
        var ballFlash = 0f;
        var ballVisible = false;
        var sendOutBallVisible = false;
        var sendOutBallPos = wildBase;
        var sendOutBallAngle = 0f;
        var sendOutBallFlash = 0f;
        if (captureFx is { } cap)
        {
            cap.Age += dt;
            if (cap.Phase is CaptureFx.Stage.Success or CaptureFx.Stage.Break)
            {
                cap.StageAge += dt;
            }

            var ground = wildBase + new Vector2(-4f * scale, 20f * scale);
            var throwStart = playerBase + new Vector2(-6f * scale, -30f * scale);
            switch (cap.Phase)
            {
                case CaptureFx.Stage.Throw:
                    ballVisible = true;
                    if (cap.Age < 0.45f)
                    {
                        var t = cap.Age / 0.45f;
                        ballPos = Vector2.Lerp(throwStart, wildPos, t) -
                            new Vector2(0f, MathF.Sin(t * MathF.PI) * 64f * scale);
                        ballAngle = t * 9f;
                    }
                    else if (cap.Age < 0.95f)
                    {
                        ballPos = wildPos;
                        ballFlash = 1f - (cap.Age - 0.45f) / 0.5f;
                        var t = Math.Clamp((cap.Age - 0.52f) / 0.42f, 0f, 1f);
                        capWildAlpha = 1f - t;
                        capWildScale = 1f - t * 0.9f;
                        capWildPos = Vector2.Lerp(wildPos, ballPos, t);
                    }
                    else
                    {
                        var t = Math.Clamp((cap.Age - 0.95f) / 0.32f, 0f, 1f);
                        ballPos = Vector2.Lerp(wildPos, ground, t * t); // fall with a little gravity
                        capWildAlpha = 0f;
                    }

                    break;
                case CaptureFx.Stage.Wait:
                    ballVisible = true;
                    ballPos = ground;
                    capWildAlpha = 0f;
                    var wa = cap.Age - cap.LastShake;
                    if (wa is >= 0f and < 0.5f)
                    {
                        ballAngle = MathF.Sin(wa * 20f) * 0.6f * MathF.Exp(-wa * 4.5f);
                    }

                    break;
                case CaptureFx.Stage.Success:
                    ballVisible = true;
                    ballPos = ground;
                    capWildAlpha = 0f;
                    break;
                default: // Break free
                    ballVisible = cap.StageAge < 0.22f;
                    ballPos = ground;
                    ballFlash = Math.Clamp(1f - cap.StageAge / 0.3f, 0f, 1f);
                    capWildAlpha = Math.Clamp(cap.StageAge / 0.32f, 0f, 1f);
                    if (cap.StageAge > 0.6f)
                    {
                        captureFx = null;
                    }

                    break;
            }
        }

        if (sendOutFx is { } sendOut)
        {
            sendOut.Age += dt;
            const float releaseStart = 0.38f;
            const float releaseEnd = 0.78f;
            // The ball is already on the opponent's side before it opens. Keeping it fixed here
            // makes the creature visibly emerge from the ball instead of crossing paths with it.
            sendOutBallPos = wildBase + new Vector2(-4f * scale, 18f * scale);
            sendOutBallAngle = sendOut.Age < 0.16f ? 0f : MathF.Sin((sendOut.Age - 0.16f) * 18f) * 0.11f;
            sendOutBallVisible = sendOut.Age < releaseStart + 0.12f;
            sendOutBallFlash = Math.Clamp(1f - MathF.Abs(sendOut.Age - releaseStart) / 0.16f, 0f, 1f);
            if (sendOut.Age < releaseStart)
            {
                sendOutAlpha = 0f;
                sendOutScale = 0.15f;
                capWildPos = sendOutBallPos;
            }
            else
            {
                var reveal = Math.Clamp((sendOut.Age - releaseStart) / (releaseEnd - releaseStart), 0f, 1f);
                sendOutAlpha = reveal * reveal * (3f - 2f * reveal);
                sendOutScale = 0.15f + sendOutAlpha * 0.85f;
                capWildPos = Vector2.Lerp(sendOutBallPos, wildPos, sendOutAlpha);
            }

            if (sendOut.Age >= SendOutFx.Duration)
            {
                sendOutFx = null;
            }
        }

        var wildHidden = battle.Wild.SemiInvulnerable ? 0.2f : 1f; // underground / in the air during a charge
        MonsterArt.Draw(drawList, capWildPos, 42f * scale * wildAnimPose.ScaleMul * capWildScale * sendOutScale,
            battle.Wild.Species, -1f,
            new MonsterPose(time, wildAnim.Lunge, wildAnim.Hurt,
                wildAnim.Alpha * wildAnimPose.Alpha * wildHidden * capWildAlpha * sendOutAlpha, displayedWildHp <= 0),
            back: false);
        if (sendOutAlpha > 0f)
        {
            DrawStatusFx(drawList, wildPos, displayedWildStatus, time, scale);
            if (battle.Wild.ConfusionTurns > 0 && displayedWildHp > 0)
            {
                DrawConfusionFx(drawList, wildPos, time, scale);
            }
        }

        DrawImpactFx(drawList, wildPos, wildAnim.Hurt, Elements.Color(battle.Wild.Element), scale);
        var wildPanel = new Rect(new Vector2(content.Min.X + 8f * scale, content.Min.Y + 18f * scale),
            new Vector2(content.Min.X + 188f * scale, content.Min.Y + 78f * scale));
        DrawStatusPanel(drawList, wildPanel.Min, wildPanel.Max, battle.Wild, animatedWildHp, displayedWildStatus,
            displayedWildAtkStage, displayedWildDefStage, displayedWildSpAtkStage, displayedWildSpDefStage,
            displayedWildSpdStage, displayedWildAccuracyStage, displayedWildEvasionStage,
            displayedWildLevel, 0f, false, theme, scale);
        if (wildPanel.Contains(ImGui.GetMousePos()))
        {
            string note;
            if (battle.IsTrainerBattle)
            {
                note = $"{battle.TrainerName}'s Pokémon.";
            }
            else
            {
                var caughtBefore = State.Party.Concat(State.Box)
                    .Any(m => m.Species.Id == battle.Wild.Species.Id);
                note = caughtBefore
                    ? "Wild opponent.\n✓ Already in your collection."
                    : "Wild opponent.\n★ New species — not caught yet!";
            }

            ShowTooltip(BuildMonsterTooltip(battle.Wild, note + BattleWeatherNote(battle.Wild),
                displayedWildHp));
        }

        // Player active (bottom).
        var playerAnimPose = MonAnimPose(true, sceneMap);
        var playerPos = playerBase + playerAnimPose.Offset;
        DrawGroundShadow(drawList, playerBase + new Vector2(playerAnimPose.Offset.X, 36f * scale), 44f * scale);
        var playerHidden = battle.Active.SemiInvulnerable ? 0.2f : 1f;
        MonsterArt.Draw(drawList, playerPos, 46f * scale * playerAnimPose.ScaleMul, player.Species, 1f,
            new MonsterPose(time, playerAnim.Lunge, playerAnim.Hurt,
                playerAnim.Alpha * playerAnimPose.Alpha * playerHidden, displayedPlayerHp <= 0),
            back: true);
        DrawStatusFx(drawList, playerPos, displayedPlayerStatus, time + 0.8f, scale);
        if (player.ConfusionTurns > 0 && displayedPlayerHp > 0)
        {
            DrawConfusionFx(drawList, playerPos, time + 0.4f, scale);
        }

        DrawImpactFx(drawList, playerPos, playerAnim.Hurt, Elements.Color(player.Element), scale);
        var playerPanel = new Rect(
            new Vector2(content.Max.X - 194f * scale, content.Min.Y + content.Height * 0.5f),
            new Vector2(content.Max.X - 8f * scale, content.Min.Y + content.Height * 0.5f + 68f * scale));
        DrawStatusPanel(drawList, playerPanel.Min, playerPanel.Max, player, animatedPlayerHp, displayedPlayerStatus,
            displayedPlayerAtkStage, displayedPlayerDefStage, displayedPlayerSpAtkStage, displayedPlayerSpDefStage,
            displayedPlayerSpdStage, displayedPlayerAccuracyStage, displayedPlayerEvasionStage,
            displayedPlayerLevel, animatedPlayerXp, true, theme, scale);
        if (playerPanel.Contains(ImGui.GetMousePos()))
        {
            ShowTooltip(BuildMonsterTooltip(player, "Your active creature." + BattleWeatherNote(player),
                displayedPlayerHp));
        }

        if (ballVisible && captureFx is { } capDraw)
        {
            DrawCaptureBall(drawList, ballPos, ballAngle, ballFlash, capDraw, scale);
        }

        if (sendOutBallVisible)
        {
            DrawSendOutBall(drawList, sendOutBallPos, sendOutBallAngle, sendOutBallFlash, scale);
        }

        DrawWeatherChip(content, theme, scale);
        DrawBattleIndicators(content, theme, scale);
        DrawMoveFx(drawList, content, playerBase, wildBase, sceneMap, scale);
        DrawBattlePopups(wildPos, playerPos, theme, scale);

        // Once the battle is decided, the Victory / Defeat / Forfeit screen takes over the bottom
        // message box; the arena and creatures stay visible above it.
        if (awaitingResult)
        {
            var resultTop = content.Max.Y - content.Height * 0.32f;
            var resultPanel = new Rect(new Vector2(content.Min.X + 6f * scale, resultTop),
                new Vector2(content.Max.X - 6f * scale, content.Max.Y - 6f * scale));
            var prevResultInteractive = LgUi.Interactive;
            if (time < suppressBattleButtonsUntil)
            {
                LgUi.Interactive = false;
            }

            DrawResult(resultPanel, theme, scale);
            LgUi.Interactive = prevResultInteractive;
            return;
        }

        // Bottom panel: message, action menu, or result — the navy combat box from the UI Update kit.
        var panelTop = content.Max.Y - content.Height * 0.32f;
        var panelMin = new Vector2(content.Min.X + 6f * scale, panelTop);
        var panelMax = new Vector2(content.Max.X - 6f * scale, content.Max.Y - 6f * scale);
        DrawCombatBox(drawList, panelMin, panelMax, scale);
        var panel = new Rect(panelMin, panelMax);

        var historyButton = new Rect(new Vector2(panelMax.X - 58f * scale, panelMin.Y + 5f * scale),
            new Vector2(panelMax.X - 8f * scale, panelMin.Y + 28f * scale));
        if (State.BattleLogEnabled && RosterUi.ColorButton(historyButton, "LOG",
                showBattleHistory ? RosterUi.Green : RosterUi.Blue, scale, true))
        {
            showBattleHistory = !showBattleHistory;
        }

        // Freeze the panel's buttons briefly after each message (see suppressBattleButtonsUntil).
        var prevInteractive = LgUi.Interactive;
        if (time < suppressBattleButtonsUntil)
        {
            LgUi.Interactive = false;
        }

        if (message is not null)
        {
            DrawMessage(panel, theme, scale);
        }
        else if (battle.PendingMoveChoice is not null)
        {
            DrawMoveLearnMenu(panel, theme, scale);
        }
        else
        {
            DrawActionMenu(panel, theme, scale);
        }

        LgUi.Interactive = prevInteractive;

        if (showBattleHistory)
        {
            DrawBattleHistory(panel, theme, scale);
        }
    }

    // The battle's bottom box: navy gradient, dark outline, light inner hairline and a top shine,
    // matching the app-wide header/nav chrome.
    private static void DrawCombatBox(ImDrawListPtr drawList, Vector2 min, Vector2 max, float scale)
    {
        var radius = 12f * scale;
        drawList.AddRectFilled(min + new Vector2(0f, 2.5f * scale), max + new Vector2(0f, 2.5f * scale),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.25f)), radius);
        Squircle.FillVerticalGradient(drawList, min, max, radius,
            ImGui.GetColorU32(RosterUi.NavyTop with { W = 0.97f }),
            ImGui.GetColorU32(RosterUi.NavyBottom with { W = 0.97f }));
        Squircle.Stroke(drawList, min, max, radius, ImGui.GetColorU32(RosterUi.NavyEdge), 2f * scale);
        Squircle.Stroke(drawList, min + new Vector2(2f, 2f) * scale, max - new Vector2(2f, 2f) * scale,
            radius - 2f * scale, ImGui.GetColorU32(RosterUi.NavyLine with { W = 0.35f }), 1f * scale);
        drawList.AddLine(new Vector2(min.X + radius, min.Y + 2f * scale),
            new Vector2(max.X - radius, min.Y + 2f * scale),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.18f)), 1.2f * scale);
    }

    private void AdvancePlayback(float dt)
    {
        if (battle is null)
        {
            return;
        }

        if (message is not null)
        {
            // Keep the menu buttons inert for a beat after messages so the tap that advances text
            // doesn't also trigger an action or a move-learn choice.
            suppressBattleButtonsUntil = time + 0.22f;
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
            if (State.BattleLogEnabled && !string.IsNullOrWhiteSpace(msg.Text))
            {
                battleHistory.Add(msg.Text);
                while (battleHistory.Count > 100)
                {
                    battleHistory.RemoveAt(0);
                }
            }

            var baseDuration = msg.Cue switch
            {
                BattleCue.CaptureThrow => 1.7f,  // ball arcs in, opens, the wild is drawn in, ball drops
                BattleCue.CaptureShake => 0.75f, // a dramatic beat between each shake
                _ => Math.Clamp(0.9f + msg.Text.Length * 0.025f, 1.15f, 2.4f),
            };
            messageTimer = baseDuration / Math.Clamp(State.BattleSpeed, 0.5f, 2.5f);
            return;
        }

        // A trainer's Pokémon fainted and the faint has finished animating: send out the next.
        if (battle.RequiresEnemySend)
        {
            battle.SendNextEnemy();
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
                // The traced Showdown anim includes the attacker's own movement; only fall
                // back to the procedural lunge when no anim drives the move.
                if (!BeginMoveFx(battleMessage, true))
                {
                    playerAnim.Lunge = 1f;
                }

                break;
            case BattleCue.WildAttack:
                if (!BeginMoveFx(battleMessage, false))
                {
                    wildAnim.Lunge = 1f;
                }

                break;
            case BattleCue.PlayerHurt:
                playerAnim.Hurt = 1f;
                break;
            case BattleCue.WildHurt:
                wildAnim.Hurt = 1f;
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
                animatedPlayerHp = displayedPlayerHp;
                animatedPlayerXp = displayedPlayerXpFraction;
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
            case BattleCue.Evolve:
                // Only the on-screen active creature gets the popup + entrance; a benched XP-Share
                // evolution just shows its text line.
                if (ReferenceEquals(battleMessage.Subject, displayedPlayer))
                {
                    battlePopups.Add(new BattlePopup
                    {
                        OnWild = false,
                        Value = "EVOLVED!",
                        Label = displayedPlayer?.Species.Name ?? string.Empty,
                        Color = Accent,
                    });
                    playerAnim.Reset();
                }

                break;
            case BattleCue.WildFaint:
                wildAnim.AlphaTarget = 0.35f;
                break;
            case BattleCue.EnemySwitch:
                // A trainer sent out their next Pokémon: the generic StateAfter block above already
                // refreshed the panel; reset the entrance animation so it appears at full opacity.
                animatedWildHp = displayedWildHp;
                wildAnim.Reset();
                enemyAwaitingSendOut = false;
                sendOutFx = new SendOutFx();
                break;
            case BattleCue.CaptureThrow:
                captureFx = new CaptureFx { BallId = pendingCaptureBallId };
                break;
            case BattleCue.CaptureShake:
                if (captureFx is { } shakeFx)
                {
                    shakeFx.Phase = CaptureFx.Stage.Wait;
                    shakeFx.LastShake = shakeFx.Age;
                    shakeFx.Shakes++;
                }

                break;
            case BattleCue.Captured:
                wildAnim.AlphaTarget = 0f;
                if (captureFx is { } caughtFx)
                {
                    caughtFx.Phase = CaptureFx.Stage.Success;
                    caughtFx.StageAge = 0f;
                }

                break;
            case BattleCue.CaptureFail:
                if (captureFx is { } freeFx)
                {
                    freeFx.Phase = CaptureFx.Stage.Break;
                    freeFx.StageAge = 0f;
                }

                break;
        }

        if (cue == BattleCue.PlayerFaint)
        {
            playerAnim.Alpha = 1f;
        }
    }

    // A centered, hover-for-details weather chip. Hovering it lists exactly what the weather does;
    // hovering a battler additionally shows how its ability reacts to the weather (see BattleWeatherNote).
    private void DrawWeatherChip(Rect content, PhoneTheme theme, float scale)
    {
        if (battle is null || battle.Weather == BattleWeather.None)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var (icon, tone) = battle.Weather switch
        {
            BattleWeather.Sun => (FontAwesomeIcon.Sun, new Vector4(1f, 0.74f, 0.24f, 1f)),
            BattleWeather.Rain => (FontAwesomeIcon.CloudRain, Elements.Color(Element.Water)),
            BattleWeather.Sandstorm => (FontAwesomeIcon.Wind, new Vector4(0.82f, 0.6f, 0.32f, 1f)),
            BattleWeather.Snow => (FontAwesomeIcon.Snowflake, Elements.Color(Element.Ice)),
            _ => (FontAwesomeIcon.Question, Vector4.One),
        };
        var label = battle.WeatherIsSuppressed ? $"{battle.WeatherName} (negated)" : battle.WeatherName;
        var size = Typography.Measure(label, TextStyles.Caption2);
        var w = size.X + 46f * scale;
        var h = 24f * scale;
        // Anchored to the top-right so it never overlaps the enemy HP bar / level (top-left).
        var max = new Vector2(content.Max.X - 10f * scale, content.Min.Y + 14f * scale + h);
        var min = new Vector2(max.X - w, content.Min.Y + 14f * scale);
        var center = (min + max) * 0.5f;
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        Squircle.Fill(drawList, min, max, 8f * scale, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, hovered ? 0.66f : 0.5f)));
        Squircle.Stroke(drawList, min, max, 8f * scale, ImGui.GetColorU32(tone with { W = 0.75f }), 1f * scale);
        ProgressRing.CenterIcon(drawList, new Vector2(min.X + 14f * scale, center.Y), icon, tone, 13f * scale);
        Typography.Draw(new Vector2(min.X + 26f * scale, center.Y - size.Y * 0.5f), label, tone,
            TextStyles.Caption2);
        ProgressRing.CenterIcon(drawList, new Vector2(max.X - 11f * scale, center.Y), FontAwesomeIcon.InfoCircle,
            tone with { W = 0.7f }, 10f * scale);
        if (hovered)
        {
            ShowTooltip($"{battle.WeatherName}\n\n• " + string.Join("\n• ", battle.WeatherSummary()));
        }
    }

    private void DrawBattleIndicators(Rect content, PhoneTheme theme, float scale)
    {
        if (battle is null)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var x = content.Min.X + 10f * scale;
        var y = content.Min.Y + 86f * scale;
        var rowBottom = content.Min.Y + content.Height * 0.28f;
        foreach (var indicator in battle.FieldIndicators())
        {
            var label = $"{indicator.Label}: {indicator.Detail}";
            var width = Typography.Measure(label, TextStyles.Caption2).X + 26f * scale;
            if (x + width > content.Max.X - 10f * scale)
            {
                x = content.Min.X + 10f * scale;
                y += 22f * scale;
            }

            if (y + 19f * scale > rowBottom)
            {
                break;
            }

            var min = new Vector2(x, y);
            var max = new Vector2(x + width, y + 19f * scale);
            var hovered = ImGui.IsMouseHoveringRect(min, max);
            Squircle.Fill(drawList, min, max, 6f * scale,
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, hovered ? 0.7f : 0.48f)));
            Squircle.Stroke(drawList, min, max, 6f * scale,
                ImGui.GetColorU32(indicator.Color with { W = 0.75f }), 1f * scale);
            ProgressRing.CenterIcon(drawList, new Vector2(min.X + 10f * scale, (min.Y + max.Y) * 0.5f),
                FontAwesomeIcon.InfoCircle, indicator.Color, 10f * scale);
            Typography.Draw(new Vector2(min.X + 18f * scale, min.Y + 3f * scale), label,
                hovered ? theme.TextStrong : theme.TextMuted, TextStyles.Caption2);
            if (hovered)
            {
                ShowTooltip($"{indicator.Label}\n{indicator.Detail}");
            }

            x = max.X + 5f * scale;
        }
    }

    private void DrawBattleHistory(Rect panel, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var min = panel.Min + new Vector2(12f * scale, 12f * scale);
        var max = panel.Max - new Vector2(12f * scale, 12f * scale);
        Squircle.Fill(drawList, min, max, 9f * scale, ImGui.GetColorU32(RosterUi.NavyInset with { W = 0.98f }));
        Typography.Draw(min + new Vector2(12f * scale, 10f * scale), "Battle history", theme.TextStrong,
            TextStyles.SubheadlineEmphasized);
        var first = Math.Max(0, battleHistory.Count - 8);
        var y = min.Y + 34f * scale;
        for (var i = first; i < battleHistory.Count; i++)
        {
            Typography.Draw(new Vector2(min.X + 12f * scale, y),
                FitLabel(battleHistory[i], max.X - min.X - 24f * scale, TextStyles.Caption1),
                theme.TextMuted, TextStyles.Caption1);
            y += 19f * scale;
        }
    }

    // A short suffix for a battler's hover tooltip describing how the current weather affects it via
    // its ability/typing. Empty when nothing applies.
    private string BattleWeatherNote(MonsterInstance mon)
    {
        if (battle is null)
        {
            return string.Empty;
        }

        var lines = battle.WeatherAbilityLines(mon);
        return lines.Count == 0 ? string.Empty : $"\n\n{battle.WeatherName}:\n• " + string.Join("\n• ", lines);
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
        displayedPlayerAccuracyStage = state.AccuracyStage;
        displayedPlayerEvasionStage = state.EvasionStage;
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
        displayedWildAccuracyStage = state.AccuracyStage;
        displayedWildEvasionStage = state.EvasionStage;
        displayedWildLevel = state.Level;
    }

    // The pre-gym-battle intro: the leader sweeps in over the arena, holds with their name/badge,
    // then plays their throw-animation frames (from the extracted 5-frame strip) so it reads as
    // winding up and hurling a Poké Ball, before a white flash hands off to the battle (where the
    // first Pokémon is sent out). Leaders without art show a styled placeholder that auto-loads the
    // strip once dropped into Assets/pokemon/leaders/<Leader>_throw.png.
    private const int GymThrowFrames = 5;

    private void DrawGymIntro(Rect content, PhoneTheme theme, GymDef gym, float scale)
    {
        static float EaseOut(float x) => 1f - MathF.Pow(1f - Math.Clamp(x, 0f, 1f), 3f);

        var drawList = ImGui.GetWindowDrawList();
        const float total = 2.8f;
        const float throwStart = 1.85f;
        const float throwDur = 0.75f;
        var elapsed = Math.Clamp(total - gymIntroTimer, 0f, total);
        var slideIn = EaseOut(elapsed / 0.5f);
        var plateIn = EaseOut((elapsed - 0.15f) / 0.5f);
        var throwT = Math.Clamp((elapsed - (total - 0.4f)) / 0.4f, 0f, 1f); // closing flash
        var bob = MathF.Sin(elapsed * 3.4f) * 5f * scale * (1f - Math.Clamp((elapsed - throwStart) / 0.3f, 0f, 1f));
        var typeColor = Elements.Color(gym.Type);

        // Which throw frame to show: hold on the ready pose, then advance 0→4 during the throw window.
        var frame = elapsed < throwStart ? 0
            : Math.Clamp((int)((elapsed - throwStart) / throwDur * GymThrowFrames), 0, GymThrowFrames - 1);

        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, true);
        // Strong dark wash + spotlight so the leader reads clearly and any sheet background blends in.
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.62f)));

        // Type-coloured speed bands sweeping across the scene.
        for (var i = 0; i < 5; i++)
        {
            var yy = content.Min.Y + content.Height * (0.12f + i * 0.16f);
            var sweep = ((elapsed * 260f + i * 70f) % (content.Width + 240f * scale)) - 120f * scale;
            var x = content.Min.X + sweep;
            drawList.AddLine(new Vector2(x, yy), new Vector2(x + 120f * scale, yy - 26f * scale),
                ImGui.GetColorU32(typeColor with { W = 0.22f }), 10f * scale);
        }

        // Leader, sliding in from the right, playing the throw strip frame-by-frame near the end.
        var offX = (1f - slideIn) * content.Width * 0.7f;
        if (AssetTextures.TryGet($"leaders/{gym.Leader}_throw.png", out var tex, out var stripAspect))
        {
            var cellAspect = stripAspect / GymThrowFrames; // strip is GymThrowFrames equal cells
            var h = content.Height * 0.66f;
            var w = h * cellAspect;
            var rawCenter = new Vector2(content.Max.X - w * 0.5f - 18f * scale + offX,
                content.Max.Y - h * 0.5f - 6f * scale + bob);
            var centerX = content.Width >= w + 16f * scale
                ? Math.Clamp(rawCenter.X, content.Min.X + w * 0.5f + 8f * scale,
                    content.Max.X - w * 0.5f - 8f * scale)
                : content.Center.X;
            var centerY = content.Height >= h + 16f * scale
                ? Math.Clamp(rawCenter.Y, content.Min.Y + h * 0.5f + 8f * scale,
                    content.Max.Y - h * 0.5f - 8f * scale)
                : content.Center.Y;
            var center = new Vector2(centerX, centerY);
            // Soft dark spotlight behind the leader to hide any residual sheet background.
            drawList.AddCircleFilled(center, w * 0.62f, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.28f)));
            var min = center - new Vector2(w * 0.5f, h * 0.5f);
            var max = center + new Vector2(w * 0.5f, h * 0.5f);
            var uv0 = new Vector2(frame / (float)GymThrowFrames, 0f);
            var uv1 = new Vector2((frame + 1) / (float)GymThrowFrames, 1f);
            drawList.AddImage(tex, min, max, uv0, uv1, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, slideIn)));
        }
        else
        {
            // Placeholder card until the leader's art is added.
            var w = content.Width * 0.42f;
            var h = content.Height * 0.54f;
            var rawMax = new Vector2(content.Max.X - 16f * scale + offX, content.Max.Y - 16f * scale + bob);
            var maxX = content.Width >= w + 16f * scale
                ? Math.Clamp(rawMax.X, content.Min.X + w + 8f * scale, content.Max.X - 8f * scale)
                : content.Center.X + w * 0.5f;
            var maxY = content.Height >= h + 16f * scale
                ? Math.Clamp(rawMax.Y, content.Min.Y + h + 8f * scale, content.Max.Y - 8f * scale)
                : content.Center.Y + h * 0.5f;
            var max = new Vector2(maxX, maxY);
            var min = new Vector2(max.X - w, max.Y - h);
            RosterUi.DarkCard(drawList, new Rect(min, max), 16f * scale, scale);
            Squircle.Stroke(drawList, min, max, 16f * scale, ImGui.GetColorU32(typeColor with { W = 0.6f }),
                2f * scale);
            var c = new Vector2((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f);
            ProgressRing.CenterIcon(drawList, c, FontAwesomeIcon.UserNinja, typeColor with { W = slideIn }, 64f * scale);
        }

        // Name plate sliding in from the left.
        var plateX = content.Min.X + 14f * scale - (1f - plateIn) * content.Width * 0.5f;
        var plateMin = new Vector2(plateX, content.Min.Y + content.Height * 0.16f);
        var plateMax = new Vector2(plateX + content.Width * 0.62f, plateMin.Y + 96f * scale);
        RosterUi.DarkCard(drawList, new Rect(plateMin, plateMax), 12f * scale, scale, accent: typeColor);
        var innerX = plateMin.X + 16f * scale;
        Typography.Draw(new Vector2(innerX, plateMin.Y + 10f * scale), "GYM LEADER",
            GamePalette.Lighten(typeColor, 0.2f), TextStyles.Caption2);
        Typography.Draw(new Vector2(innerX, plateMin.Y + 26f * scale),
            FitLabel(gym.Leader, plateMax.X - innerX - 48f * scale, TextStyles.Title2), RosterUi.CardInk,
            TextStyles.Title2);
        Typography.Draw(new Vector2(innerX, plateMin.Y + 58f * scale),
            FitLabel($"{gym.City}  ·  {Elements.Name(gym.Type)} Gym", plateMax.X - innerX - 48f * scale,
                TextStyles.Caption1), RosterUi.CardMuted, TextStyles.Caption1);
        Typography.Draw(new Vector2(innerX, plateMax.Y - 20f * scale), "wants to battle!",
            RosterUi.CardInk with { W = 0.9f }, TextStyles.Caption1);

        // The leader's actual gym badge on the plate.
        if (GymBadge(gym, out var badgeTex, out var badgeAspect))
        {
            var bw = 40f * scale;
            var bh = bw / MathF.Max(0.01f, badgeAspect);
            var bc = new Vector2(plateMax.X - 30f * scale, plateMin.Y + 34f * scale);
            drawList.AddImage(badgeTex, bc - new Vector2(bw * 0.5f, bh * 0.5f),
                bc + new Vector2(bw * 0.5f, bh * 0.5f));
        }

        // Closing flash that hands off to the battle.
        if (throwT > 0f)
        {
            drawList.AddRectFilled(content.Min, content.Max,
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, throwT * throwT * 0.6f)));
        }
    }

}
