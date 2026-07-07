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
    // Screen-share tab: capture source, resolution, enhancement/upscaling controls.

    private void DrawScreenTab(bool running)
    {
        ImGui.Spacing();

        if (running)
        {
            this.DrawPlaybackSection();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }
        else
        {
            ImGui.TextDisabled("Nothing on screen yet. Start or join a room on the Watch tab.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        if (UiTheme.BeginCollapsibleSection("Audio", defaultOpen: true, warm: true))
        {
            this.DrawAudioControls();
            ImGui.TreePop();
        }

        if (UiTheme.BeginCollapsibleSection("Screen layout", defaultOpen: true, warm: true))
        {
            UiTheme.SectionTitle("Placement");
            ImGui.TextDisabled("Put the floating screen where your group is looking.");
            ImGui.Spacing();

            if (UiTheme.PrimaryButton(this.worldScreenAnchor is null ? "Place screen in front of me" : "Move screen to me"))
            {
                if (this.PlaceWorldScreenInFrontOfPlayer())
                {
                    this.EnableNativeWorldScreen();
                    this.QueueSnowSyncBroadcast();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Turn toward me"))
            {
                this.FaceWorldScreenToPlayer();
                this.QueueSnowSyncBroadcast();
            }

            ImGui.SameLine();
            if (this.worldScreenEnabled)
            {
                if (UiTheme.DangerButton("Hide screen"))
                {
                    this.worldScreenEnabled = false;
                    this.presentHookProbe.NativeTestDrawEnabled = false;
                    this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
                    this.presentHookProbe.ClearNativeQuad();
                    this.status = "Hid the screen. Show it again any time.";
                    this.QueueSnowSyncBroadcast();
                }
            }
            else if (ImGui.Button("Show screen"))
            {
                if (this.PlaceWorldScreenInFrontOfPlayer())
                {
                    this.EnableNativeWorldScreen();
                    this.QueueSnowSyncBroadcast();
                }
            }

            ImGui.Spacing();
            UiTheme.SectionTitle("Size");
            ImGui.Spacing();
            ImGui.PushItemWidth(-130f);
            UiTheme.PushSliderAccent();
            if (ImGui.SliderFloat("Width", ref this.worldScreenWidth, 1f, 14f, "%.1f yalms"))
            {
                if (this.worldScreenLockAspect)
                {
                    this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
                }

                this.QueueSnowSyncBroadcast();
            }

            var aspectLocked = this.worldScreenLockAspect;
            if (aspectLocked)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.SliderFloat("Height", ref this.worldScreenHeight, 0.5f, 10f, "%.1f yalms"))
            {
                this.QueueSnowSyncBroadcast();
            }

            if (aspectLocked)
            {
                ImGui.EndDisabled();
            }

            UiTheme.PopSliderAccent();
            ImGui.PopItemWidth();

            if (ImGui.Checkbox("Lock to 16:9", ref this.worldScreenLockAspect) && this.worldScreenLockAspect)
            {
                this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
                this.QueueSnowSyncBroadcast();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Turn off to stretch the picture. Width and Height then move independently.");
            }

            ImGui.Spacing();
            UiTheme.SectionTitle("Position");
            ImGui.Spacing();
            ImGui.PushItemWidth(-130f);
            UiTheme.PushSliderAccent();

            var elevation = this.worldScreenElevation;
            if (ImGui.SliderFloat("Elevation", ref elevation, -4f, 8f, "%.1f yalms"))
            {
                if (this.worldScreenAnchor is { } elevated)
                {
                    this.worldScreenAnchor = elevated + new Vector3(0f, elevation - this.worldScreenElevation, 0f);
                }

                this.worldScreenElevation = elevation;
                this.QueueSnowSyncBroadcast();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Raise or lower the screen from where it was placed.");
            }

            var push = this.worldScreenPush;
            if (ImGui.SliderFloat("Distance", ref push, -6f, 14f, "%.1f yalms"))
            {
                if (this.worldScreenAnchor is { } pushed)
                {
                    this.worldScreenAnchor = pushed + (this.GetViewerAwayAxis() * (push - this.worldScreenPush));
                }

                this.worldScreenPush = push;
                this.QueueSnowSyncBroadcast();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Push the screen closer or farther from you.");
            }

            var yawDegrees = RadiansToDegrees(this.worldScreenRotation);
            if (ImGui.SliderFloat("Facing angle", ref yawDegrees, -180f, 180f, "%.0f deg"))
            {
                this.worldScreenRotation = DegreesToRadians(yawDegrees);
                this.QueueSnowSyncBroadcast();
            }

            UiTheme.PopSliderAccent();
            ImGui.PopItemWidth();
            ImGui.Spacing();
            UiTheme.SectionTitle("Fine position");
            ImGui.Spacing();

            if (UiTheme.IconButton(FontAwesomeIcon.ArrowLeft, "videosync-nudge-left", new Vector2(32f, 0f), "Nudge left"))
            {
                this.NudgeScreenFromViewer(-0.25f, 0f, 0f);
            }

            ImGui.SameLine();
            if (UiTheme.IconButton(FontAwesomeIcon.ArrowRight, "videosync-nudge-right", new Vector2(32f, 0f), "Nudge right"))
            {
                this.NudgeScreenFromViewer(0.25f, 0f, 0f);
            }

            ImGui.SameLine();
            if (UiTheme.IconButton(FontAwesomeIcon.ArrowUp, "videosync-nudge-up", new Vector2(32f, 0f), "Raise"))
            {
                this.NudgeScreenFromViewer(0f, 0.25f, 0f);
            }

            ImGui.SameLine();
            if (UiTheme.IconButton(FontAwesomeIcon.ArrowDown, "videosync-nudge-down", new Vector2(32f, 0f), "Lower"))
            {
                this.NudgeScreenFromViewer(0f, -0.25f, 0f);
            }

            ImGui.SameLine();
            if (ImGui.Button("Closer"))
            {
                this.NudgeScreenFromViewer(0f, 0f, -0.25f);
            }

            ImGui.SameLine();
            if (ImGui.Button("Away"))
            {
                this.NudgeScreenFromViewer(0f, 0f, 0.25f);
            }

            if (this.worldScreenAnchor is { } anchor)
            {
                ImGui.TextDisabled($"Anchor: X {anchor.X:0.00}, Y {anchor.Y:0.00}, Z {anchor.Z:0.00}");
            }
            else
            {
                ImGui.TextDisabled("The screen has not been placed yet.");
            }

            ImGui.TreePop();
        }

        ImGui.Spacing();
        this.DrawCasualPictureControls();

        if (UiTheme.BeginCollapsibleSection("Advanced", warm: true))
        {
            UiTheme.SectionTitle("Compatibility");
            var useFallback = this.drawImguiWorldScreen;
            if (ImGui.Checkbox("Use the 2D fallback screen", ref useFallback))
            {
                this.drawImguiWorldScreen = useFallback;
                this.userChose2DFallback = useFallback;
                if (useFallback)
                {
                    this.worldScreenActorOcclusion = true;
                }
                else
                {
                    this.EnableNativeWorldScreen();
                }

                this.QueueSnowSyncBroadcast();
            }

            ImGui.TextDisabled("The 3D screen is the normal world object. Use 2D only if native hooks will not load.");
            if (this.drawImguiWorldScreen)
            {
                if (ImGui.Checkbox("Hide the screen behind players", ref this.worldScreenActorOcclusion))
                {
                    this.QueueSnowSyncBroadcast();
                }

                if (ImGui.SliderFloat("Cutout padding", ref this.worldScreenOcclusionPadding, 0f, 48f, "%.0f px"))
                {
                    this.QueueSnowSyncBroadcast();
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            this.DrawUpscalingControls();
            ImGui.TreePop();
        }
    }

    // Source resolution the renderer bridge renders the browser at. Higher = sharper
    // (and less work for the upscaler), but costs VRAM/GPU. The capture window is sized
    // at launch, so a change restarts the bridge if a video is currently playing.
    // The resolution dropdown itself, shared by the casual Screen tab and Advanced. A
    // full-width combo hides its own label, so callers put a heading above it.
    private void DrawResolutionCombo()
    {
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##resolution", ScreenResolutionNames[Math.Clamp(this.screenResolution, 0, ScreenResolutionNames.Length - 1)]))
        {
            for (var i = 0; i < ScreenResolutionNames.Length; i++)
            {
                var selected = this.screenResolution == i;
                if (ImGui.Selectable(ScreenResolutionNames[i], selected) && i != this.screenResolution)
                {
                    this.screenResolution = i;
                    this.config.ScreenResolution = i;
                    this.config.Save();
                    this.RestartRendererForSetting($"Resolution: {ScreenResolutionNames[i]}");
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        var (resWidth, resHeight) = this.ResolveCaptureSize();
        ImGui.TextDisabled($"Targets {resWidth} x {resHeight}, capped to your monitor's resolution.");
    }

    // Casual Picture controls for the Screen tab: a Resolution dropdown and an Enhancement
    // preset, kept as two separate controls (the preset never changes resolution).
    private void DrawCasualPictureControls()
    {
        if (UiTheme.BeginCollapsibleSection("Picture quality", warm: true))
        {
            UiTheme.SectionTitle("Resolution");
            ImGui.TextDisabled("How sharp the video is rendered. Higher = crisper; capped to your monitor.");
            ImGui.Spacing();
            this.DrawResolutionCombo();

            ImGui.Spacing();
            UiTheme.SectionTitle("Enhancement");
            ImGui.TextDisabled("One-tap picture cleanup, light to heavy. Advanced has per-effect control.");
            ImGui.Spacing();

            var current = this.DetectPicturePreset();
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##picturepreset", PicturePresetNames[current]))
            {
                // Only the real presets are selectable; "Custom" is a display-only state shown
                // when the underlying Advanced values don't match any preset.
                for (var i = 0; i < PicturePresets.Length; i++)
                {
                    var selected = current == i;
                    if (ImGui.Selectable(PicturePresetNames[i], selected))
                    {
                        this.ApplyPicturePreset(i);
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(PicturePresetHints[i]);
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.TextDisabled(PicturePresetHints[current]);

            ImGui.Spacing();
            this.DrawCompareToggle();
            ImGui.TreePop();
        }
    }

    // Maps the current upscale/deband/artifact settings to a preset index, or the "Custom"
    // slot when they don't match one (e.g. the user hand-tuned things in Advanced).
    private int DetectPicturePreset()
    {
        for (var i = 0; i < PicturePresets.Length; i++)
        {
            var (up, band, art) = PicturePresets[i];
            if (this.upscaleMode == up && this.debandMode == band && this.artifactMode == art)
            {
                return i;
            }
        }

        return PicturePresetNames.Length - 1; // Custom
    }

    private void ApplyPicturePreset(int index)
    {
        if (index < 0 || index >= PicturePresets.Length)
        {
            return; // "Custom" is not applied — it only reflects hand-tuned Advanced values.
        }

        var (up, band, art) = PicturePresets[index];
        this.upscaleMode = up;
        this.debandMode = band;
        this.artifactMode = art;
        this.config.UpscaleMode = up;
        this.config.DebandMode = band;
        this.config.ArtifactMode = art;
        this.config.Save();
        this.status = $"Enhancement: {PicturePresetNames[index]}.";
    }

    private void DrawResolutionControls()
    {
        UiTheme.SectionTitle("Resolution");
        ImGui.TextDisabled("How sharp the video is rendered before it reaches the screen. Doesn't change the screen's size in-world.");
        ImGui.Spacing();

        this.DrawResolutionCombo();

        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("Capture##capturemode", CaptureModeNames[Math.Clamp(this.captureMode, 0, CaptureModeNames.Length - 1)]))
        {
            for (var i = 0; i < CaptureModeNames.Length; i++)
            {
                var selected = this.captureMode == i;
                if (ImGui.Selectable(CaptureModeNames[i], selected) && i != this.captureMode)
                {
                    this.captureMode = i;
                    this.config.CaptureMode = i;
                    this.config.Save();
                    this.RestartRendererForSetting($"Capture mode: {CaptureModeNames[i]}");
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(CaptureModeHints[i]);
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(CaptureModeHints[Math.Clamp(this.captureMode, 0, CaptureModeHints.Length - 1)]);

        ImGui.Spacing();
        if (ImGui.Checkbox("Foreground capture (experimental)", ref this.foregroundCapture))
        {
            this.config.ForegroundCapture = this.foregroundCapture;
            this.config.Save();
            this.RestartRendererForSetting($"Foreground capture {(this.foregroundCapture ? "on" : "off")}");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Tries to lift the ~50fps cap by keeping the hidden browser composited at full\n" +
                "refresh rate (DWM cloak + top-most) instead of throttled behind the game.\n" +
                "Invisible and click-through. Experimental: if the screen goes black or misbehaves,\n" +
                "turn it off. Best paired with FFXIV in Borderless Windowed mode.");
        }

        ImGui.TextDisabled("Experimental fps fix — keeps the source composited at full rate. Turn off if the screen breaks.");
    }

    // Resolution and capture mode are both fixed when the bridge launches (capture window
    // size / browser args / frame pool), so applying either relaunches the bridge with the
    // same URL if a video is playing. Otherwise it just takes effect on the next start.
    private void RestartRendererForSetting(string label)
    {
        var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;
        if (running && !string.IsNullOrWhiteSpace(this.lastRendererUrl))
        {
            this.status = $"{label}. Restarting the screen…";
            this.StartRendererBridge(this.lastRendererUrl, this.lastRendererShareUrl);
        }
        else
        {
            this.status = $"{label}. Applies when the screen next starts.";
        }
    }

    private (int Width, int Height) ResolveCaptureSize()
    {
        var index = Math.Clamp(this.screenResolution, 0, ScreenResolutionSizes.Length - 1);
        return ScreenResolutionSizes[index];
    }

    // Post-process cleanup for compressed video: debanding (smooths gradient stair-steps) and
    // artifact cleanup (softens blocking/mosquito noise). Both off by default and applied live
    // in the shader — no bridge restart needed.
    private void DrawEnhancementControls()
    {
        UiTheme.SectionTitle("Enhancement");
        ImGui.TextDisabled("Live cleanup for compressed video (YouTube). Applies instantly — no restart. Off by default.");
        ImGui.Spacing();

        this.DrawEnhanceCombo("Debanding", "deband", ref this.debandMode, m => this.config.DebandMode = m,
            "Smooths color banding in skies, gradients and dark scenes. The standout fix — try Medium.");
        ImGui.Spacing();
        this.DrawEnhanceCombo("Artifact cleanup", "artifact", ref this.artifactMode, m => this.config.ArtifactMode = m,
            "Reduces blocky compression and mosquito noise on rough streams. Trades a little sharpness.");

        ImGui.Spacing();
        this.DrawCompareToggle();
    }

    // A/B split-view toggle. Shared by the casual Screen tab and Advanced — both flip the same
    // flag, so they stay in sync and the shader picks it up on the next frame.
    private void DrawCompareToggle()
    {
        if (ImGui.Checkbox("A/B compare (split view)", ref this.compareSplit))
        {
            this.status = this.compareSplit
                ? "Compare on: left half processed, right half raw."
                : "Compare off.";
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Splits the in-world screen: the LEFT half gets the full pipeline (upscaling +\n" +
                "enhancement), the RIGHT half is raw source, with a divider line. The seam makes\n" +
                "even subtle effects obvious. Turn up Debanding/Sharpen while this is on to see it.");
        }

        ImGui.TextDisabled("Left = processed, right = raw. Diagnostic only — turn off when done.");
    }

    private void DrawEnhanceCombo(string label, string id, ref int mode, Action<int> persist, string description)
    {
        var clamped = Math.Clamp(mode, 0, EnhanceModeNames.Length - 1);

        // Explicit label above the full-width combo: a full-width combo pushes its own label
        // off-screen, so name it here instead.
        ImGui.Text(label);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo($"##{id}", EnhanceModeNames[clamped]))
        {
            for (var i = 0; i < EnhanceModeNames.Length; i++)
            {
                var selected = clamped == i;
                if (ImGui.Selectable(EnhanceModeNames[i], selected))
                {
                    mode = i;
                    persist(i);
                    this.config.Save();
                    this.status = $"{label}: {EnhanceModeNames[i]}.";
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(description);
    }

    private (float Deband, float Artifact) ResolveEnhance()
    {
        var deband = DebandStrengths[Math.Clamp(this.debandMode, 0, DebandStrengths.Length - 1)];
        var artifact = ArtifactStrengths[Math.Clamp(this.artifactMode, 0, ArtifactStrengths.Length - 1)];
        return (deband, artifact);
    }

    // Video upscaling: a friendly quality preset, with a Custom mode that exposes the
    // raw filter + sharpen. Off keeps the screen exactly as it renders today.
    private void DrawUpscalingControls()
    {
        this.DrawResolutionControls();
        ImGui.Spacing();

        UiTheme.SectionTitle("Upscaling");
        ImGui.TextDisabled("Cleans up the picture when the screen is large or close. Off keeps the original look.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("Quality", UpscaleModeNames[Math.Clamp(this.upscaleMode, 0, UpscaleModeNames.Length - 1)]))
        {
            for (var i = 0; i < UpscaleModeNames.Length; i++)
            {
                var selected = this.upscaleMode == i;
                if (ImGui.Selectable(UpscaleModeNames[i], selected))
                {
                    this.upscaleMode = i;
                    this.config.UpscaleMode = i;
                    this.config.Save();
                    this.status = $"Upscaling: {UpscaleModeNames[i]}.";
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(UpscaleModeHints[i]);
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(UpscaleModeHints[Math.Clamp(this.upscaleMode, 0, UpscaleModeHints.Length - 1)]);

        if (this.upscaleMode == 4)
        {
            ImGui.TextColored(UiTheme.Accent, "Ultra may impact FPS.");
        }

        // Custom (mode 5) unlocks the raw filter and sharpen amount.
        if (this.upscaleMode == 5)
        {
            ImGui.Spacing();
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("Filter", UpscaleFilterNames[Math.Clamp(this.upscaleFilter, 0, UpscaleFilterNames.Length - 1)]))
            {
                for (var i = 0; i < UpscaleFilterNames.Length; i++)
                {
                    var selected = this.upscaleFilter == i;
                    if (ImGui.Selectable(UpscaleFilterNames[i], selected))
                    {
                        this.upscaleFilter = i;
                        this.config.UpscaleFilter = i;
                        this.config.Save();
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            UiTheme.PushSliderAccent();
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.SliderFloat("Sharpen", ref this.upscaleSharpness, 0f, 1f, "%.2f"))
            {
                this.upscaleSharpness = Math.Clamp(this.upscaleSharpness, 0f, 1f);
                this.config.UpscaleSharpness = this.upscaleSharpness;
                this.config.Save();
            }

            UiTheme.PopSliderAccent();
        }

        ImGui.Spacing();
        this.DrawEnhancementControls();

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Checkbox("Debug readout", ref this.upscaleDebug))
        {
            this.config.UpscaleDebugOverlay = this.upscaleDebug;
            this.config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Shows the resolved filter, sharpen amount and source resolution so you can confirm upscaling is actually doing something.");
        }

        if (this.upscaleDebug)
        {
            this.DrawUpscaleDebugReadout();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Only affects the 3D screen (not the 2D fallback).");
    }

    // Live confirmation that the upscaling settings are reaching the shader. Reads the
    // same resolved (filter, sharpen) pair the renderer gets each frame, plus the source
    // resolution the shader actually samples, so it's the ground truth — not a guess.
    private void DrawUpscaleDebugReadout()
    {
        var (filterMode, sharpen) = this.ResolveUpscale();
        var srcWidth = this.presentHookProbe.NativeSourceWidth;
        var srcHeight = this.presentHookProbe.NativeSourceHeight;
        var knownSource = srcWidth > 0 && srcHeight > 0;
        var filterName = UpscaleFilterNames[Math.Clamp(filterMode, 0, UpscaleFilterNames.Length - 1)];

        ImGui.Indent();
        ImGui.TextDisabled($"Resolved: {filterName}, sharpen {sharpen:0.00}");

        var (deband, artifact) = this.ResolveEnhance();
        ImGui.TextDisabled($"Enhance: deband {deband:0.00}, cleanup {artifact:0.00}");

        if (knownSource)
        {
            ImGui.TextDisabled($"Source: {srcWidth} x {srcHeight}");
        }
        else
        {
            ImGui.TextColored(UiTheme.Accent, "Source: unknown - filters, sharpen & enhance all skipped");
        }

        // Live capture rate from the browser. Low here (well under the video's fps) means the
        // choppiness is source-side (decode/capture); high here but still choppy is game-side.
        var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;
        if (running && this.captureFps > 0f)
        {
            ImGui.TextDisabled($"Capture: {this.captureFps:0} fps arriving");
        }
        else if (running)
        {
            ImGui.TextDisabled("Capture: measuring…");
        }

        // The shader only diverges from the "Off" look when a real source size is known
        // and either a non-bilinear filter or some sharpen is in play.
        var active = knownSource && (filterMode != 0 || sharpen > 0.001f);
        if (active)
        {
            ImGui.TextColored(UiTheme.Accent, "Status: ACTIVE (differs from Off)");
        }
        else
        {
            ImGui.TextDisabled("Status: idle (matches Off - try Ultra or Custom + Sharpen)");
        }

        ImGui.Unindent();
    }

}
