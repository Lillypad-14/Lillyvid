using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using VideoSyncPrototype.Rendering;

using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// The overworld follower + in-world battle stage, rendered through the native scene-pass
// pipeline (the same depth-tested draw the world video screen uses) so the world and other
// characters occlude the sprites correctly.
//
// Follower: the lead (first healthy) party Pokémon walks behind the player as a billboarded
// gen5ani sprite, with a small idle life of its own (wander / look at the trainer / hop).
//
// Battle stage: while a Lillypad Go battle is on the phone, the phone publishes a per-frame
// snapshot (WorldBattleStage) and this renderer mirrors it in the world IN TANDEM with the
// phone: the active battler takes position in front of the player, the enemy appears facing
// it, and the traced Showdown move animations, capture balls, background flashes and damage
// popups play out between them.
internal sealed class FollowerRenderer
{
    private const float FollowDistance = 2.0f;       // resting spot behind the player, in yalms
    private const float ResumeFollowDistance = 3.4f; // this far from the player → run to catch up
    private const float MoveDeadzone = 0.35f;        // no jitter once close enough to a target
    private const float SnapDistance = 25f;          // teleport past this (zone jumps, mounts)
    private const float WanderSpeed = 1.7f;          // idle amble, much slower than catch-up
    private const float SpriteHeight = 1.2f;         // follower billboard height in world units

    private const float BattlePlayerRange = 1.8f;    // active battler, in front of the player
    private const float BattleEnemyRange = 4.8f;     // the enemy faces it from further out
    private const float BattleMonHeight = 1.35f;     // battlers stand a touch larger than the follower
    private const float SpawnHeight = 1.15f;         // immersive-mode wild spawns

    private enum Mode
    {
        Follow,
        Idle,
        Wander,
    }

    private static readonly Random Rng = new();

    private readonly List<NativeWorldSprite> spriteList = new(24);

    private Vector3 position;
    private Vector3 facing = new(0f, 0f, 1f);
    private bool placed;
    private uint territory;
    private float animTime;
    private float hopPhase;
    private float hopBurstUntil;
    private Mode mode = Mode.Follow;
    private Vector3 wanderTarget;
    private float nextIdleActionAt;
    private DateTime lastInstallAttemptUtc = DateTime.MinValue;

    // Battle stage anchors, frozen when a battle snapshot first arrives so the arena does
    // not swing around with the player's facing mid-fight.
    private bool battleActive;
    private Vector3 battlePlayerFeet;
    private Vector3 battleWildFeet;
    private Vector3 battleForward = new(0f, 0f, 1f);

    // Runs once per frame from the UI draw. Advances the follower/battle state and hands the
    // current sprite billboards to the native renderer (or clears them while hidden).
    public void Update(PresentHookProbe probe)
    {
        var state = Plugin.LillypadGo;
        var player = Plugin.ObjectTable.LocalPlayer;
        var lead = state is { FollowerEnabled: true }
            ? state.Party.FirstOrDefault(monster => !monster.Fainted)
            : null;
        WorldBattleStage.Snapshot? snap = null;
        if (state is { WorldBattlesEnabled: true } && WorldBattleStage.TryGet(out var liveSnap))
        {
            snap = liveSnap;
        }

        var spawns = WorldSpawnStage.Current;
        if (player is null || (lead is null && snap is null && spawns.Length == 0))
        {
            probe.ClearNativeWorldSprites();
            this.placed = false;
            this.battleActive = false;
            return;
        }

        // The world screen normally installs the render hook; do it here too so the
        // follower works while the TV is off. Retries are throttled like the TV's.
        if (!probe.IsInstalled)
        {
            var now = DateTime.UtcNow;
            if (now - this.lastInstallAttemptUtc < TimeSpan.FromSeconds(2))
            {
                return;
            }

            this.lastInstallAttemptUtc = now;
            if (!probe.TryInstall())
            {
                return;
            }
        }

        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.25f);
        this.animTime += dt;

        if (!TryGetCameraPosition(out var cameraPosition))
        {
            probe.ClearNativeWorldSprites();
            return;
        }

        if (snap is not null)
        {
            this.UpdateBattleStage(probe, snap, player.Position, player.Rotation, cameraPosition, dt);
            return;
        }

        this.battleActive = false;
        this.spriteList.Clear();
        if (lead is not null)
        {
            this.AppendFollower(lead, player.Position, player.Rotation, cameraPosition, dt);
        }
        else
        {
            this.placed = false;
        }

        this.AppendSpawns(spawns, cameraPosition);
        probe.SetNativeWorldSprites(this.spriteList);
    }

    // ---- Overworld follower -----------------------------------------------------------

    private void AppendFollower(MonsterInstance lead, Vector3 playerPosition,
        float playerRotation, Vector3 cameraPosition, float dt)
    {
        var playerForward = new Vector3(MathF.Sin(playerRotation), 0f, MathF.Cos(playerRotation));
        var restSpot = playerPosition - (playerForward * FollowDistance);

        var currentTerritory = Plugin.ClientState.TerritoryType;
        if (!this.placed || currentTerritory != this.territory ||
            Vector3.Distance(this.position, restSpot) > SnapDistance)
        {
            this.position = restSpot;
            this.facing = playerForward;
            this.placed = true;
            this.territory = currentTerritory;
            this.position.Y = GroundY(this.position, playerPosition.Y);
        }

        var toPlayer = playerPosition - this.position;
        toPlayer.Y = 0f;
        var playerDistance = toPlayer.Length();

        // Catch-up beats everything: whenever the trainer walks off, drop what we're doing.
        // Distance is measured to the player (not the rest spot) so turning in place doesn't
        // send the follower shuffling around behind them like it's on a rigid pole.
        if (playerDistance > ResumeFollowDistance)
        {
            this.mode = Mode.Follow;
        }

        var moving = false;
        switch (this.mode)
        {
            case Mode.Follow:
            {
                var toRest = restSpot - this.position;
                var horizontal = new Vector3(toRest.X, 0f, toRest.Z);
                var distance = horizontal.Length();
                if (distance > MoveDeadzone)
                {
                    // Speed scales with distance so it ambles when close and sprints to catch up.
                    var direction = horizontal / distance;
                    var speed = Math.Clamp(distance * 3f, 1.8f, 40f);
                    this.position += direction * MathF.Min(speed * dt, distance);
                    this.facing = direction;
                    moving = true;
                }
                else
                {
                    this.mode = Mode.Idle;
                    this.ScheduleIdleAction();
                }

                break;
            }

            case Mode.Wander:
            {
                var toWander = this.wanderTarget - this.position;
                var horizontal = new Vector3(toWander.X, 0f, toWander.Z);
                var distance = horizontal.Length();
                if (distance > 0.25f)
                {
                    var direction = horizontal / distance;
                    this.position += direction * MathF.Min(WanderSpeed * dt, distance);
                    this.facing = direction;
                    moving = true;
                }
                else
                {
                    this.mode = Mode.Idle;
                    this.ScheduleIdleAction();
                }

                break;
            }

            case Mode.Idle when this.animTime >= this.nextIdleActionAt:
            {
                var roll = Rng.NextDouble();
                if (roll < 0.5)
                {
                    // Amble to a random spot in a ring around the trainer.
                    var angle = (float)(Rng.NextDouble() * Math.PI * 2.0);
                    var radius = 1.2f + ((float)Rng.NextDouble() * 1.6f);
                    this.wanderTarget = playerPosition +
                        (new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * radius);
                    this.mode = Mode.Wander;
                }
                else if (roll < 0.8 && playerDistance > 0.3f)
                {
                    // Turn to look at the trainer.
                    this.facing = toPlayer / playerDistance;
                    this.ScheduleIdleAction();
                }
                else
                {
                    // A couple of happy hops in place.
                    this.hopPhase = 0f;
                    this.hopBurstUntil = this.animTime + 0.7f;
                    this.ScheduleIdleAction();
                }

                break;
            }
        }

        // A little skip while travelling (like the HGSS walkers) or during an idle hop burst.
        var hopping = moving || this.animTime < this.hopBurstUntil;
        this.hopPhase = hopping ? this.hopPhase + (dt * 9f) : 0f;
        var hop = MathF.Abs(MathF.Sin(this.hopPhase)) * 0.08f;

        // Plant the feet on the actual terrain so slopes and stairs don't clip the sprite.
        this.position.Y += (GroundY(this.position, playerPosition.Y) - this.position.Y) * MathF.Min(1f, dt * 12f);

        var toCamera = HorizontalDirection(cameraPosition - this.position);
        var showBack = Vector3.Dot(this.facing, toCamera) < 0f;
        if (!PokemonSprites.TryGetFrame(lead.Species.Id, showBack, this.animTime, out var frame))
        {
            // The spritesheet streams in on first use; skip drawing until it is ready.
            return;
        }

        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toCamera));
        var center = this.position + new Vector3(0f, hop + (SpriteHeight * 0.5f), 0f);
        this.spriteList.Add(Billboard(center, SpriteHeight * frame.Aspect * 0.5f, SpriteHeight * 0.5f,
            right, (nint)frame.Handle.Handle, frame.Uv0.X, frame.Uv1.X, Vector4.One, pointSample: true));
    }

    // Immersive-mode wild spawns roaming the world, published by the app (WorldSpawnStage).
    private void AppendSpawns(WorldSpawnStage.SpawnSnap[] spawns, Vector3 cameraPosition)
    {
        foreach (var spawn in spawns)
        {
            var toCamera = HorizontalDirection(cameraPosition - spawn.Position);
            var showBack = Vector3.Dot(spawn.Facing, toCamera) < 0f;
            if (!PokemonSprites.TryGetFrame(spawn.SpeciesId, showBack, this.animTime + spawn.AnimSeed, out var frame))
            {
                continue;
            }

            var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toCamera));
            var center = spawn.Position + new Vector3(0f, SpawnHeight * 0.5f, 0f);
            this.spriteList.Add(Billboard(center, SpawnHeight * frame.Aspect * 0.5f, SpawnHeight * 0.5f,
                right, (nint)frame.Handle.Handle, frame.Uv0.X, frame.Uv1.X, Vector4.One, pointSample: true));
        }
    }

    private void ScheduleIdleAction()
    {
        this.nextIdleActionAt = this.animTime + 2.5f + ((float)Rng.NextDouble() * 3.5f);
    }

    // ---- In-world battle stage ----------------------------------------------------------

    private void UpdateBattleStage(PresentHookProbe probe, WorldBattleStage.Snapshot snap,
        Vector3 playerPosition, float playerRotation, Vector3 cameraPosition, float dt)
    {
        // Freeze the arena the moment the battle reaches the world so it doesn't swing
        // around with the player's facing mid-fight.
        if (!this.battleActive)
        {
            this.battleActive = true;
            this.battleForward = new Vector3(MathF.Sin(playerRotation), 0f, MathF.Cos(playerRotation));
            this.battlePlayerFeet = playerPosition + (this.battleForward * BattlePlayerRange);
            this.battlePlayerFeet.Y = GroundY(this.battlePlayerFeet, playerPosition.Y);
            this.battleWildFeet = playerPosition + (this.battleForward * BattleEnemyRange);
            this.battleWildFeet.Y = GroundY(this.battleWildFeet, playerPosition.Y);
            if (!this.placed)
            {
                // No follower on the field (disabled/fainted-into-battle edge): start in place.
                this.position = this.battlePlayerFeet;
                this.placed = true;
                this.territory = Plugin.ClientState.TerritoryType;
            }
        }

        // The follower runs to its battle station; once there it holds the line.
        var toStation = this.battlePlayerFeet - this.position;
        var horizontal = new Vector3(toStation.X, 0f, toStation.Z);
        var stationDistance = horizontal.Length();
        var running = stationDistance > 0.15f;
        if (running)
        {
            var direction = horizontal / stationDistance;
            var speed = Math.Clamp(stationDistance * 4f, 3f, 40f);
            this.position += direction * MathF.Min(speed * dt, stationDistance);
        }

        this.hopPhase = running ? this.hopPhase + (dt * 9f) : 0f;
        var hop = running ? MathF.Abs(MathF.Sin(this.hopPhase)) * 0.08f : 0f;
        this.position.Y += (GroundY(this.position, this.battlePlayerFeet.Y) - this.position.Y) *
            MathF.Min(1f, dt * 12f);
        this.facing = this.battleForward;

        // ---- The 2D→3D scene map. The traced Showdown choreography lives in a projected 2D
        // space where the far mon sits up-screen; in the world both battlers stand on the
        // ground. A uniform-scale map keeps arcs and effect sizes true, and a linear shear
        // brings the far anchor back down onto the actual enemy, so effects aimed at a
        // battler arrive at that battler while vertical motion keeps its full height.
        var playerH = BattleMonHeight;
        var wildH = BattleMonHeight * snap.WildBaseScale;
        var playerCenter = this.position + new Vector3(0f, hop + (playerH * 0.5f), 0f);
        var wildCenter = this.battleWildFeet + new Vector3(0f, wildH * 0.5f, 0f);

        // Let the app hit-test the world enemy and hang HP plates over both battlers.
        WorldBattleStage.ReportStage(playerCenter, playerH * 0.5f, wildCenter, wildH * 0.5f);

        var planeDelta = wildCenter - playerCenter;
        var planeRight = HorizontalDirection(planeDelta);
        var planeDistance = MathF.Max(1f, new Vector2(planeDelta.X, planeDelta.Z).Length());
        var map = new SceneMap(Vector2.Zero, new Vector2(planeDistance, -planeDistance * 0.5f));
        // Shear so scene-x=far lands at the enemy's real height instead of hDist/2 above it.
        var shearPerX = (planeDelta.Y - (planeDistance * 0.5f)) / planeDistance;

        Vector3 MapToWorld(Vector2 scenePoint) =>
            playerCenter + (planeRight * scenePoint.X) +
            new Vector3(0f, -scenePoint.Y + (scenePoint.X * shearPerX), 0f);

        // Screen shake (Earthquake and friends) jiggles the whole stage.
        var shake = 0f;
        if (snap.FxPlayback is { } shakePlayback)
        {
            shake = MoveAnims.ShakeY(shakePlayback, snap.FxAgeMs) * map.Sy;
        }

        var shakeOffset = new Vector3(0f, -shake, 0f);
        var toCamera = HorizontalDirection(cameraPosition - playerCenter);
        var billboardRight = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toCamera));

        this.spriteList.Clear();

        // Background flash (Night Shade's darkness, Solar Beam's sun) as a tinted plane
        // behind the battlers.
        if (snap.FxPlayback is { } bgPlayback &&
            MoveAnims.BackgroundWash(bgPlayback, snap.FxAgeMs, out var wash))
        {
            var washCenter = ((playerCenter + wildCenter) * 0.5f) + new Vector3(0f, 0.3f, 0f) + shakeOffset;
            this.spriteList.Add(Billboard(washCenter, planeDistance * 0.95f, planeDistance * 0.55f,
                billboardRight, 0, 0f, 1f, wash with { W = wash.W * 0.45f }, pointSample: false));
        }

        // Ball positions are shared by the ball itself and the wild being pulled into it.
        var ballGround = this.battleWildFeet + new Vector3(0f, 0.14f, 0f) + shakeOffset;
        var ballAir = snap.BallGrounded
            ? ballGround
            : Vector3.Lerp(playerCenter, wildCenter, snap.BallArcT) +
              new Vector3(0f, MathF.Sin(snap.BallArcT * MathF.PI) * 0.9f, 0f) + shakeOffset;

        // Enemy battler (drawn first: it is the far one).
        this.AddBattler(snap, isPlayer: false, wildCenter, wildH, planeRight, billboardRight, toCamera,
            shakeOffset, ballGround, map);

        // Player battler.
        this.AddBattler(snap, isPlayer: true, playerCenter, playerH, planeRight, billboardRight, toCamera,
            shakeOffset, Vector3.Zero, map);

        // Capture / send-out ball with its opening flash.
        if (snap.BallVisible)
        {
            if (snap.BallFlash > 0.01f)
            {
                var flashSize = 0.24f * (0.6f + (snap.BallFlash * 1.4f));
                this.spriteList.Add(Billboard(ballAir, flashSize, flashSize, billboardRight, 0, 0f, 1f,
                    new Vector4(1f, 1f, 0.92f, snap.BallFlash * 0.55f), pointSample: false));
            }

            if (AssetTextures.TryGet($"items/{snap.BallSpriteId}.png", out var ballTex, out var ballAspect))
            {
                this.spriteList.Add(RotatedBillboard(ballAir, 0.12f * MathF.Max(0.3f, ballAspect), 0.12f,
                    billboardRight, snap.BallAngle, (nint)ballTex.Handle, Vector4.One));
            }
        }

        // The traced move animation's effect layer, mapped onto the battle plane but
        // billboarded per-quad so the effects read from any camera angle.
        if (snap.FxPlayback is { } playback)
        {
            bool ResolveMonFrame(bool attackerSprite, out ImTextureID tex, out Vector2 uv0, out Vector2 uv1)
            {
                tex = default;
                uv0 = Vector2.Zero;
                uv1 = Vector2.One;
                var isPlayerMon = attackerSprite == playback.FromPlayer;
                var speciesId = isPlayerMon ? snap.PlayerSpeciesId : snap.WildSpeciesId;
                if (!PokemonSprites.TryGetFrame(speciesId, isPlayerMon, this.animTime, out var monFrame))
                {
                    return false;
                }

                tex = monFrame.Handle;
                uv0 = monFrame.Uv0;
                uv1 = monFrame.Uv1;
                return true;
            }

            MoveAnims.EvaluateEffects(playback, snap.FxAgeMs, map, snap.FxEffectScale, ResolveMonFrame,
                (tex, center, half, uv0, uv1, alpha) =>
                {
                    var world = MapToWorld(center) + shakeOffset;
                    this.spriteList.Add(new NativeWorldSprite(
                        world + new Vector3(0f, half.Y, 0f) - (billboardRight * half.X),
                        world + new Vector3(0f, half.Y, 0f) + (billboardRight * half.X),
                        world - new Vector3(0f, half.Y, 0f) + (billboardRight * half.X),
                        world - new Vector3(0f, half.Y, 0f) - (billboardRight * half.X),
                        (nint)tex.Handle,
                        uv0.X,
                        uv1.X,
                        new Vector4(1f, 1f, 1f, alpha),
                        PointSample: false));
                });
        }

        probe.SetNativeWorldSprites(this.spriteList);
        this.DrawBattlePopups(snap, playerCenter + new Vector3(0f, playerH * 0.55f, 0f),
            wildCenter + new Vector3(0f, wildH * 0.55f, 0f));
    }

    // One battler billboard: traced anim pose (offset/scale/alpha) + procedural lunge and
    // hurt flash, capture pull toward the ball, front/back sheet picked from the camera.
    private void AddBattler(WorldBattleStage.Snapshot snap, bool isPlayer, Vector3 center, float height,
        Vector3 planeRight, Vector3 billboardRight, Vector3 toCamera, Vector3 shakeOffset,
        Vector3 ballGround, in SceneMap map)
    {
        var speciesId = isPlayer ? snap.PlayerSpeciesId : snap.WildSpeciesId;
        var alpha = isPlayer ? snap.PlayerAlpha : snap.WildAlpha;
        var hurt = isPlayer ? snap.PlayerHurt : snap.WildHurt;
        var lunge = isPlayer ? snap.PlayerLunge : snap.WildLunge;
        var scale = isPlayer ? 1f : snap.WildScale;

        var poseOffset = Vector3.Zero;
        var poseScale = 1f;
        var poseAlpha = 1f;
        if (snap.FxPlayback is { } playback)
        {
            var attackerRole = isPlayer == playback.FromPlayer;
            var pose = MoveAnims.MonPose(playback, snap.FxAgeMs, attackerRole, map);
            // Pose offsets are local jumps/lunges; the plane shear correction is negligible here.
            poseOffset = (planeRight * pose.Offset.X) + new Vector3(0f, -pose.Offset.Y, 0f);
            poseScale = pose.ScaleMul;
            poseAlpha = pose.Alpha;
        }

        // Procedural fallback lunge toward the opponent + a hurt jitter/red flash.
        var lungeDirection = isPlayer ? planeRight : -planeRight;
        poseOffset += lungeDirection * (lunge * 0.5f);
        poseOffset += billboardRight * (MathF.Sin(this.animTime * 60f) * hurt * 0.04f);

        var effectiveCenter = center + poseOffset + shakeOffset;
        if (!isPlayer && snap.WildPullT > 0f)
        {
            effectiveCenter = Vector3.Lerp(effectiveCenter, ballGround, snap.WildPullT);
        }

        var finalAlpha = Math.Clamp(alpha * poseAlpha, 0f, 1f);
        if (finalAlpha <= 0.01f)
        {
            return;
        }

        var monFacing = isPlayer ? planeRight : -planeRight;
        var showBack = Vector3.Dot(monFacing, toCamera) < 0f;
        if (!PokemonSprites.TryGetFrame(speciesId, showBack, this.animTime, out var frame))
        {
            return;
        }

        var halfH = height * 0.5f * poseScale * scale;
        var halfW = halfH * frame.Aspect;
        var tint = new Vector4(1f, 1f - (hurt * 0.55f), 1f - (hurt * 0.55f), finalAlpha);
        this.spriteList.Add(Billboard(effectiveCenter, halfW, halfH, billboardRight,
            (nint)frame.Handle.Handle, frame.Uv0.X, frame.Uv1.X, tint, pointSample: true));
    }

    // Damage/status popups above the battlers, drawn as screen-space text anchored to the
    // world positions (the native pass has no font path).
    private void DrawBattlePopups(WorldBattleStage.Snapshot snap, Vector3 playerTop, Vector3 wildTop)
    {
        if (snap.Popups.Length == 0)
        {
            return;
        }

        var drawList = ImGui.GetBackgroundDrawList();
        var font = ImGui.GetFont();
        var baseSize = ImGui.GetFontSize();
        foreach (var popup in snap.Popups)
        {
            var progress = Math.Clamp(popup.Age / 1.15f, 0f, 1f);
            var rise = (1f - MathF.Pow(1f - progress, 2f)) * 0.55f;
            var fade = 1f - Math.Clamp((progress - 0.68f) / 0.32f, 0f, 1f);
            if (fade <= 0.01f)
            {
                continue;
            }

            var anchor = (popup.OnWild ? wildTop : playerTop) + new Vector3(0f, 0.25f + rise, 0f);
            if (!Plugin.GameGui.WorldToScreen(anchor, out var screen))
            {
                continue;
            }

            var valueSize = baseSize * 1.45f;
            var textWidth = ImGui.CalcTextSize(popup.Value).X * (valueSize / baseSize);
            var textPos = new Vector2(screen.X - (textWidth * 0.5f), screen.Y);
            drawList.AddText(font, valueSize, textPos + new Vector2(1.5f, 1.5f),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.75f * fade)), popup.Value);
            drawList.AddText(font, valueSize, textPos,
                ImGui.GetColorU32(popup.Color with { W = fade }), popup.Value);
            if (popup.Label.Length > 0)
            {
                var labelWidth = ImGui.CalcTextSize(popup.Label).X;
                var labelPos = new Vector2(screen.X - (labelWidth * 0.5f), screen.Y + valueSize + 1f);
                drawList.AddText(font, baseSize, labelPos + new Vector2(1f, 1f),
                    ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.7f * fade)), popup.Label);
                drawList.AddText(font, baseSize, labelPos,
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, fade)), popup.Label);
            }
        }
    }

    // ---- Shared helpers -----------------------------------------------------------------

    private static NativeWorldSprite Billboard(Vector3 center, float halfWidth, float halfHeight,
        Vector3 right, nint textureSrv, float u0, float u1, Vector4 tint, bool pointSample)
    {
        var up = new Vector3(0f, halfHeight, 0f);
        var side = right * halfWidth;
        return new NativeWorldSprite(
            center + up - side,
            center + up + side,
            center - up + side,
            center - up - side,
            textureSrv,
            u0,
            u1,
            tint,
            pointSample);
    }

    private static NativeWorldSprite RotatedBillboard(Vector3 center, float halfWidth, float halfHeight,
        Vector3 right, float angle, nint textureSrv, Vector4 tint)
    {
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var rotatedRight = ((right * cos) + (Vector3.UnitY * sin)) * halfWidth;
        var rotatedUp = ((Vector3.UnitY * cos) - (right * sin)) * halfHeight;
        return new NativeWorldSprite(
            center + rotatedUp - rotatedRight,
            center + rotatedUp + rotatedRight,
            center - rotatedUp + rotatedRight,
            center - rotatedUp - rotatedRight,
            textureSrv,
            0f,
            1f,
            tint,
            PointSample: true);
    }

    private static Vector3 HorizontalDirection(Vector3 v)
    {
        v.Y = 0f;
        return v.LengthSquared() < 0.0001f ? new Vector3(0f, 0f, 1f) : Vector3.Normalize(v);
    }

    // The terrain height under a point via the game's collision mesh; falls back to
    // fallbackY when the raycast misses or hits something implausibly far away (e.g. the
    // probe started above a bridge while the player is under it). Shared with the app's
    // immersive spawner.
    internal static unsafe float GroundY(Vector3 at, float fallbackY, float tolerance = 4f)
    {
        var probeTop = MathF.Max(at.Y, fallbackY) + 2.5f;
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(at.X, probeTop, at.Z),
                new Vector3(0f, -1f, 0f),
                out var hit,
                30f) &&
            MathF.Abs(hit.Point.Y - fallbackY) < tolerance)
        {
            return hit.Point.Y;
        }

        return fallbackY;
    }

    // The camera's world position, recovered by inverting the scene camera's view matrix
    // (the same matrix the native renderer draws with, so the two always agree).
    private static unsafe bool TryGetCameraPosition(out Vector3 position)
    {
        position = default;
        var cameraManager = SceneCameraManager.Instance();
        var sceneCamera = cameraManager is null ? null : cameraManager->CurrentCamera;
        if (sceneCamera is null)
        {
            return false;
        }

        var view = sceneCamera->ViewMatrix;
        view.M44 = 1f;
        if (!Matrix4x4.Invert(view, out var cameraWorld))
        {
            return false;
        }

        position = cameraWorld.Translation;
        return true;
    }
}
