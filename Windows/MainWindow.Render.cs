using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using VideoSyncPrototype.Emotes;
using VideoSyncPrototype.Fun;
using VideoSyncPrototype.Rendering;

namespace VideoSyncPrototype.Windows;

public sealed partial class MainWindow
{
    // In-window preview, world/screen render surfaces, native capture, renderer process, decode helpers.

    private void DrawInWindowPreview()
    {
        ImGui.Text("Fallback preview (PNG snapshots; inactive while GPU streaming works)");

        if (this.rendererProcess is not null)
        {
            var running = !this.rendererProcess.HasExited;
            ImGui.Text(running ? "Renderer bridge: running" : "Renderer bridge: stopped");
            ImGui.SameLine();
            if (ImGui.Button("Stop Preview"))
            {
                this.StopInWindowPreview();
                this.status = "Stopped the renderer bridge.";
            }
        }
        else
        {
            ImGui.TextWrapped("Join or play a video to feed browser frames into this preview.");
        }

        var availableWidth = Math.Max(240, ImGui.GetContentRegionAvail().X);
        var previewWidth = Math.Min(availableWidth, 820);
        var previewSize = new Vector2(previewWidth, previewWidth * 9f / 16f);
        this.DrawScreenSurface(previewSize, showWaitingText: true);
    }

    private void TryUpdatePlaybackStatus()
    {
        var statusPath = this.GetStatusPath();
        try
        {
            if (!File.Exists(statusPath))
            {
                return;
            }

            var writeTime = File.GetLastWriteTimeUtc(statusPath);
            if (writeTime <= this.lastStatusReadUtc)
            {
                return;
            }

            // A seek/nudge was just sent; the file still reports the old position,
            // so hold the optimistic value until the player has caught up.
            if (DateTime.UtcNow < this.playbackTimeOverrideUntilUtc)
            {
                return;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statusPath));
            this.playbackTime = document.RootElement.GetProperty("t").GetDouble();
            this.playbackDuration = document.RootElement.GetProperty("d").GetDouble();
            if (DateTime.UtcNow >= this.pauseOverrideUntilUtc)
            {
                this.playbackPaused = document.RootElement.GetProperty("p").GetBoolean();
            }

            // Page state for Nearby Broadcast sync (older OverlayPlayer builds omit these).
            if (document.RootElement.TryGetProperty("url", out var urlElement) &&
                urlElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                this.browserPageUrl = urlElement.GetString() ?? string.Empty;
            }

            // The joinable room URL OverlayPlayer parsed off the page (may be absent when
            // not on a Watch2Gether room, or null when it couldn't find a shareable link).
            this.browserWatch2GetherRoomUrl =
                document.RootElement.TryGetProperty("w2gRoom", out var roomElement) &&
                roomElement.ValueKind == System.Text.Json.JsonValueKind.String
                    ? roomElement.GetString() ?? string.Empty
                    : string.Empty;

            if (document.RootElement.TryGetProperty("sx", out var sxElement) &&
                sxElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                this.browserScrollX = (int)sxElement.GetDouble();
            }

            if (document.RootElement.TryGetProperty("sy", out var syElement) &&
                syElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                this.browserScrollY = (int)syElement.GetDouble();
            }

            if (document.RootElement.TryGetProperty("vw", out var vwElement) &&
                vwElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                this.browserViewportWidth = (int)vwElement.GetDouble();
            }

            if (document.RootElement.TryGetProperty("vh", out var vhElement) &&
                vhElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                this.browserViewportHeight = (int)vhElement.GetDouble();
            }

            if (document.RootElement.TryGetProperty("dw", out var dwElement) &&
                dwElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                this.browserDocumentWidth = (int)dwElement.GetDouble();
            }

            if (document.RootElement.TryGetProperty("dh", out var dhElement) &&
                dhElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                this.browserDocumentHeight = (int)dhElement.GetDouble();
            }

            if (document.RootElement.TryGetProperty("z", out var zoomElement) &&
                zoomElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                this.browserZoom = zoomElement.GetDouble();
            }

            if (document.RootElement.TryGetProperty("rate", out var rateElement) &&
                rateElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                this.browserMediaRate = rateElement.GetDouble();
            }

            if (document.RootElement.TryGetProperty("vMuted", out var mediaMutedElement) &&
                mediaMutedElement.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                this.browserMediaMuted = mediaMutedElement.GetBoolean();
            }

            if (document.RootElement.TryGetProperty("fs", out var fullscreenElement) &&
                fullscreenElement.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                this.browserMediaFullscreen = fullscreenElement.GetBoolean();
            }

            this.lastStatusReadUtc = writeTime;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            // The file is being rewritten; keep the last known state.
        }
    }

    /// <summary>
    /// The status file is only written at 2 Hz, so the raw playback time is up to
    /// half a second stale. Extrapolating from the file's write time gives a much
    /// better estimate for drift checks, broadcasts, and the seek bar.
    /// </summary>
    private double GetEstimatedPlaybackTime()
    {
        if (this.playbackPaused || this.lastStatusReadUtc == DateTime.MinValue)
        {
            return this.playbackTime;
        }

        var elapsed = Math.Clamp((DateTime.UtcNow - this.lastStatusReadUtc).TotalSeconds, 0, 5);
        var estimated = this.playbackTime + elapsed;
        return this.playbackDuration > 0
            ? Math.Min(estimated, this.playbackDuration)
            : estimated;
    }

    private void SetOptimisticPlaybackTime(double seconds)
    {
        this.playbackTime = Math.Max(0, seconds);
        this.lastStatusReadUtc = DateTime.UtcNow;
        this.playbackTimeOverrideUntilUtc = DateTime.UtcNow.AddSeconds(1.2);
    }

    private void SendPlaybackCommand(string command, double value = 0)
    {
        var seq = Interlocked.Increment(ref this.controlSeq);
        var payload =
            $"{{\"seq\":{seq},\"cmd\":\"{command}\",\"value\":{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
        try
        {
            WriteFileAtomic(this.GetControlPath(), payload);
        }
        catch (IOException)
        {
            this.status = "Could not send the playback command; is the renderer bridge still running?";
        }
    }

    private void SendNavigationCommand(string url)
    {
        var seq = Interlocked.Increment(ref this.controlSeq);
        var payload =
            $"{{\"seq\":{seq},\"cmd\":\"navigate\",\"url\":{JsonSerializer.Serialize(url)}}}";
        try
        {
            WriteFileAtomic(this.GetControlPath(), payload);
        }
        catch (IOException)
        {
            this.status = "Could not send the browser navigation command; is the renderer bridge still running?";
        }
    }

    private void SetVideoFullscreen(bool enabled)
    {
        this.videoFullscreen = enabled;
        this.SendPlaybackCommand("setvideofullscreen", enabled ? 1 : 0);
    }

    private void AllowRendererForeground()
    {
        try
        {
            if (this.rendererProcess is not null && !this.rendererProcess.HasExited)
            {
                AllowSetForegroundWindow(this.rendererProcess.Id);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Could not allow renderer foreground access.");
        }
    }

    /// <summary>
    /// Drives PyonPix-style spatial audio: volume falls off with the player's distance
    /// to the screen and the sound pans toward the screen's on-screen position.
    /// </summary>
    private void UpdateAudioBridge()
    {
        if (this.rendererProcess is null)
        {
            return;
        }

        var volume = this.audioMuted ? 0f : this.masterVolume;
        var pan = 0f;
        if (this.spatialAudio && this.worldScreenEnabled && this.worldScreenAnchor is { } anchor)
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player is not null)
            {
                const float nearDistance = 4f;
                var distance = Vector3.Distance(player.Position, anchor);
                var falloff = distance <= nearDistance
                    ? 1f
                    : Math.Max(0f, 1f - ((distance - nearDistance) / Math.Max(1f, this.audioRange - nearDistance)));
                volume *= falloff;
            }

            if (Plugin.GameGui.WorldToScreen(anchor, out var screenPos))
            {
                var display = ImGui.GetIO().DisplaySize;
                if (display.X > 0)
                {
                    pan = Math.Clamp(((screenPos.X / display.X) - 0.5f) * 1.6f, -1f, 1f);
                }
            }
        }

        var now = DateTime.UtcNow;
        if ((now - this.lastAudioWriteUtc).TotalMilliseconds < 250)
        {
            return;
        }

        if (Math.Abs(volume - this.lastSentVolume) < 0.02f &&
            Math.Abs(pan - this.lastSentPan) < 0.05f &&
            this.audioMuted == this.lastSentMuted)
        {
            return;
        }

        var payload =
            $"{{\"volume\":{volume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}," +
            $"\"pan\":{pan.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}," +
            $"\"muted\":{(this.audioMuted ? "true" : "false")}}}";
        try
        {
            WriteFileAtomic(this.GetAudioPath(), payload);
            this.lastSentVolume = volume;
            this.lastSentPan = pan;
            this.lastSentMuted = this.audioMuted;
            this.lastAudioWriteUtc = now;
        }
        catch (IOException)
        {
            // Retry on a later frame.
        }
    }

    private static void WriteFileAtomic(string path, string content)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, true);
    }

    public void DrawWorldSurfaceOverlay()
    {
        this.UpdateAudioBridge();
        this.TickNativeScreenRecovery();
        this.pokemonFollower.Update(this.presentHookProbe);

        // Immerse mode needs the Lillypad Go app alive even before the phone is first
        // opened this session; creating the phone screen instantiates it.
        if (Phone.Plugin.LillypadGo?.ImmersiveModeEnabled == true)
        {
            this.phoneScreen ??= new Phone.PhoneScreen(Phone.Plugin.Cfg);
        }

        Phone.Apps.LillypadGo.LillypadGoApp.Instance?.DrawImmersiveOverlay();

        if (!this.worldScreenEnabled || this.worldScreenAnchor is not { } anchor)
        {
            this.presentHookProbe.ClearNativeQuad();
            this.presentHookProbe.SetNativeTexture(0);
            return;
        }

        var (upscaleFilterMode, upscaleSharpenAmount) = this.ResolveUpscale();
        this.presentHookProbe.NativeUpscaleFilter = upscaleFilterMode;
        this.presentHookProbe.NativeUpscaleSharpness = upscaleSharpenAmount;

        var (debandStrength, artifactStrength) = this.ResolveEnhance();
        this.presentHookProbe.NativeDebandStrength = debandStrength;
        this.presentHookProbe.NativeArtifactStrength = artifactStrength;
        this.presentHookProbe.NativeCompareSplit = this.compareSplit ? 0.5f : 0f;

        if (this.worldScreenLockAspect)
        {
            this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
        }

        var halfWidth = Math.Max(0.5f, this.worldScreenWidth) * 0.5f;
        var halfHeight = Math.Max(0.25f, this.worldScreenHeight) * 0.5f;
        var right = new Vector3(MathF.Cos(this.worldScreenRotation), 0f, -MathF.Sin(this.worldScreenRotation));
        var up = new Vector3(0f, 1f, 0f);

        var topLeftWorld = anchor - (right * halfWidth) + (up * halfHeight);
        var topRightWorld = anchor + (right * halfWidth) + (up * halfHeight);
        var bottomRightWorld = anchor + (right * halfWidth) - (up * halfHeight);
        var bottomLeftWorld = anchor - (right * halfWidth) - (up * halfHeight);

        // The native scene-pass renderer clips against the camera itself, so the quad
        // and video texture are always kept up to date, even when corners are off screen.
        this.TryUpdateFrameTexture();
        this.TryUpdateSharedTexture();
        this.presentHookProbe.SetNativeQuad(topLeftWorld, topRightWorld, bottomRightWorld, bottomLeftWorld);
        this.presentHookProbe.SetNativeDecorations(this.BuildWorldScreenDecorations(anchor, right, up, halfWidth, halfHeight));
        this.presentHookProbe.SetNativeTexture(
            this.frameTexture is null ? 0 : (nint)this.frameTexture.Handle.Handle);
        this.presentHookProbe.SetNativeSharedTexture(this.sharedTextureHandle);

        if (!this.drawImguiWorldScreen)
        {
            return;
        }

        if (!Plugin.GameGui.WorldToScreen(topLeftWorld, out var topLeft) ||
            !Plugin.GameGui.WorldToScreen(topRightWorld, out var topRight) ||
            !Plugin.GameGui.WorldToScreen(bottomRightWorld, out var bottomRight) ||
            !Plugin.GameGui.WorldToScreen(bottomLeftWorld, out var bottomLeft))
        {
            return;
        }
        var drawList = ImGui.GetBackgroundDrawList();
        var clipRects = this.GetWorldScreenVisibleClipRects(
            anchor,
            right,
            up,
            halfWidth,
            halfHeight,
            topLeft,
            topRight,
            bottomRight,
            bottomLeft);

        if (this.frameTexture is not null)
        {
            foreach (var clipRect in clipRects)
            {
                drawList.PushClipRect(clipRect.Min, clipRect.Max, true);
                drawList.AddImageQuad(
                    this.frameTexture.Handle,
                    topLeft,
                    topRight,
                    bottomRight,
                    bottomLeft,
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, 1f),
                    0xFFFFFFFF);
                drawList.PopClipRect();
            }
        }
        else
        {
            foreach (var clipRect in clipRects)
            {
                drawList.PushClipRect(clipRect.Min, clipRect.Max, true);
                drawList.AddQuadFilled(topLeft, topRight, bottomRight, bottomLeft, 0xFF050505);
                drawList.PopClipRect();
            }
        }

        drawList.AddQuad(topLeft, topRight, bottomRight, bottomLeft, 0xAA1F1F1F, 2f);
    }

    private void TickNativeScreenRecovery()
    {
        if (!this.worldScreenEnabled || !this.drawImguiWorldScreen || this.userChose2DFallback)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - this.lastNativeInstallRetryUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        this.lastNativeInstallRetryUtc = now;
        if (!this.presentHookProbe.TryInstall())
        {
            return;
        }

        this.drawImguiWorldScreen = false;
        this.presentHookProbe.NativeTestDrawEnabled = true;
        this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
        this.status = "Switched to the 3D screen. Your character should now block it correctly.";
    }

    private List<ScreenRect> GetWorldScreenVisibleClipRects(
        Vector3 anchor,
        Vector3 right,
        Vector3 up,
        float halfWidth,
        float halfHeight,
        Vector2 topLeft,
        Vector2 topRight,
        Vector2 bottomRight,
        Vector2 bottomLeft)
    {
        var bounds = ScreenRect.FromPoints(topLeft, topRight, bottomRight, bottomLeft).Inflate(2f);
        var visibleRects = new List<ScreenRect> { bounds };
        if (!this.worldScreenActorOcclusion)
        {
            return visibleRects;
        }

        var normal = new Vector3(MathF.Sin(this.worldScreenRotation), 0f, MathF.Cos(this.worldScreenRotation));
        for (var i = 0; i < Plugin.ObjectTable.Length; i++)
        {
            var gameObject = Plugin.ObjectTable[i];
            if (gameObject is null)
            {
                continue;
            }

            var position = gameObject.Position;
            var delta = position - anchor;
            var frontDistance = Vector3.Dot(delta, normal);
            if (frontDistance is < -0.25f or > 12f)
            {
                continue;
            }

            var radius = Math.Clamp(gameObject.HitboxRadius, 0.25f, 2.5f);
            var objectX = Vector3.Dot(delta, right);
            var objectFootY = Vector3.Dot(delta, up);
            var objectHeight = Math.Clamp(radius * 3.2f, 1.4f, 4.0f);
            var objectHeadY = objectFootY + objectHeight;
            if (Math.Abs(objectX) > halfWidth + radius ||
                objectHeadY < -halfHeight ||
                objectFootY > halfHeight)
            {
                continue;
            }

            var footWorld = position;
            var headWorld = position + new Vector3(0f, objectHeight, 0f);
            if (!Plugin.GameGui.WorldToScreen(footWorld, out var footScreen) ||
                !Plugin.GameGui.WorldToScreen(headWorld, out var headScreen))
            {
                continue;
            }

            var heightPixels = Math.Abs(footScreen.Y - headScreen.Y);
            if (heightPixels < 12f)
            {
                continue;
            }

            var widthPixels = Math.Clamp(heightPixels * 0.45f, 24f, 220f) + (this.worldScreenOcclusionPadding * 2f);
            var min = new Vector2(
                Math.Min(footScreen.X, headScreen.X) - (widthPixels * 0.5f),
                Math.Min(footScreen.Y, headScreen.Y) - this.worldScreenOcclusionPadding);
            var max = new Vector2(
                Math.Max(footScreen.X, headScreen.X) + (widthPixels * 0.5f),
                Math.Max(footScreen.Y, headScreen.Y) + this.worldScreenOcclusionPadding);
            var occluder = new ScreenRect(min, max).Intersect(bounds);
            if (occluder.IsEmpty)
            {
                continue;
            }

            visibleRects = SubtractRect(visibleRects, occluder);
            if (visibleRects.Count == 0)
            {
                break;
            }
        }

        return visibleRects;
    }

    private static List<ScreenRect> SubtractRect(List<ScreenRect> source, ScreenRect cutout)
    {
        var result = new List<ScreenRect>();
        foreach (var rect in source)
        {
            if (!rect.Intersects(cutout))
            {
                result.Add(rect);
                continue;
            }

            var intersection = rect.Intersect(cutout);
            if (intersection.Min.Y > rect.Min.Y)
            {
                result.Add(new ScreenRect(rect.Min, new Vector2(rect.Max.X, intersection.Min.Y)));
            }

            if (intersection.Max.Y < rect.Max.Y)
            {
                result.Add(new ScreenRect(new Vector2(rect.Min.X, intersection.Max.Y), rect.Max));
            }

            if (intersection.Min.X > rect.Min.X)
            {
                result.Add(new ScreenRect(
                    new Vector2(rect.Min.X, intersection.Min.Y),
                    new Vector2(intersection.Min.X, intersection.Max.Y)));
            }

            if (intersection.Max.X < rect.Max.X)
            {
                result.Add(new ScreenRect(
                    new Vector2(intersection.Max.X, intersection.Min.Y),
                    new Vector2(rect.Max.X, intersection.Max.Y)));
            }
        }

        return result;
    }

    public void DrawScreenSurface()
    {
        var available = ImGui.GetContentRegionAvail();
        var width = Math.Max(260, available.X);
        var height = Math.Max(160, available.Y);
        var fitWidth = Math.Min(width, height * 16f / 9f);
        var fitHeight = fitWidth * 9f / 16f;
        if (fitHeight > height)
        {
            fitHeight = height;
            fitWidth = fitHeight * 16f / 9f;
        }

        var offset = new Vector2(Math.Max(0, (width - fitWidth) * 0.5f), Math.Max(0, (height - fitHeight) * 0.5f));
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, height));
        ImGui.SetCursorScreenPos(cursor + offset);
        this.DrawScreenSurface(new Vector2(fitWidth, fitHeight), showWaitingText: false);
    }

    private void DrawScreenSurface(Vector2 previewSize, bool showWaitingText)
    {
        this.TryUpdateFrameTexture();

        if (this.frameTexture is not null)
        {
            ImGui.Image(this.frameTexture.Handle, previewSize);
        }
        else
        {
            var cursor = ImGui.GetCursorScreenPos();
            ImGui.Dummy(previewSize);
            var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;
            if (running)
            {
                // Renderer is up, first frame not here yet: show the loading animation.
                this.DrawLoadingSurface(cursor, previewSize);
            }
            else
            {
                var drawList = ImGui.GetWindowDrawList();
                drawList.AddRectFilled(cursor, cursor + previewSize, 0xFF050505, 2f);
                drawList.AddRect(cursor, cursor + previewSize, 0xAA1F1F1F, 2f);
                if (showWaitingText)
                {
                    ImGui.SetCursorScreenPos(cursor + new Vector2(12, 12));
                    ImGui.TextWrapped("Nothing is playing yet.");
                }
            }

            ImGui.SetCursorScreenPos(cursor + new Vector2(0, previewSize.Y + 4));
        }
    }

    private void TryUpdateFrameTexture()
    {
        if (this.frameTextureTask is { IsCompleted: true })
        {
            try
            {
                var nextTexture = this.frameTextureTask.GetAwaiter().GetResult();
                this.frameTexture?.Dispose();
                this.frameTexture = nextTexture;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Could not load captured renderer frame.");
            }
            finally
            {
                this.frameTextureTask = null;
            }
        }

        if (this.frameTextureTask is not null)
        {
            return;
        }

        var framePath = this.GetCaptureFramePath();
        if (!File.Exists(framePath))
        {
            return;
        }

        DateTime writeTime;
        byte[] bytes;
        try
        {
            writeTime = File.GetLastWriteTimeUtc(framePath);
            if (writeTime <= this.lastLoadedFrameWriteUtc)
            {
                return;
            }

            bytes = File.ReadAllBytes(framePath);
        }
        catch (IOException)
        {
            return;
        }

        this.lastLoadedFrameWriteUtc = writeTime;
        this.frameTextureTask = Plugin.TextureProvider.CreateFromImageAsync(bytes, $"VideoSyncFrame-{writeTime.Ticks}", CancellationToken.None);
    }

    private void StopInWindowPreview()
    {
        this.StopRendererProcess();
        this.frameTexture?.Dispose();
        this.frameTexture = null;
        this.frameTextureTask = null;
        this.sharedTextureHandle = 0;
        this.lastSharedInfoWriteUtc = DateTime.MinValue;
        this.presentHookProbe.SetNativeTexture(0);
        this.presentHookProbe.SetNativeSharedTexture(0);
        this.SurfaceWindow.IsOpen = false;
    }

    private void TickRendererHealth()
    {
        if (this.rendererProcess is null || !this.rendererProcess.HasExited)
        {
            return;
        }

        this.rendererProcess.Dispose();
        this.rendererProcess = null;
        this.sharedTextureHandle = 0;
        this.lastSharedInfoWriteUtc = DateTime.MinValue;
        this.presentHookProbe.SetNativeSharedTexture(0);
        this.presentHookProbe.SetNativeTexture(0);
        this.status = "The video browser stopped. Start or join a room again.";
    }

    private void StopRendererProcess()
    {
        var process = this.rendererProcess;
        if (process is null)
        {
            return;
        }

        this.rendererProcess = null;
        _ = Task.Run(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1000))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Could not stop renderer bridge process cleanly.");
            }
            finally
            {
                process.Dispose();
            }
        });
    }

    /// <summary>
    /// Reads the sidecar file that OverlayPlayer writes when it starts streaming its
    /// browser frames into a shared D3D11 texture. The handle in that file can be
    /// opened directly on the game's device for real-time video.
    /// </summary>
    private void TryUpdateSharedTexture()
    {
        var infoPath = this.GetSharedInfoPath();
        try
        {
            if (!File.Exists(infoPath))
            {
                this.sharedTextureHandle = 0;
                this.lastSharedInfoWriteUtc = DateTime.MinValue;
                return;
            }

            var writeTime = File.GetLastWriteTimeUtc(infoPath);
            if (writeTime <= this.lastSharedInfoWriteUtc)
            {
                return;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(infoPath));
            if (document.RootElement.TryGetProperty("frames", out var framesElement) &&
                framesElement.TryGetInt64(out var frames))
            {
                if (frames <= 0)
                {
                    this.sharedTextureHandle = 0;
                    this.presentHookProbe.SetNativeSharedTexture(0);
                    return;
                }

                // The sidecar's frame counter is cumulative and rewritten every ~2s, so the
                // delta between two writes gives the live capture rate arriving from the browser.
                if (this.lastCaptureFpsSampleUtc != DateTime.MinValue)
                {
                    var elapsed = (writeTime - this.lastCaptureFpsSampleUtc).TotalSeconds;
                    if (elapsed > 0.1)
                    {
                        this.captureFps = (float)((frames - this.lastCaptureFrameCount) / elapsed);
                    }
                }

                this.lastCaptureFrameCount = frames;
                this.lastCaptureFpsSampleUtc = writeTime;
            }

            var handle = document.RootElement.GetProperty("handle").GetInt64();
            this.sharedTextureHandle = (nint)handle;
            this.lastSharedInfoWriteUtc = writeTime;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            // The file is being rewritten; try again next frame.
        }
    }

    private string GetCaptureFramePath()
    {
        return Path.Combine(this.pluginDirectory, "videosync-preview.jpg");
    }

    private string GetSharedInfoPath()
    {
        return Path.Combine(this.pluginDirectory, "videosync-preview.shared.json");
    }

    private string GetControlPath()
    {
        return Path.Combine(this.pluginDirectory, "videosync-preview.control.json");
    }

    private string GetStatusPath()
    {
        return Path.Combine(this.pluginDirectory, "videosync-preview.status.json");
    }

    private string GetAudioPath()
    {
        return Path.Combine(this.pluginDirectory, "videosync-preview.audio.json");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale frame is harmless; the next successful capture overwrites it.
        }
    }

    private static string BuildWatchUrl(SyncPayload payload)
    {
        var targetSeconds = Math.Max(0, (int)Math.Floor(payload.GetCurrentVideoSeconds()));
        return $"https://www.youtube.com/watch?v={Uri.EscapeDataString(payload.VideoId)}&t={targetSeconds}s&autoplay=1";
    }

    private static bool TryDecode(string code, out SyncPayload payload, out string error)
    {
        payload = default;
        error = string.Empty;

        try
        {
            payload = SyncCode.Decode(code);
            if (string.IsNullOrWhiteSpace(payload.VideoId))
            {
                error = "That code decoded, but it did not contain a video id.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not decode that sync code: {ex.Message}";
            return false;
        }
    }

    private static string FormatSummary(SyncPayload payload)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(payload.StartUnixSeconds).ToLocalTime();
        var target = TimeSpan.FromSeconds(Math.Max(0, payload.GetCurrentVideoSeconds()));
        var options = payload.Options is { } opt
            ? $"\nFullscreen: {(opt.VideoFullscreen ? "yes" : "no")}\nAdblock: {(opt.AdBlock ? "yes" : "no")}"
            : string.Empty;
        var placement = payload.Screen is { } screen
            ? $"\nScreen: {(screen.Enabled ? "shown" : "hidden")} at {screen.X:0.0}, {screen.Y:0.0}, {screen.Z:0.0}"
            : "\nScreen: receiver default";
        return $"Video: {payload.VideoId}\nStart: {start:g}\nOffset: {FormatTimestamp(payload.OffsetSeconds)}\nCurrent target: {FormatTimestamp(target.TotalSeconds)}{options}{placement}";
    }

    private static bool TryExtractYouTubeId(string input, out string videoId)
    {
        videoId = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        input = input.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            videoId = input;
            return LooksLikeYouTubeId(videoId);
        }

        var host = uri.Host.ToLowerInvariant();
        if (host is "youtu.be" or "www.youtu.be")
        {
            videoId = uri.AbsolutePath.Trim('/');
            return LooksLikeYouTubeId(videoId);
        }

        if (!host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var queryVideoId = GetQueryValue(uri.Query, "v");
        if (LooksLikeYouTubeId(queryVideoId))
        {
            videoId = queryVideoId;
            return true;
        }

        var path = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length >= 2 && (path[0] is "shorts" or "embed" or "live") && LooksLikeYouTubeId(path[1]))
        {
            videoId = path[1];
            return true;
        }

        return false;
    }

    private static string GetQueryValue(string query, string key)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && Uri.UnescapeDataString(pieces[0]) == key)
            {
                return Uri.UnescapeDataString(pieces[1].Replace("+", " "));
            }
        }

        return string.Empty;
    }

    private static bool LooksLikeYouTubeId(string value)
    {
        if (value.Length != 11)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseTimestamp(string input, out double seconds)
    {
        seconds = 0;
        input = input.Trim();

        if (double.TryParse(input, out seconds))
        {
            return seconds >= 0;
        }

        var parts = input.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var multiplier = 1;
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (!double.TryParse(parts[i], out var value) || value < 0)
            {
                return false;
            }

            seconds += value * multiplier;
            multiplier *= 60;
        }

        return true;
    }

    private static string FormatTimestamp(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private static float RadiansToDegrees(float radians)
    {
        return radians * 180f / MathF.PI;
    }

    private static float DegreesToRadians(float degrees)
    {
        return degrees * MathF.PI / 180f;
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private readonly record struct SnowSyncBurst(DateTime DueUtc, string CommandPrefix, string Code);

    private readonly record struct SnowSyncshell(string Gid, int ShellNumber, bool Enabled);

    private readonly record struct ScreenRect(Vector2 Min, Vector2 Max)
    {
        public bool IsEmpty => this.Max.X <= this.Min.X || this.Max.Y <= this.Min.Y;

        public static ScreenRect FromPoints(params Vector2[] points)
        {
            var min = points[0];
            var max = points[0];
            foreach (var point in points)
            {
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }

            return new ScreenRect(min, max);
        }

        public ScreenRect Inflate(float amount)
        {
            var delta = new Vector2(amount, amount);
            return new ScreenRect(this.Min - delta, this.Max + delta);
        }

        public bool Intersects(ScreenRect other)
        {
            return this.Min.X < other.Max.X &&
                   this.Max.X > other.Min.X &&
                   this.Min.Y < other.Max.Y &&
                   this.Max.Y > other.Min.Y;
        }

        public ScreenRect Intersect(ScreenRect other)
        {
            return new ScreenRect(Vector2.Max(this.Min, other.Min), Vector2.Min(this.Max, other.Max));
        }
    }
}
