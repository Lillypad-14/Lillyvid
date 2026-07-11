using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using VideoSyncPrototype.Phone.Apps.Games.Framework;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Immerse mode (opt-in, State.ImmersiveModeEnabled): play Lillypad Go in the game world
// without the phone. Wild Pokémon spawn around the player and can be clicked to engage;
// battles run from an on-screen battle hotbar (keys 1–4 = the active battler's moves,
// 5 Bag, 6 Swap, 7 Run, 8 opens the phone), with the same tooltips as the phone app.
//
// This is IN ADDITION to the phone, never a replacement: the battle playback that normally
// only advances while the phone's battle screen draws is ticked headless here whenever the
// phone isn't showing it (TickBattleHeadless mirrors DrawBattle's logic half), so the same
// battle can be driven from either surface at any moment. Keys 1–8 are captured only while
// a battle is live — the player's real game hotbars are never touched.
internal sealed partial class LillypadGoApp
{
    internal static LillypadGoApp? Instance { get; private set; }

    public LillypadGoApp()
    {
        Instance = this;
    }

    private bool appDrewThisFrame;
    private bool battleTickedThisFrame;
    private float immersiveTime;

    // ---- World spawns -----------------------------------------------------------------

    private sealed class WorldSpawn
    {
        private static int nextId;

        public readonly int Id = ++nextId;
        public MonsterInstance Mon = null!;
        public Vector3 Position;
        public Vector3 Home;
        public Vector3 Facing = new(0f, 0f, 1f);
        public Vector3? WanderTo;
        public float NextWanderAt;
        public float DespawnAt;
        public float AnimSeed;
    }

    private const int MaxWorldSpawns = 3;
    private readonly List<WorldSpawn> worldSpawns = new();
    private float nextSpawnCheckAt;
    private uint spawnTerritory;
    private WorldSpawn? engageTarget;

    // Immersive battle UI state.
    // Wide enough that the slot row (moves + utilities) never clips the right edge:
    // 196 info block + 4×(126+5) moves + 8 divider + 4×(54+5) utilities + padding.
    private const float BarWidth = 976f;
    private const float BarContentHeight = 118f;

    private bool immersiveBagOpen;
    private bool immersiveSwapOpen;
    private bool immersiveRunConfirm;
    private bool immersiveLogOpen;
    private bool immersiveBarDragging;
    private readonly bool[] immersiveKeyWasDown = new bool[8];
    private readonly bool[] immersiveKeyPressed = new bool[8];

    // Entry point, called once per frame from the plugin draw (after the phone windows), so
    // immersive play works with the phone closed, open on another app, or mid-battle.
    internal void DrawImmersiveOverlay()
    {
        var appDrew = appDrewThisFrame;
        appDrewThisFrame = false;
        var battleTicked = battleTickedThisFrame;
        battleTickedThisFrame = false;

        if (!State.ImmersiveModeEnabled)
        {
            if (worldSpawns.Count > 0)
            {
                worldSpawns.Clear();
                engageTarget = null;
                WorldSpawnStage.Clear();
            }

            return;
        }

        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.25f);
        immersiveTime += dt;

        if (battle is not null)
        {
            if (!battleTicked)
            {
                // The phone isn't showing the battle this frame: advance playback here so
                // the fight keeps running in the world. `time` normally advances in Draw.
                if (!appDrew)
                {
                    time += dt;
                }

                TickBattleHeadless(dt);
            }

            if (worldSpawns.Count > 0)
            {
                worldSpawns.Clear();
                engageTarget = null;
                WorldSpawnStage.Clear();
            }
        }
        else
        {
            UpdateWorldSpawns(dt);
        }

        DrawImmersiveInterface();
    }

    // ---- Headless battle tick -----------------------------------------------------------
    // Mirrors the logic half of DrawBattle (playback, anims, capture/send-out aging, world
    // publish) with none of the phone drawing. NOTE: the capture/send-out timings here must
    // stay in sync with DrawBattle's.
    private void TickBattleHeadless(float dt)
    {
        if (battle is null)
        {
            return;
        }

        if (gymIntroTimer > 0f && gymIntroGym is not null)
        {
            gymIntroTimer -= dt;
            if (gymIntroTimer <= 0f)
            {
                gymIntroGym = null;
            }

            return;
        }

        var player = displayedPlayer ?? battle.Active;
        playerAnim.Update(dt);
        wildAnim.Update(dt);
        AdvancePlayback(dt);
        UpdateBattlePopups(dt);
        UpdateMoveFx(dt);
        if (battleEvolutionPulse > 0f)
        {
            battleEvolutionPulse -= dt;
        }

        var barLerp = 1f - MathF.Exp(-dt * 10f);
        animatedWildHp += (displayedWildHp - animatedWildHp) * barLerp;
        animatedPlayerHp += (displayedPlayerHp - animatedPlayerHp) * barLerp;

        var capWildAlpha = 1f;
        var capWildScale = 1f;
        var sendOutAlpha = enemyAwaitingSendOut ? 0f : 1f;
        var sendOutScale = 1f;
        if (captureFx is { } cap)
        {
            cap.Age += dt;
            if (cap.Phase is CaptureFx.Stage.Success or CaptureFx.Stage.Break)
            {
                cap.StageAge += dt;
            }

            switch (cap.Phase)
            {
                case CaptureFx.Stage.Throw:
                    if (cap.Age is >= 0.45f and < 0.95f)
                    {
                        var t = Math.Clamp((cap.Age - 0.52f) / 0.42f, 0f, 1f);
                        capWildAlpha = 1f - t;
                        capWildScale = 1f - (t * 0.9f);
                    }
                    else if (cap.Age >= 0.95f)
                    {
                        capWildAlpha = 0f;
                    }

                    break;
                case CaptureFx.Stage.Wait:
                case CaptureFx.Stage.Success:
                    capWildAlpha = 0f;
                    break;
                default: // break free
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
            if (sendOut.Age < releaseStart)
            {
                sendOutAlpha = 0f;
                sendOutScale = 0.15f;
            }
            else
            {
                var reveal = Math.Clamp((sendOut.Age - releaseStart) / (releaseEnd - releaseStart), 0f, 1f);
                sendOutAlpha = reveal * reveal * (3f - (2f * reveal));
                sendOutScale = 0.15f + (sendOutAlpha * 0.85f);
            }

            if (sendOut.Age >= SendOutFx.Duration)
            {
                sendOutFx = null;
            }
        }

        var wildHidden = battle.Wild.SemiInvulnerable ? 0.2f : 1f;
        var playerHidden = battle.Active.SemiInvulnerable ? 0.2f : 1f;
        var alphaScale = battle.Wild.IsAlpha ? Alphas.BattleScale : 1f;
        PublishWorldStage(player, playerHidden, wildHidden, capWildAlpha, capWildScale,
            sendOutAlpha, sendOutScale, alphaScale);
    }

    // ---- World spawns -------------------------------------------------------------------

    private void UpdateWorldSpawns(float dt)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null || !State.HasAnyMonster || State.AllMonstersFainted || Towns.IsTown(State.Territory))
        {
            if (worldSpawns.Count > 0)
            {
                worldSpawns.Clear();
                engageTarget = null;
                WorldSpawnStage.Clear();
            }

            return;
        }

        if (State.Territory != spawnTerritory)
        {
            spawnTerritory = State.Territory;
            worldSpawns.Clear();
            engageTarget = null;
        }

        // Lifecycle: despawn far/expired spawns, roll new ones up to the cap.
        for (var i = worldSpawns.Count - 1; i >= 0; i--)
        {
            var spawn = worldSpawns[i];
            if (immersiveTime > spawn.DespawnAt ||
                Vector3.Distance(spawn.Position, player.Position) > 45f)
            {
                if (ReferenceEquals(engageTarget, spawn))
                {
                    engageTarget = null;
                }

                worldSpawns.RemoveAt(i);
            }
        }

        if (immersiveTime >= nextSpawnCheckAt)
        {
            nextSpawnCheckAt = immersiveTime + 6f;
            if (worldSpawns.Count < MaxWorldSpawns && rng.NextDouble() < 0.65)
            {
                RollWorldSpawn(player.Position, player.Rotation);
            }
        }

        // A little life: each spawn ambles around its home point.
        foreach (var spawn in worldSpawns)
        {
            if (spawn.WanderTo is { } wanderTo)
            {
                var delta = wanderTo - spawn.Position;
                delta.Y = 0f;
                var distance = delta.Length();
                if (distance > 0.2f)
                {
                    var direction = delta / distance;
                    spawn.Position += direction * MathF.Min(0.9f * dt, distance);
                    spawn.Facing = direction;
                }
                else
                {
                    spawn.WanderTo = null;
                    spawn.NextWanderAt = immersiveTime + 2f + ((float)rng.NextDouble() * 4f);
                }
            }
            else if (immersiveTime >= spawn.NextWanderAt)
            {
                var angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                var radius = 0.8f + ((float)rng.NextDouble() * 2.2f);
                spawn.WanderTo = spawn.Home + (new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * radius);
            }

            spawn.Position.Y = FollowerRenderer.GroundY(spawn.Position, spawn.Position.Y, tolerance: 8f);
        }

        // Hand the renderable list to the world compositor.
        var snaps = new WorldSpawnStage.SpawnSnap[worldSpawns.Count];
        for (var i = 0; i < worldSpawns.Count; i++)
        {
            var spawn = worldSpawns[i];
            snaps[i] = new WorldSpawnStage.SpawnSnap(spawn.Mon.Species.Id, spawn.Position, spawn.Facing,
                spawn.AnimSeed);
        }

        WorldSpawnStage.Publish(snaps);
        DrawSpawnInteractions();
    }

    // Rolls a wild from the zone's encounter table (same weighting the phone scanner uses)
    // and places it around the player — biased ahead of their facing so travellers meet
    // spawns instead of leaving them all behind, with the rest scattered anywhere.
    private void RollWorldSpawn(Vector3 playerPosition, float playerRotation)
    {
        var table = ArrZones.Encounters(State.Territory, State.CurrentBiome);
        var total = 0;
        foreach (var entry in table)
        {
            total += entry.Weight;
        }

        if (total <= 0)
        {
            return;
        }

        var roll = rng.Next(total);
        foreach (var entry in table)
        {
            roll -= entry.Weight;
            if (roll >= 0)
            {
                continue;
            }

            var species = Dex.Find(entry.SpeciesId);
            if (species is null)
            {
                return;
            }

            var level = rng.Next(entry.MinLevel, entry.MaxLevel + 1);
            float angle;
            float distance;
            if (rng.NextDouble() < 0.7)
            {
                // Ahead of the player, within ~±70° of their facing, a bit further out so
                // they walk up to it.
                angle = playerRotation + (((float)rng.NextDouble() - 0.5f) * 2.4f);
                distance = 12f + ((float)rng.NextDouble() * 12f);
            }
            else
            {
                angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                distance = 8f + ((float)rng.NextDouble() * 12f);
            }

            var position = playerPosition + (new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * distance);
            position.Y = FollowerRenderer.GroundY(position, playerPosition.Y, tolerance: 10f);
            worldSpawns.Add(new WorldSpawn
            {
                Mon = new MonsterInstance(species, level),
                Position = position,
                Home = position,
                Facing = new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)),
                NextWanderAt = immersiveTime + 1f + ((float)rng.NextDouble() * 3f),
                DespawnAt = immersiveTime + 90f + ((float)rng.NextDouble() * 60f),
                AnimSeed = (float)rng.NextDouble() * 10f,
            });
            State.Seen.Add(species.Id);
            return;
        }
    }

    // Hover a world spawn for its info card (like the phone's encounter card); click it to
    // get the Engage prompt. Each spawn gets a real (invisible) ImGui window as its hit
    // target so hover and clicks are captured by ImGui itself — a raw world click is also
    // processed by the game (camera/targeting) and never reliably reaches us.
    private void DrawSpawnInteractions()
    {
        const ImGuiWindowFlags hitFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBackground;
        foreach (var spawn in worldSpawns)
        {
            if (!Plugin.GameGui.WorldToScreen(spawn.Position + new Vector3(0f, 0.55f, 0f), out var center) ||
                !Plugin.GameGui.WorldToScreen(spawn.Position + new Vector3(0f, 1.15f, 0f), out var top))
            {
                continue;
            }

            var radius = Math.Clamp(MathF.Abs(center.Y - top.Y), 20f, 90f);
            ImGui.SetNextWindowPos(center - new Vector2(radius, radius), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(radius * 2f, radius * 2f));
            if (ImGui.Begin($"##lillypadgo-spawn-hit-{spawn.Id}", hitFlags))
            {
                ImGui.SetCursorPos(Vector2.Zero);
                ImGui.InvisibleButton($"##lillypadgo-spawn-btn-{spawn.Id}", new Vector2(radius * 2f, radius * 2f));
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ShowTooltip(BuildSpawnTooltip(spawn.Mon));
                }

                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    engageTarget = spawn;
                }
            }

            ImGui.End();
        }

        if (engageTarget is not { } target || !worldSpawns.Contains(target))
        {
            engageTarget = null;
            return;
        }

        if (!Plugin.GameGui.WorldToScreen(target.Position + new Vector3(0f, 1.3f, 0f), out var anchor))
        {
            return;
        }

        ImGui.SetNextWindowPos(new Vector2(anchor.X - 90f, anchor.Y - 78f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.92f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav;
        if (ImGui.Begin("##lillypadgo-engage", flags))
        {
            ImGui.TextUnformatted($"Wild {target.Mon.Species.Name}  ·  Lv {target.Mon.Level}");
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.24f, 0.6f, 0.32f, 1f));
            if (ImGui.Button("Engage", new Vector2(84f, 26f)))
            {
                var wild = target.Mon;
                worldSpawns.Remove(target);
                engageTarget = null;
                WorldSpawnStage.Clear();
                Engage(wild);
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.Button("Ignore", new Vector2(84f, 26f)))
            {
                engageTarget = null;
            }
        }

        ImGui.End();
    }

    private string BuildSpawnTooltip(MonsterInstance mon)
    {
        var types = mon.SecondaryElement is { } secondary
            ? $"{Elements.Name(mon.Element)} / {Elements.Name(secondary)}"
            : Elements.Name(mon.Element);
        var caught = State.Party.Concat(State.Box).Any(m => m.Species.Id == mon.Species.Id);
        return $"Wild {mon.Species.Name}  ·  Lv {mon.Level}\n{types}\n" +
               (caught ? "✓ Already in your collection." : "★ New species — not caught yet!") +
               "\n\nClick to engage.";
    }

    // ---- Immersive interface (always on while the mode is enabled) -------------------------

    private void DrawImmersiveInterface()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var display = ImGui.GetIO().DisplaySize;
        var introPlaying = gymIntroTimer > 0f && gymIntroGym is not null;
        var battleLive = battle is not null && !introPlaying;

        if (battleLive)
        {
            CollectImmersiveKeys();
            DrawBattleWorldPlates();
            DrawWildWorldTooltip();
        }
        else
        {
            Array.Clear(immersiveKeyPressed);
            Array.Clear(immersiveKeyWasDown);
        }

        DrawImmersiveBar(scale, display, battleLive);

        if (battleLive && immersiveLogOpen && State.BattleLogEnabled)
        {
            DrawBattleLogWindow(scale);
        }

        // Sub menus above the bar — held back while battle text is still playing, exactly
        // like the phone's combat box replaces its menus with the message. Re-check the
        // battle itself: CONTINUE inside the bar calls FinishBattle, nulling it mid-frame.
        if (battle is null || !battleLive || message is not null)
        {
            return;
        }

        if (battle.PendingMoveChoice is not null)
        {
            DrawHudMoveLearnPopup(scale, display);
        }
        else if (immersiveSwapOpen || battle.RequiresSwitch)
        {
            DrawHudSwapPopup(scale, display);
        }
        else if (immersiveBagOpen)
        {
            DrawHudBagPopup(scale, display);
        }
        else if (immersiveRunConfirm)
        {
            DrawHudRunConfirm(scale, display);
        }
    }

    // The always-on hotbar, drawn with the app's own chrome (combat box, chunky move cards,
    // RosterUi buttons) so it reads as part of Lillypad Go rather than a debug window.
    // Draggable by its header strip, collapsible to just the header, both persisted.
    private void DrawImmersiveBar(float scale, Vector2 display, bool battleLive)
    {
        var width = BarWidth * scale;
        var headerHeight = 24f * scale;
        var collapsed = State.ImmersiveBarCollapsed;
        var height = collapsed ? headerHeight + 6f * scale : headerHeight + (BarContentHeight * scale);

        // Position: the user's parked spot, else the default bottom-center perch.
        var pos = State.ImmersiveBarX >= 0f && State.ImmersiveBarY >= 0f
            ? new Vector2(State.ImmersiveBarX, State.ImmersiveBarY)
            : new Vector2((display.X - width) * 0.5f,
                display.Y - (headerHeight + (BarContentHeight * scale)) - 44f * scale);
        pos.X = Math.Clamp(pos.X, 0f, MathF.Max(0f, display.X - width));
        pos.Y = Math.Clamp(pos.Y, 0f, MathF.Max(0f, display.Y - height));

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, height));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove;
        if (!ImGui.Begin("##lillypadgo-immersive-bar", flags))
        {
            ImGui.End();
            return;
        }

        var prevInteractive = LgUi.Interactive;
        LgUi.Interactive = true;
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + new Vector2(width, height);
        DrawCombatBox(dl, min, max, scale);

        this.DrawBarHeader(dl, min, max, headerHeight, scale, battleLive, pos, display, width, height);

        if (collapsed)
        {
            // Keys keep working while collapsed; the popups draw independently of the bar.
            if (battleLive)
            {
                this.HandleCollapsedKeys();
            }

            LgUi.Interactive = prevInteractive;
            ImGui.End();
            return;
        }

        var contentTop = min.Y + headerHeight;

        // Left block: animated portrait, name, level and a slim HP readout.
        var mon = battleLive ? displayedPlayer ?? battle!.Active : State.Party.FirstOrDefault(m => !m.Fainted);
        if (mon is not null)
        {
            MonsterArt.Draw(dl, new Vector2(min.X + 38f * scale, contentTop + 59f * scale), 24f * scale,
                mon.Species, 1f, MonsterPose.Idle(time), back: true);
            var infoX = min.X + 72f * scale;
            Typography.Draw(new Vector2(infoX, contentTop + 16f * scale),
                FitLabel(mon.Name, 112f * scale, TextStyles.SubheadlineEmphasized),
                PhoneTheme.Default.TextStrong, TextStyles.SubheadlineEmphasized);
            Typography.Draw(new Vector2(infoX, contentTop + 36f * scale), $"Lv {mon.Level}",
                RosterUi.CardMuted, TextStyles.Caption1);
            var hp = battleLive ? animatedPlayerHp : mon.CurrentHp;
            LgUi.HpBar(dl, new Vector2(infoX, contentTop + 58f * scale),
                new Vector2(infoX + 106f * scale, contentTop + 66f * scale),
                Math.Clamp(hp / MathF.Max(1f, mon.MaxHp), 0f, 1f));
            Typography.Draw(new Vector2(infoX, contentTop + 72f * scale),
                $"{(int)MathF.Round(hp)}/{mon.MaxHp} HP", RosterUi.CardMuted, TextStyles.Caption2);
        }
        else
        {
            Typography.Draw(new Vector2(min.X + 18f * scale, contentTop + 24f * scale), "No healthy",
                RosterUi.CardMuted, TextStyles.Caption1);
            Typography.Draw(new Vector2(min.X + 18f * scale, contentTop + 42f * scale), "Pokémon",
                RosterUi.CardMuted, TextStyles.Caption1);
        }

        // Party pips: one dot per slot, coloured by health, hover for the full profile.
        var pipY = contentTop + 94f * scale;
        for (var i = 0; i < LillypadGoState.PartyLimit; i++)
        {
            var pipCenter = new Vector2(min.X + (20f + (i * 16f)) * scale, pipY);
            if (i < State.Party.Count)
            {
                var member = State.Party[i];
                var fraction = member.MaxHp > 0 ? member.CurrentHp / (float)member.MaxHp : 0f;
                var pipColor = member.Fainted
                    ? new Vector4(0.45f, 0.2f, 0.2f, 1f)
                    : fraction > 0.5f
                        ? new Vector4(0.36f, 0.78f, 0.42f, 1f)
                        : fraction > 0.22f
                            ? new Vector4(0.92f, 0.72f, 0.26f, 1f)
                            : new Vector4(0.88f, 0.3f, 0.28f, 1f);
                dl.AddCircleFilled(pipCenter, 4.5f * scale, ImGui.GetColorU32(pipColor));
                dl.AddCircle(pipCenter, 4.5f * scale, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.5f)), 12,
                    1f * scale);
                if (ImGui.IsMouseHoveringRect(pipCenter - (new Vector2(6f, 6f) * scale),
                        pipCenter + (new Vector2(6f, 6f) * scale)))
                {
                    ShowTooltip(BuildMonsterTooltip(member,
                        member.Fainted ? "Fainted." : $"{member.CurrentHp}/{member.MaxHp} HP."));
                }
            }
            else
            {
                dl.AddCircle(pipCenter, 4.5f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.18f)), 12,
                    1f * scale);
            }
        }

        // Status zone: the battle text (with the phone's coloured move names, accent rail
        // and reveal fade), or an idle hint.
        var lineX = min.X + 196f * scale;
        var lineWidth = max.X - lineX - 16f * scale;
        if (battleLive && message is not null && battleText.Count > 0)
        {
            var entry = battleText[^1];
            var alpha = Math.Clamp(entry.Age / 0.12f, 0f, 1f);
            dl.AddRectFilled(new Vector2(lineX - 9f * scale, contentTop + 9f * scale),
                new Vector2(lineX - 6f * scale, contentTop + 38f * scale),
                ImGui.GetColorU32((entry.MoveColor ?? Accent) with { W = alpha }), 2f * scale);
            var wrapped = WrapBattleText(entry, lineWidth);
            for (var lineIndex = 0; lineIndex < Math.Min(2, wrapped.Count); lineIndex++)
            {
                DrawBattleTextLine(new Vector2(lineX, contentTop + (8f + (lineIndex * 17f)) * scale),
                    wrapped[lineIndex], PhoneTheme.Default.TextStrong with { W = alpha }, alpha,
                    TextStyles.BodyEmphasized);
            }

            if (messageTimer > 0.18f)
            {
                Typography.Draw(new Vector2(max.X - 46f * scale, contentTop + 26f * scale), "click",
                    RosterUi.CardMuted with { W = 0.62f }, TextStyles.Caption2);
            }
        }
        else
        {
            var lineText = battleLive
                ? $"What will {battle!.Active.Name} do?"
                : worldSpawns.Count > 0
                    ? $"{worldSpawns.Count} wild Pokémon nearby — click one to engage!"
                    : "Wild Pokémon will appear as you explore.";
            Typography.Draw(new Vector2(lineX, contentTop + 8f * scale),
                FitLabel(lineText, lineWidth, TextStyles.Body), RosterUi.CardMuted, TextStyles.Body);

            // The phone's move-menu heading (Choice-item locks, blocked status moves) so a
            // greyed move card reads as intentional rather than as a bug.
            if (battleLive && !awaitingResult)
            {
                var heading = MoveMenuHeading();
                if (heading != "Choose a move")
                {
                    Typography.Draw(new Vector2(lineX, contentTop + 25f * scale),
                        FitLabel(heading, lineWidth, TextStyles.Caption2),
                        new Vector4(0.93f, 0.76f, 0.36f, 0.85f), TextStyles.Caption2);
                }
            }
        }

        if (battleLive && awaitingResult)
        {
            DrawBarResult(dl, lineX, contentTop, scale);
            LgUi.Interactive = prevInteractive;
            ImGui.End();
            return;
        }

        DrawBarSlots(dl, lineX, contentTop, max, scale, battleLive);
        LgUi.Interactive = prevInteractive;
        ImGui.End();
    }

    // The header strip: grip dots + title on a drag handle, LOG and collapse chips on the
    // right. Dragging moves the whole bar; the spot persists in the save.
    private void DrawBarHeader(ImDrawListPtr dl, Vector2 min, Vector2 max, float headerHeight, float scale,
        bool battleLive, Vector2 pos, Vector2 display, float width, float height)
    {
        var chipTop = min.Y + 4f * scale;
        var chipBottom = min.Y + headerHeight - 2f * scale;
        var collapseRect = new Rect(new Vector2(max.X - 34f * scale, chipTop),
            new Vector2(max.X - 8f * scale, chipBottom));
        var logRect = new Rect(new Vector2(max.X - 78f * scale, chipTop),
            new Vector2(max.X - 38f * scale, chipBottom));

        // Drag handle: everything left of the chips.
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##lillypadgo-bar-drag",
            new Vector2(MathF.Max(20f, logRect.Min.X - min.X - 6f * scale), headerHeight));
        if (ImGui.IsItemActive())
        {
            immersiveBarDragging = true;
            var next = pos + ImGui.GetIO().MouseDelta;
            State.ImmersiveBarX = Math.Clamp(next.X, 0f, MathF.Max(0f, display.X - width));
            State.ImmersiveBarY = Math.Clamp(next.Y, 0f, MathF.Max(0f, display.Y - height));
        }
        else if (immersiveBarDragging)
        {
            immersiveBarDragging = false;
            State.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }

        // Grip dots + title.
        var gripX = min.X + 12f * scale;
        var gripY = min.Y + (headerHeight * 0.5f);
        for (var i = 0; i < 3; i++)
        {
            dl.AddCircleFilled(new Vector2(gripX + (i * 5f * scale), gripY - 3f * scale), 1.4f * scale,
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f)));
            dl.AddCircleFilled(new Vector2(gripX + (i * 5f * scale), gripY + 3f * scale), 1.4f * scale,
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f)));
        }

        Typography.Draw(new Vector2(min.X + 34f * scale, min.Y + 5f * scale), "LILLYPAD GO",
            new Vector4(0.93f, 0.76f, 0.36f, 0.9f), TextStyles.Caption2);
        if (battleLive)
        {
            Typography.Draw(new Vector2(min.X + 118f * scale, min.Y + 5f * scale),
                battle!.IsTrainerBattle ? $"VS {battle.TrainerName}" : $"VS wild {battle.Wild.Species.Name}",
                RosterUi.CardMuted, TextStyles.Caption2);
            this.DrawHeaderBattleChips(dl, min, headerHeight, scale, logRect.Min.X - 8f * scale);
        }

        if (battleLive && State.BattleLogEnabled &&
            RosterUi.ColorButton(logRect, "LOG", immersiveLogOpen ? RosterUi.Green : RosterUi.Blue, scale, true))
        {
            immersiveLogOpen = !immersiveLogOpen;
        }

        if (RosterUi.ColorButton(collapseRect, State.ImmersiveBarCollapsed ? "+" : "–",
                GamePalette.Cell, scale, true))
        {
            State.ImmersiveBarCollapsed = !State.ImmersiveBarCollapsed;
            State.Save();
        }

        if (ImGui.IsMouseHoveringRect(collapseRect.Min, collapseRect.Max))
        {
            ShowTooltip(State.ImmersiveBarCollapsed
                ? "Expand the battle bar."
                : "Collapse the bar to its header. Keys 1–8 still work in battle.");
        }
    }

    // Weather + field-condition chips in the header, right-to-left before the LOG chip —
    // the same information the phone's weather chip and battle indicators carry, with the
    // same hover summaries.
    private void DrawHeaderBattleChips(ImDrawListPtr dl, Vector2 min, float headerHeight, float scale,
        float rightLimit)
    {
        var chipTop = min.Y + 3f * scale;
        var chipBottom = min.Y + headerHeight - 3f * scale;
        var x = rightLimit;
        var leftLimit = min.X + 320f * scale;

        if (battle!.Weather != BattleWeather.None)
        {
            var (icon, tone) = battle.Weather switch
            {
                BattleWeather.Sun => (FontAwesomeIcon.Sun, new Vector4(1f, 0.74f, 0.24f, 1f)),
                BattleWeather.Rain => (FontAwesomeIcon.CloudRain, Elements.Color(Element.Water)),
                BattleWeather.Sandstorm => (FontAwesomeIcon.Wind, new Vector4(0.82f, 0.6f, 0.32f, 1f)),
                BattleWeather.Snow => (FontAwesomeIcon.Snowflake, Elements.Color(Element.Ice)),
                _ => (FontAwesomeIcon.Question, Vector4.One),
            };
            var label = battle.WeatherIsSuppressed ? $"{battle.WeatherName} (negated)" : battle.WeatherName;
            var width = Typography.Measure(label, TextStyles.Caption2).X + 26f * scale;
            var chipMin = new Vector2(x - width, chipTop);
            var chipMax = new Vector2(x, chipBottom);
            Squircle.Fill(dl, chipMin, chipMax, 6f * scale,
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.45f)));
            Squircle.Stroke(dl, chipMin, chipMax, 6f * scale, ImGui.GetColorU32(tone with { W = 0.75f }),
                1f * scale);
            ProgressRing.CenterIcon(dl, new Vector2(chipMin.X + 11f * scale, (chipTop + chipBottom) * 0.5f),
                icon, tone, 9f * scale);
            Typography.Draw(new Vector2(chipMin.X + 20f * scale, chipTop + 2f * scale), label, tone,
                TextStyles.Caption2);
            if (ImGui.IsMouseHoveringRect(chipMin, chipMax))
            {
                ShowTooltip($"{battle.WeatherName}\n\n• " + string.Join("\n• ", battle.WeatherSummary()));
            }

            x = chipMin.X - 5f * scale;
        }

        foreach (var indicator in battle.FieldIndicators())
        {
            var label = indicator.Label;
            var width = Typography.Measure(label, TextStyles.Caption2).X + 14f * scale;
            if (x - width < leftLimit)
            {
                break;
            }

            var chipMin = new Vector2(x - width, chipTop);
            var chipMax = new Vector2(x, chipBottom);
            Squircle.Fill(dl, chipMin, chipMax, 6f * scale,
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.45f)));
            Squircle.Stroke(dl, chipMin, chipMax, 6f * scale,
                ImGui.GetColorU32(indicator.Color with { W = 0.7f }), 1f * scale);
            Typography.Draw(new Vector2(chipMin.X + 7f * scale, chipTop + 2f * scale), label,
                GamePalette.Lighten(indicator.Color, 0.2f), TextStyles.Caption2);
            if (ImGui.IsMouseHoveringRect(chipMin, chipMax))
            {
                ShowTooltip($"{indicator.Label}\n{indicator.Detail}");
            }

            x = chipMin.X - 5f * scale;
        }
    }

    // Key handling while the bar is collapsed: same actions, no visuals (popups still draw).
    private void HandleCollapsedKeys()
    {
        if (battle is null)
        {
            return;
        }

        if (awaitingResult)
        {
            if (immersiveKeyPressed[0])
            {
                immersiveBagOpen = false;
                immersiveSwapOpen = false;
                immersiveRunConfirm = false;
                FinishBattle();
            }

            return;
        }

        var actionable = message is null && time >= suppressBattleButtonsUntil &&
                         battle.PendingMoveChoice is null && !battle.RequiresSwitch;
        for (var i = 0; i < 4; i++)
        {
            if (immersiveKeyPressed[i])
            {
                TriggerMoveSlot(i, actionable);
            }
        }

        for (var i = 0; i < 4; i++)
        {
            if (immersiveKeyPressed[i + 4])
            {
                TriggerUtilitySlot(i, actionable);
            }
        }
    }

    private void TriggerMoveSlot(int index, bool actionable)
    {
        if (!actionable || battle is null)
        {
            return;
        }

        if (ForcedMoveLabel() is not null)
        {
            if (index == 0)
            {
                battle.UseMove(0);
            }

            return;
        }

        if (battle.MustStruggle)
        {
            if (index == 0)
            {
                battle.UseMove(-1);
            }

            return;
        }

        if (index < battle.Active.Moves.Count && battle.CanSelectMove(index))
        {
            battle.UseMove(index);
        }
    }

    private void TriggerUtilitySlot(int index, bool actionable)
    {
        switch (index)
        {
            case 0 when actionable:
                immersiveBagOpen = !immersiveBagOpen;
                immersiveSwapOpen = false;
                immersiveRunConfirm = false;
                break;
            case 1 when actionable:
                immersiveSwapOpen = !immersiveSwapOpen;
                immersiveBagOpen = false;
                immersiveRunConfirm = false;
                break;
            case 2 when actionable:
                immersiveRunConfirm = !immersiveRunConfirm;
                immersiveBagOpen = false;
                immersiveSwapOpen = false;
                break;
            case 3:
                global::VideoSyncPrototype.Plugin.OpenMainWindow?.Invoke();
                break;
        }
    }

    // The phone's battle-history panel, floating above the bar while toggled on.
    private void DrawBattleLogWindow(float scale)
    {
        var width = 400f * scale;
        var height = 214f * scale;
        var barX = State.ImmersiveBarX >= 0f
            ? State.ImmersiveBarX
            : (ImGui.GetIO().DisplaySize.X - (BarWidth * scale)) * 0.5f;
        var barY = State.ImmersiveBarY >= 0f
            ? State.ImmersiveBarY
            : ImGui.GetIO().DisplaySize.Y - ((24f + BarContentHeight) * scale) - 44f * scale;
        ImGui.SetNextWindowPos(new Vector2(barX, MathF.Max(0f, barY - height - 8f * scale)), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, height));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBackground;
        if (ImGui.Begin("##lillypadgo-immersive-log", flags))
        {
            var panel = new Rect(ImGui.GetWindowPos(), ImGui.GetWindowPos() + new Vector2(width, height));
            DrawBattleHistory(panel, PhoneTheme.Default, scale);
        }

        ImGui.End();
    }

    private void DrawBarResult(ImDrawListPtr dl, float lineX, float contentTop, float scale)
    {
        var resultText = battle!.Outcome switch
        {
            BattleOutcome.Won => $"Victory!{(battle.PrizeMoney > 0 ? $"   +{LgUi.Money(battle.PrizeMoney)}" : string.Empty)}" +
                                 $"{(battle.XpGained > 0 ? $"   +{battle.XpGained} XP" : string.Empty)}",
            BattleOutcome.Captured => $"Gotcha! {battle.Wild.Name} was caught!",
            BattleOutcome.Whiteout => "You were unable to win…",
            BattleOutcome.Fled when battle.IsTrainerBattle => "You forfeited the battle.",
            _ => "Got away safely!",
        };
        Typography.Draw(new Vector2(lineX, contentTop + 38f * scale), resultText,
            new Vector4(0.93f, 0.76f, 0.36f, 1f), TextStyles.SubheadlineEmphasized);
        var continueRect = new Rect(new Vector2(lineX, contentTop + 64f * scale),
            new Vector2(lineX + 190f * scale, contentTop + 102f * scale));
        if (RosterUi.ColorButton(continueRect, "CONTINUE", RosterUi.Green, scale, true) ||
            immersiveKeyPressed[0])
        {
            immersiveBagOpen = false;
            immersiveSwapOpen = false;
            immersiveRunConfirm = false;
            FinishBattle();
            return;
        }

        DrawKeyBadge(dl, continueRect.Min, 1, scale);
    }

    // ---- World HP plates: the phone's REAL battle status panels floating above each
    // battler — same angled plate, status/volatile chips, stat-stage summary and XP meter,
    // for exact parity. Hosted in a fullscreen pass-through window so DrawStatusPanel's
    // Typography (which writes to the current window's draw list) works over the world.

    private void DrawBattleWorldPlates()
    {
        if (battle is null ||
            !WorldBattleStage.TryGetStage(out var playerCenter, out var playerHalf, out var wildCenter,
                out var wildHalf))
        {
            return;
        }

        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(display);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoFocusOnAppearing;
        if (!ImGui.Begin("##lillypadgo-world-plates", flags))
        {
            ImGui.End();
            return;
        }

        var dl = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var active = displayedPlayer ?? battle.Active;

        // Battler-anchored FX from the phone arena — alpha aura, status flames/bubbles,
        // confusion stars, evolution pulse — sized by each battler's on-screen height.
        if (Plugin.GameGui.WorldToScreen(wildCenter, out var wildBody) &&
            Plugin.GameGui.WorldToScreen(wildCenter + new Vector3(0f, wildHalf, 0f), out var wildBodyTop))
        {
            var sizeFactor = Math.Clamp(MathF.Abs(wildBody.Y - wildBodyTop.Y) / 34f, 0.4f, 2.2f);
            if (battle.Wild.Trait is { } wildAlphaTrait && displayedWildHp > 0)
            {
                DrawAlphaAura(dl, wildBody, 44f * scale * sizeFactor, Alphas.TraitColor(wildAlphaTrait),
                    time, 1f, strength: 2.4f);
            }

            if (displayedWildHp > 0)
            {
                DrawStatusFx(dl, wildBody, displayedWildStatus, time, scale * sizeFactor);
                if (battle.Wild.ConfusionTurns > 0)
                {
                    DrawConfusionFx(dl, wildBody, time, scale * sizeFactor);
                }
            }
        }

        if (Plugin.GameGui.WorldToScreen(playerCenter, out var playerBody) &&
            Plugin.GameGui.WorldToScreen(playerCenter + new Vector3(0f, playerHalf, 0f), out var playerBodyTop))
        {
            var sizeFactor = Math.Clamp(MathF.Abs(playerBody.Y - playerBodyTop.Y) / 34f, 0.4f, 2.2f);
            if (displayedPlayerHp > 0)
            {
                DrawStatusFx(dl, playerBody, displayedPlayerStatus, time + 0.8f, scale * sizeFactor);
                if (active.ConfusionTurns > 0)
                {
                    DrawConfusionFx(dl, playerBody, time + 0.4f, scale * sizeFactor);
                }
            }

            if (battleEvolutionPulse > 0f)
            {
                DrawBattleEvolutionPulse(dl, playerBody, active, scale * sizeFactor);
            }
        }

        if (Plugin.GameGui.WorldToScreen(wildCenter + new Vector3(0f, wildHalf + 0.55f, 0f), out var wildScreen))
        {
            var size = new Vector2(188f * scale, 56f * scale);
            var min = new Vector2(wildScreen.X - (size.X * 0.5f), wildScreen.Y - size.Y);
            DrawStatusPanel(dl, min, min + size, battle.Wild, animatedWildHp, displayedWildStatus,
                displayedWildAtkStage, displayedWildDefStage, displayedWildSpAtkStage, displayedWildSpDefStage,
                displayedWildSpdStage, displayedWildAccuracyStage, displayedWildEvasionStage,
                displayedWildLevel, 0f, false, PhoneTheme.Default, scale);
            if (ImGui.IsMouseHoveringRect(min, min + size))
            {
                ShowTooltip(BuildMonsterTooltip(battle.Wild, BuildWildBattleNote() + BattleWeatherNote(battle.Wild),
                    displayedWildHp));
            }
        }

        if (Plugin.GameGui.WorldToScreen(playerCenter + new Vector3(0f, playerHalf + 0.55f, 0f), out var playerScreen))
        {
            var size = new Vector2(188f * scale, 64f * scale);
            var min = new Vector2(playerScreen.X - (size.X * 0.5f), playerScreen.Y - size.Y);
            DrawStatusPanel(dl, min, min + size, active, animatedPlayerHp, displayedPlayerStatus,
                displayedPlayerAtkStage, displayedPlayerDefStage, displayedPlayerSpAtkStage,
                displayedPlayerSpDefStage, displayedPlayerSpdStage, displayedPlayerAccuracyStage,
                displayedPlayerEvasionStage, displayedPlayerLevel, animatedPlayerXp, true,
                PhoneTheme.Default, scale);
            if (ImGui.IsMouseHoveringRect(min, min + size))
            {
                ShowTooltip(BuildMonsterTooltip(active, "Your active creature." + BattleWeatherNote(active),
                    displayedPlayerHp));
            }
        }

        ImGui.End();
    }

    // The same wild-opponent note the phone's status-panel hover composes.
    private string BuildWildBattleNote()
    {
        if (battle!.IsAlphaBattle)
        {
            return battle.Wild.Trait is { } trait
                ? $"The apex of this territory.\n{Alphas.TraitName(trait)}: {Alphas.TraitBlurb(trait)}\nIt cannot be caught."
                : "The apex of this territory. It cannot be caught.";
        }

        if (battle.IsTrainerBattle)
        {
            return $"{battle.TrainerName}'s Pokémon.";
        }

        var caughtBefore = State.Party.Concat(State.Box).Any(m => m.Species.Id == battle.Wild.Species.Id);
        return caughtBefore
            ? "Wild opponent.\n✓ Already in your collection."
            : "Wild opponent.\n★ New species — not caught yet!";
    }

    // The 8 slots: four chunky move cards (the phone's own DrawMoveButton) and four
    // RosterUi utility buttons, each wearing its key number badge.
    private void DrawBarSlots(ImDrawListPtr dl, float lineX, float contentTop, Vector2 max, float scale,
        bool battleLive)
    {
        var rowTop = contentTop + 42f * scale;
        var rowBottom = max.Y - 12f * scale;
        var moveWidth = 126f * scale;
        var utilWidth = 54f * scale;
        var gap = 5f * scale;
        var actionable = battleLive && message is null && time >= suppressBattleButtonsUntil &&
                         battle!.PendingMoveChoice is null && !battle.RequiresSwitch;
        var lead = battleLive ? battle!.Active : State.Party.FirstOrDefault(m => !m.Fainted);
        var forced = battleLive ? ForcedMoveLabel() : null;

        for (var i = 0; i < 4; i++)
        {
            var left = lineX + (i * (moveWidth + gap));
            var rect = new Rect(new Vector2(left, rowTop), new Vector2(left + moveWidth, rowBottom));

            if (forced is { } forcedLabel)
            {
                if (i == 0)
                {
                    if ((RosterUi.ColorButton(rect, forcedLabel, RosterUi.Green, scale, actionable) ||
                         immersiveKeyPressed[0]) && actionable)
                    {
                        battle!.UseMove(0);
                    }
                }
                else
                {
                    DrawEmptySlot(dl, rect, scale);
                }

                DrawKeyBadge(dl, rect.Min, i + 1, scale);
                continue;
            }

            if (battleLive && battle!.MustStruggle)
            {
                if (i == 0)
                {
                    if ((RosterUi.ColorButton(rect, "Struggle", RosterUi.Red, scale, actionable) ||
                         immersiveKeyPressed[0]) && actionable)
                    {
                        battle.UseMove(-1);
                    }

                    if (ImGui.IsMouseHoveringRect(rect.Min, rect.Max))
                    {
                        ShowTooltip(BuildMoveTooltip(Moves.Struggle, battle.Active, battle.Wild, 1));
                    }
                }
                else
                {
                    DrawEmptySlot(dl, rect, scale);
                }

                DrawKeyBadge(dl, rect.Min, i + 1, scale);
                continue;
            }

            if (lead is not null && i < lead.Moves.Count)
            {
                var move = lead.Moves[i];
                var enabled = actionable && battle!.CanSelectMove(i);
                var effectiveness = !battleLive || move.IsStatus
                    ? 1f
                    : Elements.Effectiveness(move.Element, battle!.Wild.Element, battle.Wild.SecondaryElement);
                // Out of battle the cards stay in their full colours (a preview of the lead's
                // kit); the click is simply inert.
                var visualEnabled = !battleLive || enabled;
                var clicked = DrawMoveButton(rect, move, lead.Pp[i], effectiveness,
                    Elements.Color(move.Element), PhoneTheme.Default, visualEnabled, scale);
                if ((clicked || immersiveKeyPressed[i]) && battleLive && enabled)
                {
                    battle!.UseMove(i);
                }

                if (ImGui.IsMouseHoveringRect(rect.Min, rect.Max))
                {
                    ShowTooltip(battleLive
                        ? BuildMoveTooltip(move, battle!.Active, battle.Wild, lead.Pp[i])
                        : BuildProfileMoveTooltip(move, lead.Pp[i]));
                }
            }
            else
            {
                DrawEmptySlot(dl, rect, scale);
            }

            DrawKeyBadge(dl, rect.Min, i + 1, scale);
        }

        // Slots 5–8: bag / swap / run / phone.
        var utilX = lineX + (4 * (moveWidth + gap)) + 8f * scale;
        var labels = new[] { "BAG", "SWAP", "RUN", "PHONE" };
        var colors = new[] { RosterUi.Blue, RosterUi.Purple, RosterUi.Red, new Vector4(0.35f, 0.4f, 0.52f, 1f) };
        for (var i = 0; i < 4; i++)
        {
            var left = utilX + (i * (utilWidth + gap));
            var rect = new Rect(new Vector2(left, rowTop), new Vector2(left + utilWidth, rowBottom));
            var enabled = i == 3 || actionable;
            var pressed = RosterUi.ColorButton(rect, labels[i], colors[i], scale, enabled) ||
                          (battleLive && enabled && immersiveKeyPressed[i + 4]);
            DrawKeyBadge(dl, rect.Min, i + 5, scale);
            if (ImGui.IsMouseHoveringRect(rect.Min, rect.Max))
            {
                ShowTooltip(UtilityTooltip(i, battleLive));
            }

            if (pressed)
            {
                TriggerUtilitySlot(i, actionable);
            }
        }
    }

    private string UtilityTooltip(int index, bool battleLive) => index switch
    {
        0 => battleLive
            ? battle!.CanCatch
                ? "Throw a Poké Ball, or restore your active creature's HP. Using an item takes your turn."
                : "Use an item to heal, revive or cure your team. Using an item takes your turn."
            : "Bag — available during battles.",
        1 => battleLive
            ? "Switch creatures. The opponent will attack after the switch."
            : "Swap — available during battles.",
        2 => battleLive
            ? battle!.IsTrainerBattle
                ? "Forfeit the battle. Counts as a loss — no badge, money or spoils."
                : battle.IsAlphaBattle
                    ? "Retreat safely. The Alpha holds its territory — challenge it again anytime."
                    : $"Attempt to escape. Current success chance: {battle.EscapeChance:P0}."
            : "Run — available during battles.",
        _ => "Open the phone for the full app — team, bag, dex and settings.",
    };

    // The label of the forced slot-1 action while committed to a multi-turn move, else null.
    private string? ForcedMoveLabel()
    {
        if (battle!.Active.ChargingMove is { } charging)
        {
            return $"Unleash\n{charging.Name}";
        }

        if (battle.Active.MustRecharge)
        {
            return "Recharge";
        }

        if (battle.Active.LockedMove is { } rampage)
        {
            return rampage.Name;
        }

        return null;
    }

    private static void DrawEmptySlot(ImDrawListPtr dl, Rect rect, float scale)
    {
        Squircle.Fill(dl, rect.Min, rect.Max, 10f * scale,
            ImGui.GetColorU32(RosterUi.NavyInset with { W = 0.6f }));
        Squircle.Stroke(dl, rect.Min, rect.Max, 10f * scale,
            ImGui.GetColorU32(RosterUi.NavyLine with { W = 0.3f }), 1f * scale);
    }

    // The little "press this key" medallion in a slot's corner.
    private static void DrawKeyBadge(ImDrawListPtr dl, Vector2 slotMin, int number, float scale)
    {
        var center = slotMin + (new Vector2(10f, 10f) * scale);
        dl.AddCircleFilled(center, 7.5f * scale, ImGui.GetColorU32(new Vector4(0.04f, 0.07f, 0.12f, 0.85f)));
        dl.AddCircle(center, 7.5f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.25f)), 16, 1f * scale);
        var text = number.ToString();
        var size = ImGui.CalcTextSize(text);
        dl.AddText(center - (size * 0.5f), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.85f)), text);
    }

    // A phone-styled floating window: navy body, dark edge, rounded corners. Auto-sizes
    // unless a fixed size is given (fixed windows scroll their content).
    private static bool BeginNavyWindow(string id, Vector2 pos, Vector2? size = null)
    {
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        if (size is { } fixedSize)
        {
            ImGui.SetNextWindowSize(fixedSize);
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 9f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, RosterUi.NavyBottom with { W = 0.97f });
        ImGui.PushStyleColor(ImGuiCol.Border, RosterUi.NavyEdge);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
                    (size is null ? ImGuiWindowFlags.AlwaysAutoResize : ImGuiWindowFlags.None);
        return ImGui.Begin(id, flags);
    }

    private static void EndNavyWindow()
    {
        ImGui.End();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(4);
    }

    private static bool NavyButton(string label, Vector4 color, Vector2 size, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        ImGui.PushStyleColor(ImGuiCol.Button, color with { W = 0.85f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, GamePalette.Lighten(color, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, GamePalette.Darken(color, 0.12f));
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        ImGui.EndDisabled();
        return clicked;
    }

    private void DrawHudBagPopup(float scale, Vector2 display)
    {
        var owned = State.Bag.Contents().ToList();
        if (BeginNavyWindow("##lillypadgo-immersive-bag",
                new Vector2((display.X * 0.5f) - 190f * scale, display.Y - 440f * scale),
                new Vector2(380f * scale, 258f * scale)))
        {
            ImGui.TextColored(PhoneTheme.Default.TextStrong, "Bag");
            ImGui.SameLine();
            ImGui.TextColored(RosterUi.CardMuted, "— using an item takes your turn");
            ImGui.Separator();
            if (owned.Count == 0)
            {
                ImGui.TextColored(RosterUi.CardMuted, "Your bag is empty. Restock at a town Marketboard.");
            }

            foreach (var (item, count) in owned.Select(o => (o.Def, o.Count)))
            {
                var usable = battle!.CanUseItem(item);
                // Leading spaces leave room for the item sprite, mirroring the phone's rows.
                if (NavyButton($"      {item.Name}##bag-{item.Id}", LgUi.ItemTint(item.Category),
                        new Vector2(180f * scale, 26f * scale), usable))
                {
                    if (item.Category == ItemCategory.Ball)
                    {
                        pendingCaptureBallId = item.Id;
                    }

                    battle.UseItem(item);
                    immersiveBagOpen = false;
                }

                var rowMin = ImGui.GetItemRectMin();
                var rowMax = ImGui.GetItemRectMax();
                LgUi.ItemIcon(ImGui.GetWindowDrawList(),
                    new Vector2(rowMin.X + ((rowMax.Y - rowMin.Y) * 0.55f), (rowMin.Y + rowMax.Y) * 0.5f),
                    (rowMax.Y - rowMin.Y) * 0.7f, item);

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ShowTooltip(BuildBattleItemTooltip(item));
                }

                ImGui.SameLine();
                ImGui.TextColored(RosterUi.CardMuted, BattleItemSub(item, count));
            }

            ImGui.Spacing();
            if (NavyButton("Close##bag", GamePalette.Cell, new Vector2(90f * scale, 26f * scale)))
            {
                immersiveBagOpen = false;
            }
        }

        EndNavyWindow();
    }

    private void DrawHudSwapPopup(float scale, Vector2 display)
    {
        var forced = battle!.RequiresSwitch;
        if (BeginNavyWindow("##lillypadgo-immersive-swap",
                new Vector2((display.X * 0.5f) - 170f * scale, display.Y - 420f * scale)))
        {
            ImGui.TextColored(PhoneTheme.Default.TextStrong,
                forced ? "Choose your next creature" : "Switch creature — uses your turn");
            ImGui.Separator();
            var any = false;
            for (var i = 0; i < State.Party.Count; i++)
            {
                if (i == battle.ActiveIndex)
                {
                    continue;
                }

                var member = State.Party[i];
                any = true;
                if (NavyButton($"      {member.Name}  ·  Lv {member.Level}  ·  {member.CurrentHp}/{member.MaxHp} HP##swap-{i}",
                        Elements.Color(member.Element), new Vector2(300f * scale, 28f * scale), !member.Fainted))
                {
                    battle.Switch(i);
                    immersiveSwapOpen = false;
                }

                // Animated sprite tucked into the row's left edge, like the phone's menu.
                var rowMin = ImGui.GetItemRectMin();
                var rowMax = ImGui.GetItemRectMax();
                MonsterArt.Draw(ImGui.GetWindowDrawList(),
                    new Vector2(rowMin.X + ((rowMax.Y - rowMin.Y) * 0.5f), (rowMin.Y + rowMax.Y) * 0.5f),
                    (rowMax.Y - rowMin.Y) * 0.34f, member.Species, 1f,
                    new MonsterPose(time + i, 0f, 0f, member.Fainted ? 0.4f : 1f, member.Fainted));

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ShowTooltip(BuildMonsterTooltip(member, member.Fainted
                        ? "This creature has fainted and cannot switch in."
                        : forced ? "Choose this creature to continue the battle." : "Switching uses your turn."));
                }
            }

            if (!any)
            {
                ImGui.TextColored(RosterUi.CardMuted, "No other creature can battle.");
            }

            ImGui.Spacing();
            if (!forced && NavyButton("Close##swap", GamePalette.Cell, new Vector2(90f * scale, 26f * scale)))
            {
                immersiveSwapOpen = false;
            }
        }

        EndNavyWindow();
    }

    private void DrawHudRunConfirm(float scale, Vector2 display)
    {
        var trainer = battle!.IsTrainerBattle;
        var alpha = battle.IsAlphaBattle;
        if (BeginNavyWindow("##lillypadgo-immersive-run",
                new Vector2((display.X * 0.5f) - 140f * scale, display.Y - 320f * scale)))
        {
            ImGui.TextColored(PhoneTheme.Default.TextStrong,
                trainer ? "Forfeit this battle?" : alpha ? "Retreat from the Alpha?" : "Run away?");
            ImGui.TextColored(RosterUi.CardMuted, trainer ? "It counts as a loss — no badge, money or spoils."
                : alpha ? "It keeps its territory. Challenge it again anytime."
                : $"Success chance: {battle.EscapeChance:P0}.");
            ImGui.Spacing();
            if (NavyButton(trainer ? "FORFEIT" : alpha ? "RETREAT" : "RUN", RosterUi.Red,
                    new Vector2(110f * scale, 28f * scale)))
            {
                immersiveRunConfirm = false;
                battle.Run();
            }

            ImGui.SameLine();
            if (NavyButton("STAY", RosterUi.Blue, new Vector2(110f * scale, 28f * scale)))
            {
                immersiveRunConfirm = false;
            }
        }

        EndNavyWindow();
    }

    private void DrawHudMoveLearnPopup(float scale, Vector2 display)
    {
        if (battle?.PendingMoveChoice is not { } choice)
        {
            return;
        }

        if (BeginNavyWindow("##lillypadgo-immersive-learn",
                new Vector2((display.X * 0.5f) - 170f * scale, display.Y - 420f * scale)))
        {
            ImGui.TextColored(PhoneTheme.Default.TextStrong,
                $"Learn {choice.Move.Name}? Choose a move for {choice.Monster.Name} to forget:");
            if (ImGui.IsItemHovered())
            {
                ShowTooltip(BuildProfileMoveTooltip(choice.Move, choice.Move.Pp));
            }

            ImGui.Separator();
            for (var i = 0; i < choice.Monster.Moves.Count; i++)
            {
                var move = choice.Monster.Moves[i];
                if (NavyButton($"Forget {move.Name}##learn-{i}", Elements.Color(move.Element),
                        new Vector2(300f * scale, 28f * scale)))
                {
                    battle.ResolveMoveChoice(i);
                    break;
                }

                if (ImGui.IsItemHovered())
                {
                    ShowTooltip(BuildProfileMoveTooltip(move, choice.Monster.Pp[i]));
                }
            }

            ImGui.Spacing();
            if (NavyButton("Keep current moves##learn-keep", RosterUi.Blue, new Vector2(300f * scale, 28f * scale)))
            {
                battle.ResolveMoveChoice(null);
            }
        }

        EndNavyWindow();
    }

    // Hovering the enemy Pokémon standing in the world shows the same info the phone's
    // status panel hover does.
    private void DrawWildWorldTooltip()
    {
        if (battle is null ||
            !WorldBattleStage.TryGetStage(out _, out _, out var center, out var halfHeight) ||
            !Plugin.GameGui.WorldToScreen(center, out var screenCenter) ||
            !Plugin.GameGui.WorldToScreen(center + new Vector3(0f, halfHeight, 0f), out var screenTop))
        {
            return;
        }

        var radius = MathF.Max(24f, MathF.Abs(screenCenter.Y - screenTop.Y));
        if (Vector2.Distance(ImGui.GetMousePos(), screenCenter) > radius)
        {
            return;
        }

        ShowTooltip(BuildMonsterTooltip(battle.Wild, BuildWildBattleNote() + BattleWeatherNote(battle.Wild),
            displayedWildHp));
    }

    // Keys 1–8 drive the battle hotbar while a battle is live: edge-detect, then clear the
    // key state so the game's own hotbar never sees the press. Skipped entirely while any
    // text input (chat, ImGui fields) is active. The real hotbars are never modified.
    private void CollectImmersiveKeys()
    {
        var typing = ImGui.GetIO().WantTextInput || GameTextInputActive();
        for (var i = 0; i < 8; i++)
        {
            var key = (VirtualKey)((int)VirtualKey.KEY_1 + i);
            if (typing)
            {
                immersiveKeyPressed[i] = false;
                immersiveKeyWasDown[i] = false;
                continue;
            }

            var down = Plugin.KeyState[key];
            immersiveKeyPressed[i] = down && !immersiveKeyWasDown[i];
            immersiveKeyWasDown[i] = down;
            if (down)
            {
                Plugin.KeyState[key] = false;
            }
        }
    }

    private static unsafe bool GameTextInputActive()
    {
        try
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule.Instance();
            return module is not null && module->AtkModule.IsTextInputActive();
        }
        catch
        {
            return false;
        }
    }
}
